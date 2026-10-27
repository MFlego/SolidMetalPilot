# SolidPilot in-process host - stress, security and fault-injection tests (round 2). Needs SolidWorks running with the bridge loaded, NOTHING else open in it, and a DEBUG tools build
# (the __test_* hooks - sleep / throw / echo - exist only there). Every test restores what it changed (pause, call timeout, snapshot policy).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File inproc-stress.ps1 [-Group rest|gate|forensics|events|reload|all] [-BaseUrl http://localhost:5000] [-Json out.json]
param([ValidateSet('rest', 'gate', 'forensics', 'events', 'reload', 'all')][string]$Group = 'all', [string]$BaseUrl = 'http://localhost:5000', [string]$Json = '')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\probes\_common.ps1"
Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @"
using System; using System.Collections.Generic; using System.Diagnostics; using System.Net.Http; using System.Text; using System.Threading; using System.Threading.Tasks;
public static class StressHttp {
  public sealed class R { public int Status; public long Ms; public string Body; public string Error; }
  static R Do(HttpClient http, string method, string url, string body, string contentType, string[] headers) {
    var r = new R(); var sw = Stopwatch.StartNew();
    try {
      var req = new HttpRequestMessage(new HttpMethod(method), url);
      bool noBody = body == null || (body.Length == 0 && (method == "GET" || method == "HEAD" || method == "DELETE"));
      if (!noBody) req.Content = new StringContent(body, Encoding.UTF8, string.IsNullOrEmpty(contentType) ? "text/plain" : contentType);
      if (headers != null) for (int i = 0; i + 1 < headers.Length; i += 2) { if (headers[i] == "Host") req.Headers.Host = headers[i + 1]; else req.Headers.TryAddWithoutValidation(headers[i], headers[i + 1]); }
      var resp = http.SendAsync(req).GetAwaiter().GetResult(); r.Status = (int)resp.StatusCode; r.Body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    } catch (Exception e) { r.Error = e.GetType().Name + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message); }
    r.Ms = sw.ElapsedMilliseconds; return r;
  }
  public static R Send(string method, string url, string body, string contentType, string[] headers, int timeoutSec) {
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) }) return Do(http, method, url, body, contentType, headers);
  }
  public static R[] Parallel(string method, string url, string[] bodies, int timeoutSec) {
    // the CLIENT's thread pool ramps up by one thread per 0.5 s above its minimum: without this, 40 blocking requests took 27 s here while the server needed 150 ms in total
    int w, io; ThreadPool.GetMinThreads(out w, out io); if (w < bodies.Length + 8) ThreadPool.SetMinThreads(bodies.Length + 8, io);
    var res = new R[bodies.Length]; var tasks = new Task[bodies.Length];
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) }) {
      for (int i = 0; i < bodies.Length; i++) { int k = i; tasks[i] = Task.Run(() => { res[k] = Do(http, method, url, bodies[k], "application/json", null); }); }
      Task.WaitAll(tasks);
    }
    return res;
  }
}
"@
$script:results = New-Object System.Collections.ArrayList
function Test([string]$Name, [scriptblock]$Body) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $true; $why = ''
    try { & $Body } catch { $ok = $false; $why = $_.Exception.Message }
    "{0}  {1,-72} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.ElapsedMilliseconds, $why
    [void]$script:results.Add([pscustomobject]@{ name = $Name; pass = $ok; ms = $sw.ElapsedMilliseconds; why = $why })
}
function Assert($cond, [string]$msg) { if (-not $cond) { throw $msg } }
function Raw([string]$Method, [string]$Path, $Body = $null, [string]$ContentType = 'application/json', [string[]]$Headers = @(), [int]$TimeoutSec = 120) {
    $b = if ($null -eq $Body) { $null } elseif ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 40 -Compress }
    [StressHttp]::Send($Method, "$BaseUrl$Path", $b, $ContentType, $Headers, $TimeoutSec)
}
function ToolBody([string]$Tool, $Params = @{}, [int]$Sv = 0, [string]$Op = '') { if (-not $Op) { $Op = [guid]::NewGuid().ToString('N') }; @{ operationId = $Op; tool = $Tool; stateVersion = $Sv; params = $Params } | ConvertTo-Json -Depth 40 -Compress }
function Tool([string]$Tool, $Params = @{}, [int]$Sv = -1, [string]$Op = '') {       # returns the parsed reply plus .http; retries the cold-start SW_BUSY of the gate (UI thread still busy after a document operation)
    for ($i = 0; $i -lt 4; $i++) {
        if ($Sv -lt 0) { $sv = [int]((Raw 'GET' '/api/tool/state').Body | ConvertFrom-Json).stateVersion } else { $sv = $Sv }
        $r = Raw 'POST' '/api/tool/execute' (ToolBody $Tool $Params $sv $Op)
        $o = if ($r.Body) { $r.Body | ConvertFrom-Json } else { [pscustomobject]@{} }; $o | Add-Member -NotePropertyName http -NotePropertyValue $r.Status -Force
        if (-not ($o.status -eq 'FAILED' -and $o.error.code -eq 'SW_BUSY' -and $o.error.message -like "*UI thread did not answer*")) { return $o }
        Start-Sleep 2
    }
    return $o
}
function Hook([string]$Tool, $Params = @{}) { Tool $Tool $Params 0 }
function SwProc { Get-Process SLDWORKS | Select-Object -First 1 }
function ActivityCalls { ((Bridge 'activity 200') | ConvertFrom-Json).calls }
function Bridge([string]$cmd, [int]$to = 8000) { Invoke-Bridge $cmd $to }
function BridgeRaw([string]$cmd, [int]$to = 8000) { $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut); try { $c.Connect($to); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine($cmd); $x = $r.ReadLineAsync(); if (-not $x.Wait($to)) { return 'TIMEOUT' }; return $x.Result } finally { $c.Dispose() } }
function Clean { Bridge 'resume' | Out-Null; Bridge 'gate_timeout reset' | Out-Null; Bridge 'snapshot_policy 120' | Out-Null }
$h0 = (SwProc).HandleCount; $m0 = [int]((SwProc).PrivateMemorySize64 / 1MB); $pid0 = (SwProc).Id
Clean

