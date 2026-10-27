# SolidPilot Bridge - live test harness (layer 2-4 of the stress plan). Requires SolidWorks running with the bridge loaded and NO modal dialog open.
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File bridge-tests.ps1 [-Group protocol|dialogs|scripts|exec|all] [-BaseUrl http://localhost:5000] [-Json out.json]
# -BaseUrl: the REST host the 'exec' group talks to - the in-process add-in (default :5000) or the legacy out-of-process exe (SOLIDWORKS_EXEC_PORT, e.g. :5001); both must answer the same way.
# Each test prints PASS/FAIL with a reason; exit code = number of failures. The harness never leaves a dialog open (it answers its own dialogs).
param([ValidateSet('protocol', 'dialogs', 'scripts', 'exec', 'all')][string]$Group = 'all', [string]$Json = '', [string]$BaseUrl = 'http://localhost:5000')
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$probes = Join-Path (Split-Path -Parent $here) 'probes'
$script:results = New-Object System.Collections.ArrayList

function Bridge([string]$cmd, [int]$timeoutMs = 3000) {
    $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut)
    try {
        $c.Connect($timeoutMs); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c)
        $w.WriteLine($cmd); $t = $r.ReadLineAsync(); if (-not $t.Wait($timeoutMs)) { return 'TIMEOUT' }; return $t.Result
    } finally { $c.Dispose() }
}
function Test([string]$name, [scriptblock]$body) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $false; $msg = ''
    try { $r = & $body; if ($r -eq $true -or $null -eq $r) { $ok = $true } else { $msg = [string]$r } } catch { $msg = $_.Exception.Message }
    $sw.Stop(); [void]$script:results.Add([pscustomobject]@{ name = $name; pass = $ok; ms = [int]$sw.ElapsedMilliseconds; reason = $msg })
    "{0}  {1,-58} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $sw.ElapsedMilliseconds, $msg
}
function Assert($cond, [string]$why) { if (-not $cond) { throw $why } }
function ClearDialogs { for ($i = 0; $i -lt 5; $i++) { $d = Bridge 'dialogs'; if ($d -eq 'OK []') { return }; [void](Bridge 'dialog_respond OK'); Start-Sleep -Milliseconds 400 } }

if ($Group -in 'protocol', 'all') {
    "== protocol"
    Test 'ping answers with version and UI thread' { $r = Bridge 'ping'; Assert ($r -match '^OK pong v\d') "got: $r" }
    Test 'unknown command gives ERR, connection survives' { $r = Bridge 'definitely_not_a_command'; Assert ($r -like 'ERR unknown*') "got: $r"; $r2 = Bridge 'ping'; Assert ($r2 -like 'OK pong*') 'bridge dead after ERR' }
    Test 'state is valid JSON with the expected keys' { $r = Bridge 'state' 6000; Assert ($r -like 'OK {*') $r; $j = ($r.Substring(3) | ConvertFrom-Json); Assert ($null -ne $j.uiResponsive -and $null -ne $j.modalDialogs) 'missing keys' }
    Test '16 concurrent clients all get an answer' {
        $jobs = 1..16 | ForEach-Object { Start-Job { $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut); $c.Connect(5000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine('ping'); $l = $r.ReadLine(); $c.Dispose(); $l } }
        $out = $jobs | Wait-Job -Timeout 60 | Receive-Job; $jobs | Remove-Job -Force
        Assert (@($out | Where-Object { $_ -like 'OK pong*' }).Count -eq 16) ("answers: " + @($out).Count) }
    Test 'a client that connects and never sends does not block others' {
        $idle = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut); $idle.Connect(3000)
        try { $r = Bridge 'ping'; Assert ($r -like 'OK pong*') "got: $r" } finally { $idle.Dispose() } }
    Test '200 sequential pings, p95 under 50 ms' {
        $t = 1..200 | ForEach-Object { (Measure-Command { [void](Bridge 'ping') }).TotalMilliseconds } | Sort-Object; $p95 = $t[[int]($t.Count * 0.95)]; Assert ($p95 -lt 50) ("p95 = $([int]$p95) ms") }
}

if ($Group -in 'scripts', 'all') {
    "== script runner"
    Test 'hello script compiles and runs on the UI thread (STA)' { $r = Bridge ("run_cs " + (Join-Path $probes 'probe01_hello.cs')) 60000; Assert ($r -match 'apartment=STA') $r }
    Test 'compile error is reported, not thrown' {
        $f = Join-Path $env:TEMP 'bridge_bad.cs'; Set-Content $f 'public static class Script { public static string Run(SolidWorks.Interop.sldworks.SldWorks sw, string a) { return nope; } }'
        $r = Bridge "run_cs $f" 60000; Assert ($r -like 'OK COMPILE ERRORS*') $r }
    Test 'script exception is reported, SolidWorks stays responsive' {
        $f = Join-Path $env:TEMP 'bridge_throw.cs'; Set-Content $f 'public static class Script { public static string Run(SolidWorks.Interop.sldworks.SldWorks sw, string a) { throw new System.InvalidOperationException("boom"); } }'
        $r = Bridge "run_cs $f" 60000; Assert ($r -like '*SCRIPT EXCEPTION*boom*') $r; $s = Bridge 'state' 6000; Assert ($s -like '*"uiResponsive":true*') $s }
    Test 'script missing the Run method gives a clear message' {
        $f = Join-Path $env:TEMP 'bridge_norun.cs'; Set-Content $f 'public static class Script { }'; $r = Bridge "run_cs $f" 60000; Assert ($r -like '*must define*') $r }
    Test 'second run of an unchanged script is served from the compile cache (<150 ms)' {
        $p = Join-Path $probes 'probe01_hello.cs'; [void](Bridge "run_cs $p" 60000); $t = (Measure-Command { [void](Bridge "run_cs $p" 60000) }).TotalMilliseconds; Assert ($t -lt 150) "took $([int]$t) ms" }
    Test 'a long-running script hits the UI-thread timeout instead of hanging the pipe' {
        $f = Join-Path $env:TEMP 'bridge_slow.cs'; Set-Content $f 'public static class Script { public static string Run(SolidWorks.Interop.sldworks.SldWorks sw, string a) { System.Threading.Thread.Sleep(70000); return "late"; } }'
        $r = Bridge "run_cs $f" 90000; Assert ($r -like 'ERR TimeoutException*') $r
        Start-Sleep -Seconds 15; $s = Bridge 'state' 20000; Assert ($s -like '*"uiResponsive":true*') "UI thread did not recover: $s" }
}

