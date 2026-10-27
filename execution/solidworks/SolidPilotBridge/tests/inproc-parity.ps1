# Parity test: the SAME scripted tool session sent to two execution hosts - the out-of-process execution layer (default http://localhost:5000) and another host that serves the same REST contract
# (the in-process add-in, or tests\shell-harness.ps1) - and the responses compared after normalising what must differ (server time, process ids, absolute state_version).
# Each run starts with open_new_part and ends by closing that part WITHOUT saving, so it is repeatable and leaves the user's documents alone (a guard refuses to close anything that is not "Part*").
# SAFETY: this creates and closes documents in the LIVE SolidWorks session, and SolidWorks has crashed during such runs (2026-10-26, journal stack inside its own idle UI code).
# It therefore refuses to start without -DocumentsBackedUp: first write a SaveAs-copy backup of every dirty document (probes\probe23_backup_copy.cs through the bridge's run_cs) or run it
# right after a fresh SolidWorks start with nothing open.
# NOTE (2026-10-26): the out-of-process host crashed SolidWorks 2026 SP4.0 on this very sequence, twice, at create_sketch -> add_sketch_entities on a new part (same stack both times), while the
# in-process host ran it cleanly. So use -OnlyA against the in-process host for stability/timing runs; a two-host comparison now only makes sense for the read-only steps.
# Usage: powershell -NoProfile -File inproc-parity.ps1 -DocumentsBackedUp [-BaseA http://localhost:5001] [-BaseB http://localhost:5000] [-ShowAll]
#        powershell -NoProfile -File inproc-parity.ps1 -DocumentsBackedUp -OnlyA -Repeat 5 [-Out results.json]      (one host, N repetitions: results identical across runs? timings)
param([switch]$DocumentsBackedUp, [string]$BaseA = "http://localhost:5000", [string]$BaseB = "http://localhost:5001", [switch]$ShowAll, [switch]$OnlyA, [int]$Repeat = 1, [string]$Out = "")
if (-not $DocumentsBackedUp) { throw 'Refusing to run: this scenario creates and closes documents in the live SolidWorks session. Back up any dirty document first and pass -DocumentsBackedUp.' }

function Call([string]$Base, [string]$Method, [string]$Path, $Body) {
    $json = if ($null -ne $Body) { $Body | ConvertTo-Json -Depth 20 -Compress } else { $null }
    try {
        $p = @{ Uri = "$Base$Path"; Method = $Method; UseBasicParsing = $true; TimeoutSec = 300 }
        if ($json) { $p.Body = [Text.Encoding]::UTF8.GetBytes($json); $p.ContentType = 'application/json' }
        $r = Invoke-WebRequest @p; return @{ status = [int]$r.StatusCode; body = ($r.Content | ConvertFrom-Json) }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response; if (-not $resp) { throw }
        $txt = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd()
        return @{ status = [int]$resp.StatusCode; body = ($txt | ConvertFrom-Json) }
    }
}
function Strip($o, [int]$baseSv) {                     # normalise volatile fields so only real differences remain
    if ($null -eq $o) { return $null }
    if ($o -is [System.Management.Automation.PSCustomObject]) {
        $h = [ordered]@{}
        foreach ($p in $o.PSObject.Properties) {
            switch ($p.Name) {
                { $_ -in 'serverTimeUtc', 'solidworksPids', 'solidworksProcessCount', 'solidworksWindowless', 'bridgeLoaded', 'bridgeState', 'staTimeoutSeconds', 'inProcess', 'toolsVersion', 'toolsGeneration', 'bridgeVersion', 'http' } { continue }
                { $_ -in 'stateVersion', 'last_known_state_version' } { $h[$p.Name] = [int]$p.Value - $baseSv; continue }
                default { $h[$p.Name] = Strip $p.Value $baseSv }
            }
        }
        return [pscustomobject]$h
    }
    if ($o -is [System.Collections.IEnumerable] -and $o -isnot [string]) { return @($o | ForEach-Object { Strip $_ $baseSv }) }
    # values that legitimately differ per run: the random tag in our operation ids, and SolidWorks' running "Part<n>" numbering of new documents
    if ($o -is [string]) { return ($o -replace 'parity-[0-9a-f]{8}-', 'parity-' -replace '\bPart\d+\b', 'PartN') }
    return $o
}