if ($Group -in 'rest', 'all') {
    "== rest: protocol, security, payloads, concurrency  ($BaseUrl)"
    Test 'R01 unknown path -> 404' { $r = Raw 'GET' '/nope'; Assert ($r.Status -eq 404) "status $($r.Status)" }
    Test 'R02 wrong method -> 405' { $r = Raw 'POST' '/health' '{}'; Assert ($r.Status -eq 405) "status $($r.Status)" }
    Test 'R03 empty body -> 400 operation_id and tool are required' { $r = Raw 'POST' '/api/tool/execute' ''; Assert ($r.Status -eq 400 -and $r.Body -like '*operation_id and tool are required*') "$($r.Status) $($r.Body)" }
    Test 'R04 malformed JSON -> 400, host unharmed' { $r = Raw 'POST' '/api/tool/execute' '{not json'; Assert ($r.Status -eq 400 -and $r.Body -like '*malformed JSON*') "$($r.Status) $($r.Body)"; Assert ((Hook '__test_echo' @{a = 1}).status -eq 'COMPLETED') 'host unhealthy after bad JSON' }
    Test 'R05 empty object -> 400' { $r = Raw 'POST' '/api/tool/execute' '{}'; Assert ($r.Status -eq 400) "status $($r.Status)" }
    Test 'R06 unknown tool -> 400 Unknown tool: nope' { $r = Raw 'POST' '/api/tool/execute' (ToolBody 'nope'); Assert ($r.Status -eq 400 -and $r.Body -like '*Unknown tool: nope*') "$($r.Status) $($r.Body)" }
    Test 'R07 POST with a non-JSON content type -> 415' { $r = Raw 'POST' '/api/tool/execute' (ToolBody '__test_echo') ''; Assert ($r.Status -eq 415) "status $($r.Status)" }
    Test 'R08 request carrying an Origin header (browser / CSRF) -> 403' { $r = Raw 'POST' '/api/tool/execute' (ToolBody '__test_echo') 'application/json' @('Origin', 'http://evil.example'); Assert ($r.Status -eq 403) "status $($r.Status)" }
    Test 'R09 foreign Host header (DNS rebinding) is refused' { $r = Raw 'GET' '/health' $null 'application/json' @('Host', 'evil.example:5000'); Assert ($r.Status -in 400, 403 -or $r.Error) "status $($r.Status) $($r.Error)" }
    Test 'R10 unicode survives the round trip (accents, CJK, emoji)' {
        $txt = "aeoeaa " + [char]0x00E6 + [char]0x00F8 + [char]0x00E5 + " " + [char]0x65E5 + [char]0x672C + [char]0x8A9E + " " + [char]::ConvertFromUtf32(0x1F600) + " tab`there"
        $o = Hook '__test_echo' @{ text = $txt }; Assert ($o.result_geometry.text -ceq $txt) "echo differs: $($o.result_geometry.text)" }
    Test 'R11 3 MB payload round trip' { $big = 'x' * 3000000; $o = Hook '__test_echo' @{ blob = $big }; Assert ($o.result_geometry.blob.Length -eq 3000000) "length $($o.result_geometry.blob.Length)" }
    Test 'R12 JSON nested 100 deep -> 400 (reader depth limit), host unharmed' {
        $deep = ('{"a":' * 100) + '1' + ('}' * 100); $r = Raw 'POST' '/api/tool/execute' "{`"operationId`":`"d1`",`"tool`":`"__test_echo`",`"stateVersion`":0,`"params`":$deep}"
        Assert ($r.Status -in 400, 500) "status $($r.Status)"; Assert ((Hook '__test_echo').status -eq 'COMPLETED') 'host unhealthy' }
    Test 'R13 40 parallel /health all answer 200' { $rs = [StressHttp]::Parallel('GET', "$BaseUrl/health", (1..40 | ForEach-Object { $null }), 60); $bad = @($rs | Where-Object { $_.Status -ne 200 }); Assert ($bad.Count -eq 0) "$($bad.Count) failed: $($bad[0].Error) $($bad[0].Status)" }
    Test 'R14 16 parallel tool posts are serialised and all COMPLETE' {
        $bodies = 1..16 | ForEach-Object { ToolBody '__test_sleep' @{ ms = 150 } 0 }; $t = [Diagnostics.Stopwatch]::StartNew()
        $rs = [StressHttp]::Parallel('POST', "$BaseUrl/api/tool/execute", $bodies, 120); $ok = @($rs | Where-Object { $_.Status -eq 200 -and $_.Body -like '*"status":"COMPLETED"*' })
        Assert ($ok.Count -eq 16) "$($ok.Count)/16 completed: $(($rs | Where-Object { $_.Status -ne 200 } | Select-Object -First 1).Body)"; Assert ($t.ElapsedMilliseconds -ge 2200) "finished in $($t.ElapsedMilliseconds) ms - the 16 x 150 ms sleeps overlapped (must be serial)" }
}