if ($Group -in 'dialogs', 'all') {
    "== dialogs (raises harmless message boxes through the bridge itself)"
    ClearDialogs
    Test 'no dialog open at start' { $r = Bridge 'dialogs'; Assert ($r -eq 'OK []') $r }
    Test 'a raised message box is detected with its text and button' {
        $job = Start-Job { $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut); $c.Connect(5000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine('msgbox bridge-test-dialog'); $r.ReadLine(); $c.Dispose() }
        try { Start-Sleep -Milliseconds 1500; $d = Bridge 'dialogs'; Assert ($d -like '*bridge-test-dialog*') "dialogs: $d"; Assert ($d -like '*OK*') 'no OK button listed' }
        finally { ClearDialogs; $job | Wait-Job -Timeout 10 | Out-Null; $job | Remove-Job -Force } }
    Test 'dialog_respond presses OK and the dialog closes' {
        $job = Start-Job { $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut); $c.Connect(5000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine('msgbox bridge-test-dialog-2'); $r.ReadLine(); $c.Dispose() }
        Start-Sleep -Milliseconds 1500; $r = Bridge 'dialog_respond OK'; Start-Sleep -Milliseconds 1000; $d = Bridge 'dialogs'; $job | Wait-Job -Timeout 10 | Out-Null; $job | Remove-Job -Force
        Assert ($r -like 'OK clicked*') $r; Assert ($d -eq 'OK []') "still open: $d" }
    Test 'while a dialog is open: pipe still answers, state reports uiResponsive=false' {
        $job = Start-Job { $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut); $c.Connect(5000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine('msgbox bridge-test-dialog-3'); $r.ReadLine(); $c.Dispose() }
        try { Start-Sleep -Milliseconds 1500; $p = Bridge 'ping'; Assert ($p -like 'OK pong*') "ping: $p"; $s = Bridge 'state' 8000; Assert ($s -like '*bridge-test-dialog-3*') "state: $s" } finally { ClearDialogs; $job | Wait-Job -Timeout 10 | Out-Null; $job | Remove-Job -Force } }
}

if ($Group -in 'exec', 'all') {
    "== execution layer integration (REST host $BaseUrl)"
    ClearDialogs
    Test '/health reports bridgeLoaded=true and the timeout' { $h = (Invoke-WebRequest -UseBasicParsing $BaseUrl/health -TimeoutSec 30).Content | ConvertFrom-Json; Assert ($h.bridgeLoaded -eq $true) 'bridgeLoaded false'; Assert ($h.staTimeoutSeconds -gt 0) 'no timeout' }
    Test 'tool call is REFUSED with DIALOG_BLOCKING while a dialog is open, works again after' {
        $job = Start-Job { $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut); $c.Connect(5000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine('msgbox bridge-test-dialog-4'); $r.ReadLine(); $c.Dispose() }
        try {
            Start-Sleep -Milliseconds 1500
            $body = @{ operationId = [guid]::NewGuid().ToString(); tool = 'verify_state'; stateVersion = ((Invoke-WebRequest -UseBasicParsing $BaseUrl/api/tool/state -TimeoutSec 20).Content | ConvertFrom-Json).stateVersion; params = @{} } | ConvertTo-Json
            $r = (Invoke-WebRequest -UseBasicParsing -Method Post -ContentType 'application/json' -Body $body $BaseUrl/api/tool/execute -TimeoutSec 30).Content | ConvertFrom-Json
            Assert ($r.status -eq 'FAILED' -and $r.error.code -eq 'DIALOG_BLOCKING') ("response: " + ($r | ConvertTo-Json -Compress -Depth 4))
        } finally { ClearDialogs; $job | Wait-Job -Timeout 10 | Out-Null; $job | Remove-Job -Force }
        $sv = ((Invoke-WebRequest -UseBasicParsing $BaseUrl/api/tool/state -TimeoutSec 20).Content | ConvertFrom-Json).stateVersion
        $body2 = @{ operationId = [guid]::NewGuid().ToString(); tool = 'verify_state'; stateVersion = $sv; params = @{} } | ConvertTo-Json
        $r2 = (Invoke-WebRequest -UseBasicParsing -Method Post -ContentType 'application/json' -Body $body2 $BaseUrl/api/tool/execute -TimeoutSec 60).Content | ConvertFrom-Json
        Assert (-not ($r2.status -eq 'FAILED' -and $r2.error.code -in 'DIALOG_BLOCKING', 'SW_BUSY', 'STA_TIMEOUT')) ("after clearing: " + ($r2 | ConvertTo-Json -Compress -Depth 4)) }
}

""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
"{0} tests, {1} passed, {2} failed" -f $script:results.Count, ($script:results.Count - $fail), $fail
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