$script:timing = [ordered]@{}
function Run-Scenario([string]$Base) {
    $out = [ordered]@{}; $baseSv = [int](Call $Base GET '/api/tool/state').body.stateVersion; $script:n = 0
    $script:timing = [ordered]@{}
    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    function Step([string]$name, [string]$tool, $params, [int]$sv = -1, [string]$op = '') {
        $script:n++; if (-not $op) { $op = "parity-$tag-$($script:n)" }
        if ($sv -lt 0) { $sv = [int](Call $Base GET '/api/tool/state').body.stateVersion }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $r = Call $Base POST '/api/tool/execute' @{ operationId = $op; tool = $tool; stateVersion = $sv; params = $params }
        $script:timing[$name] = $clock.ElapsedMilliseconds
        $out[$name] = @{ status = $r.status; body = (Strip $r.body $baseSv) }; return $r
    }
    $h = Call $Base GET '/health'; $out['health'] = @{ status = $h.status; body = (Strip ([pscustomobject]@{ status = $h.body.status; comAttached = $h.body.comAttached; activeDocument = $h.body.activeDocument }) $baseSv) }
    $out['bad request: no operationId'] = (Call $Base POST '/api/tool/execute' @{ tool = 'verify_state'; stateVersion = 0 })
    $out['bad request: unknown tool'] = (Call $Base POST '/api/tool/execute' @{ operationId = "parity-$tag-unknown"; tool = 'no_such_tool'; stateVersion = 0 })

    $np = Step 'open_new_part' 'open_new_part' @{}
    $doc = [string]$np.body.cadState.activeDocument
    if ($doc -notlike 'Part*') { throw "open_new_part did not activate a new part (active: '$doc') - stopping before anything else is touched" }
    [void](Step 'create_sketch' 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0.0; face_y = 0.0; face_z = 0.0 })
    $segs = @(@{ entity_type = 'line'; x1 = 0.0; y1 = 0.0; x2 = 0.1; y2 = 0.0 }, @{ entity_type = 'line'; x1 = 0.1; y1 = 0.0; x2 = 0.1; y2 = 0.06 },
              @{ entity_type = 'line'; x1 = 0.1; y1 = 0.06; x2 = 0.0; y2 = 0.06 }, @{ entity_type = 'line'; x1 = 0.0; y1 = 0.06; x2 = 0.0; y2 = 0.0 })
    [void](Step 'add_sketch_entities' 'add_sketch_entities' @{ segments = $segs })
    [void](Step 'extrude_feature' 'extrude_feature' @{ depth = 0.02; feature_type = 'boss'; angle = 360.0; axis_x1 = 0.0; axis_y1 = 0.0; axis_x2 = 0.0; axis_y2 = 0.001; path_sketch = ''; profiles = '[]'; reverse = $false; through = $false; up_to_face_index = -1; mid_plane = $false })
    foreach ($m in 'mass_properties', 'geometry', 'bodies', 'faces', 'edges', 'features') {
        [void](Step "analyze_model $m" 'analyze_model' @{ analysis_type = $m; name = ''; from_feature = ''; to_feature = ''; near = ''; k = 0; axis = '' })
    }
    [void](Step 'verify_state' 'verify_state' @{})
    $dupOp = "parity-$tag-dup"; [void](Step 'idempotency: first' 'verify_state' @{} -1 $dupOp)
    [void](Step 'idempotency: replay (same operationId)' 'verify_state' @{} -1 $dupOp)
    [void](Step 'stale state_version' 'verify_state' @{} 999999)
    # close ONLY the part this scenario created
    $act = [string](Call $Base GET '/health').body.activeDocument
    if ($act -like 'Part*') { [void](Step 'close_document' 'close_document' @{ save = $false }) } else { throw "refusing to close '$act' - it is not the part this scenario created" }
    return $out
}

if ($OnlyA) {
    # one host, N repetitions: every repetition must give the same normalised answers; the table shows the time of each step in ms
    $runs = @(); $times = @()
    for ($i = 1; $i -le $Repeat; $i++) { "=== run $i of $Repeat on $BaseA"; $runs += , (Run-Scenario $BaseA); $times += , $script:timing }
    $first = $runs[0]; $bad = 0
    for ($i = 1; $i -lt $runs.Count; $i++) {
        foreach ($k in $first.Keys) {
            $ja = $first[$k] | ConvertTo-Json -Depth 30 -Compress; $jb = $runs[$i][$k] | ConvertTo-Json -Depth 30 -Compress
            if ($ja -ne $jb) {
                $bad++; "  DIFF run $($i + 1) step '$k'"
                if ($ShowAll) { $at = 0; while ($at -lt [Math]::Min($ja.Length, $jb.Length) -and $ja[$at] -eq $jb[$at]) { $at++ }; $from = [Math]::Max(0, $at - 60)
                                "     run 1: ..." + $ja.Substring($from, [Math]::Min(200, $ja.Length - $from)); "     run $($i + 1): ..." + $jb.Substring($from, [Math]::Min(200, $jb.Length - $from)) }
            }
        }
    }
    "{0,-42}{1}" -f 'step (ms per run)', (($times | ForEach-Object -Begin { $c = 0 } -Process { $c++; "run$c".PadLeft(8) }) -join '')
    foreach ($k in $times[0].Keys) { "{0,-42}{1}" -f $k, (($times | ForEach-Object { ([string]$_[$k]).PadLeft(8) }) -join '') }
    "=== $($runs.Count) run(s), $bad step(s) differed from run 1"
    if ($Out) { $first | ConvertTo-Json -Depth 30 | Set-Content -Path $Out -Encoding UTF8; "run 1 responses written to $Out" }
    if ($bad -gt 0) { exit 1 } else { exit 0 }
}
"=== A  $BaseA"; $a = Run-Scenario $BaseA
"=== B  $BaseB"; $b = Run-Scenario $BaseB
$same = 0; $diff = 0
foreach ($k in $a.Keys) {
    $ja = $a[$k] | ConvertTo-Json -Depth 30 -Compress; $jb = if ($b.Contains($k)) { $b[$k] | ConvertTo-Json -Depth 30 -Compress } else { '<missing>' }
    if ($ja -eq $jb) { $same++; if ($ShowAll) { "  same   $k" } }
    else { $diff++; "  DIFF   $k"; "     A: " + $ja.Substring(0, [Math]::Min(700, $ja.Length)); "     B: " + $jb.Substring(0, [Math]::Min(700, $jb.Length)) }
}
"=== $same identical, $diff different, of $($a.Count) comparisons"
if ($diff -gt 0) { exit 1 }