if ($Group -in 'gate', 'all') {
    "== gate: timeout, abandon / recover, exceptions, pause, queueing"
    Test 'G01 STA_TIMEOUT -> SW_BUSY while abandoned -> recovers by itself' {
        try {
            Bridge 'gate_timeout 3' | Out-Null; $t = [Diagnostics.Stopwatch]::StartNew()
            $a = Hook '__test_sleep' @{ ms = 6500 }
            Assert ($a.status -eq 'FAILED' -and $a.error.code -eq 'STA_TIMEOUT') "first call: $($a | ConvertTo-Json -Compress -Depth 4)"; Assert ($t.ElapsedMilliseconds -lt 5500) "timeout took $($t.ElapsedMilliseconds) ms (should be ~3 s)"
            $b = Hook '__test_echo'; Assert ($b.status -eq 'FAILED' -and $b.error.code -eq 'SW_BUSY') "while abandoned: $($b | ConvertTo-Json -Compress -Depth 4)"
            Start-Sleep 5; $c = Hook '__test_echo'; Assert ($c.status -eq 'COMPLETED') "after the sleep ended: $($c | ConvertTo-Json -Compress -Depth 4)"
        } finally { Clean } }
    Test 'G02 exception inside a tool -> HTTP 500, gate stays healthy' { $r = Raw 'POST' '/api/tool/execute' (ToolBody '__test_throw' @{ message = 'boom from a tool' } 0); Assert ($r.Status -eq 500 -and $r.Body -like '*boom from a tool*') "$($r.Status) $($r.Body)"; Assert ((Hook '__test_echo').status -eq 'COMPLETED') 'unhealthy afterwards' }
    Test 'G03 exception escaping the host -> gate maps it to HTTP 500 and recovers' { $r = Raw 'POST' '/api/tool/execute' (ToolBody '__test_throw_escape' @{} 0); Assert ($r.Status -eq 500 -and $r.Body -like '*STA execution failed*') "$($r.Status) $($r.Body)"; Assert ((Hook '__test_echo').status -eq 'COMPLETED') 'unhealthy afterwards' }
    Test 'G04 pause refuses tool calls (USER_PAUSED), /health reports it, resume restores' {
        try {
            Bridge 'pause testing the stop switch' | Out-Null; $o = Hook '__test_echo'
            Assert ($o.status -eq 'FAILED' -and $o.error.code -eq 'USER_PAUSED') "paused call: $($o | ConvertTo-Json -Compress -Depth 4)"
            $h = (Raw 'GET' '/health').Body | ConvertFrom-Json; Assert ($h.paused -like '*stop switch*') "health.paused = $($h.paused)"; Assert ($h.status -eq 'UP') 'health must keep answering while paused'
            Bridge 'resume' | Out-Null; Assert ((Hook '__test_echo').status -eq 'COMPLETED') 'resume did not restore'
        } finally { Clean } }
    Test 'G05 four parallel 800 ms calls run ONE AT A TIME (>= 3.0 s) and all complete' {
        $t = [Diagnostics.Stopwatch]::StartNew(); $rs = [StressHttp]::Parallel('POST', "$BaseUrl/api/tool/execute", (1..4 | ForEach-Object { ToolBody '__test_sleep' @{ ms = 800 } 0 }), 120)
        Assert (@($rs | Where-Object { $_.Body -like '*"status":"COMPLETED"*' }).Count -eq 4) 'not all completed'; Assert ($t.ElapsedMilliseconds -ge 3000) "only $($t.ElapsedMilliseconds) ms: the calls overlapped" }
    Test 'G06 the activity log records tool, result and time of those calls' {
        $c = ActivityCalls; Assert (@($c).Count -ge 10) "only $(@($c).Count) entries"; $s = @($c | Where-Object { $_.tool -eq '__test_sleep' -and $_.result -eq 'COMPLETED' }); Assert ($s.Count -ge 4 -and ($s | Select-Object -Last 1).ms -ge 700) 'sleep calls missing or too fast in the log'
        Assert (@($c | Where-Object { $_.result -like 'FAILED:STA_TIMEOUT' }).Count -ge 1) 'the STA_TIMEOUT call is not in the log' }
}

if ($Group -in 'forensics', 'all') {
    "== forensics and the safety net"
    Test 'F01 crash marker exists WHILE a call runs and is gone after it' {
        $f = "$env:LOCALAPPDATA\SolidPilot\inflight.txt"; Remove-Item $f -ErrorAction SilentlyContinue
        $job = Start-Job { param($u, $b) Invoke-WebRequest -UseBasicParsing -Method Post -ContentType 'application/json' -Body $b "$u/api/tool/execute" -TimeoutSec 60 | Out-Null } -ArgumentList $BaseUrl, (ToolBody '__test_sleep' @{ ms = 3000 } 0)
        try { Start-Sleep -Milliseconds 1500; Assert (Test-Path $f) 'no marker during the call'; Assert ((Get-Content $f -Raw) -like '*__test_sleep*') 'marker does not name the tool' } finally { $job | Wait-Job -Timeout 30 | Out-Null; $job | Remove-Job -Force }
        Assert (-not (Test-Path $f)) 'marker still there after the call ended' }
    Test 'F02 snapshot_now writes a copy of the dirty document; policy toggles' {
        try {
            $b = Tool 'open_new_part'; Assert ($b.status -eq 'COMPLETED') 'open_new_part'
            $s = Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }; Assert ($s.status -eq 'COMPLETED') "create_sketch $($s.error.message)"
            $segs = @(@{ entity_type = 'line'; x1 = 0; y1 = 0; x2 = 0.05; y2 = 0 }, @{ entity_type = 'line'; x1 = 0.05; y1 = 0; x2 = 0.05; y2 = 0.03 }, @{ entity_type = 'line'; x1 = 0.05; y1 = 0.03; x2 = 0; y2 = 0.03 }, @{ entity_type = 'line'; x1 = 0; y1 = 0.03; x2 = 0; y2 = 0 })
            Assert ((Tool 'add_sketch_entities' @{ segments = $segs }).status -eq 'COMPLETED') 'add_sketch_entities'
            $e = Tool 'extrude_feature' @{ depth = 0.01; feature_type = 'boss'; angle = 360; axis_x1 = 0; axis_y1 = 0; axis_x2 = 0; axis_y2 = 0.001; path_sketch = ''; profiles = '[]'; reverse = $false; through = $false; up_to_face_index = -1; mid_plane = $true }
            Assert ($e.status -eq 'COMPLETED') "extrude $($e.error.message)"
            Start-Sleep 1; $r = Bridge 'snapshot_now' 60000; Assert ($r -like 'saved *.SLDPRT*') "snapshot_now said: $r"
            $path = ($r -replace '^saved ', '' -replace ' in \d+ ms.*$', ''); Assert ((Test-Path $path) -and (Get-Item $path).Length -gt 10000) "file missing or tiny: $path"
            Assert ((Bridge 'snapshot_policy off') -like '*"intervalSeconds":0*') 'policy off'; Assert ((Bridge 'snapshot_policy 90') -like '*"intervalSeconds":90*') 'policy 90'
        } finally { Clean; Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null } }
    Test 'F03 SolidWorks is the same process and responsive after all of this' { $p = SwProc; Assert ($p.Id -eq $pid0) "SolidWorks restarted (was $pid0, now $($p.Id))"; Assert $p.Responding 'not responding' }
}

if ($Group -in 'events', 'all') {
    "== events: what the in-process event stream sees"
    Test 'E01 a new part produces file-new and active-doc-change events' {
        try {
            $before = [int](((Bridge 'events 0') -split '## total=')[1]); $o = Tool 'open_new_part'; Assert ($o.status -eq 'COMPLETED') 'open_new_part'; Start-Sleep -Milliseconds 800
            $ev = Bridge "events $before"; Assert ($ev -like '*file-new*') "no file-new in: $ev"; Assert ($ev -like '*active-doc-change*') "no active-doc-change in: $ev"
        } finally { Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null } }
    Test 'E02 creating a sketch on the Front Plane raises a typed selection event' {
        try {
            Assert ((Tool 'open_new_part').status -eq 'COMPLETED') 'open_new_part'; $before = [int](((Bridge 'events 0') -split '## total=')[1])
            Assert ((Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }).status -eq 'COMPLETED') 'create_sketch'; Start-Sleep -Milliseconds 800
            $ev = Bridge "events $before"; Assert ($ev -like '*selection-new*Front Plane*') "no typed selection event: $ev"
        } finally { Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null } }
}

if ($Group -in 'reload', 'all') {
    "== reload: hot-swapping the tools under load"
    Test 'L01 reload_tools is refused while a tool call is running' {
        $job = Start-Job { param($u, $b) Invoke-WebRequest -UseBasicParsing -Method Post -ContentType 'application/json' -Body $b "$u/api/tool/execute" -TimeoutSec 60 | Out-Null } -ArgumentList $BaseUrl, (ToolBody '__test_sleep' @{ ms = 3500 } 0)
        try { Start-Sleep -Milliseconds 1200; $r = BridgeRaw 'reload_tools' 8000; Assert ($r -like 'ERR*running*') "expected a refusal, got: $r" } finally { $job | Wait-Job -Timeout 30 | Out-Null; $job | Remove-Job -Force } }
    Test 'L02 reload keeps state_version and the idempotency memory, and tools keep working' {
        $gen0 = ((Bridge 'tools_info') | ConvertFrom-Json).tools.generation; $op = [guid]::NewGuid().ToString('N')
        $a = Tool 'open_new_part' @{} -1 $op; Assert ($a.status -eq 'COMPLETED') 'open_new_part'; $sv1 = [int]((Raw 'GET' '/api/tool/state').Body | ConvertFrom-Json).stateVersion
        $r = Bridge 'reload_tools' 20000; Assert ($r -like 'tools generation*loaded*') "reload said: $r"
        $gen1 = ((Bridge 'tools_info') | ConvertFrom-Json).tools.generation; Assert ($gen1 -eq $gen0 + 1) "generation $gen0 -> $gen1"
        $sv2 = [int]((Raw 'GET' '/api/tool/state').Body | ConvertFrom-Json).stateVersion; Assert ($sv2 -eq $sv1) "state_version changed across the reload: $sv1 -> $sv2"
        $d = Tool 'open_new_part' @{} $sv2 $op; Assert ($d.status -eq 'DUPLICATE') "replayed operation id gave $($d.status) - the idempotency memory was lost"
        $b = Tool 'close_document' @{ save = $false; close_all = $true }; Assert ($b.status -eq 'COMPLETED') "tools broken after reload: $($b.error.message)" }
    Test 'L03 six more reloads: SolidWorks memory stays flat (each generation is a few hundred KB)' {
        $m = [int]((SwProc).PrivateMemorySize64 / 1MB); 1..6 | ForEach-Object { $r = Bridge 'reload_tools' 20000; Assert ($r -like 'tools generation*') "reload $_ : $r" }
        $m2 = [int]((SwProc).PrivateMemorySize64 / 1MB); Assert (($m2 - $m) -lt 60) "private memory grew $m -> $m2 MB over 6 reloads"; Assert ((Hook '__test_echo').status -eq 'COMPLETED') 'tools broken after the reloads' }
}

Clean
"" ; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (SwProc).HandleCount; $m1 = [int]((SwProc).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
