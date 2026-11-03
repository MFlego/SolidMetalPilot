# Regression tests for Convert to Sheet Metal driven from INSIDE SolidWorks (sheet_metal_convert + ui_inspect) and the surface-preparation tools it needs (delete_face, offset_surface).
# What they prove: the PropertyManager (a WPF control in the SolidWorks process) is driven correctly - every setting reaches the created feature, the sizes and the flat blank match the CLOSED-FORM
# sheet-metal maths, the selection ORDER of the request is the order of the picks (it matters in Convert), requests that cannot work are refused before anything is opened, nothing is ever left open,
# a dialog raised inside a click is answered by the watchdog, an abandoned call cancels the PropertyManager and stops.
# Needs SolidWorks running with the bridge (shell >= 0.9), NOTHING else open (every document is closed at the end), the in-process tools loaded.
# Usage: convert-tests.ps1 [-Force] [-Json out.json] [-Only 'C03'] [-Cover]      (-Only runs the tests whose name matches the regex; every test builds its own part, so any one can run alone;
# -Cover also runs C15, which puts a small top-most window over SolidWorks' graphics area for a few seconds to prove that a window of another application is never clicked)
param([switch]$Force, [string]$Json = '', [string]$Only = '', [switch]$Cover)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\probes\_common.ps1"
. "$PSScriptRoot\..\probes\_tools.ps1"
. "$PSScriptRoot\sheetmetal-fixtures.ps1"
$script:results = New-Object System.Collections.ArrayList
$Probes = "$PSScriptRoot\..\probes"
function Test([string]$Name, [scriptblock]$Body) {
    if ($Only -and $Name -notmatch $Only) { return }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $true; $why = ''
    try { & $Body } catch { $ok = $false; $why = $_.Exception.Message }
    "{0}  {1,-100} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.ElapsedMilliseconds, $why
    [void]$script:results.Add([pscustomobject]@{ name = $Name; pass = $ok; ms = $sw.ElapsedMilliseconds; why = $why })
}
function Bodies { (Invoke-Probe 'probe54_bodies.cs') -split "`n" | Where-Object { $_ -match 'type=' } }
function ToolBusy { (Invoke-Bridge 'tools_info' 5000) -like '*"toolBusy":true*' }
function RestoreSnapshots([int]$seconds) { [void](Invoke-Bridge $(if ($seconds -gt 0) { "snapshot_policy $seconds" } else { 'snapshot_policy off' }) 5000) }

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== Convert to Sheet Metal from inside SolidWorks  ($script:ToolBase)"

Test 'C00 surface preparation: delete_face makes a surface body; offset_surface goes inward / outward by exactly the distance; bad input is refused' {
    Tray
    $o = Rg (Ok (Invoke-Tool 'offset_surface' @{ body_point = '[0,0,0]'; distance = 0.0015; direction = 'inward' }) 'offset inward')
    Assert ($o.verified -eq $true -and $o.hidden_original -eq $true) "offset: $($o | ConvertTo-Json -Compress -Depth 4)"
    Near $o.size_change[0] -0.003 1e-7 'x shrinks by 2 d'; Near $o.size_change[1] -0.003 1e-7 'y shrinks by 2 d'; Near $o.size_change[2] -0.0015 1e-7 'z shrinks by d (the front is open)'
    Tray; $o2 = Rg (Ok (Invoke-Tool 'offset_surface' @{ body_point = '[0,0,0]'; distance = 0.0015; direction = 'outward' }) 'offset outward')
    Near $o2.size_change[0] 0.003 1e-7 'x grows by 2 d'; Near $o2.size_change[2] 0.0015 1e-7 'z grows by d'
    $names = (Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'features')).features | ForEach-Object { $_.name }; Assert (@($names | Where-Object { $_ -like 'Surface-Offset*' }).Count -eq 1) "exactly one offset feature must remain (a corrected attempt is deleted): $($names -join ',')"
    Fail (Invoke-Tool 'delete_face' @{ faces = '[[5,5,5]]' }) 'FACE_NOT_FOUND' 'face far away' | Out-Null; Fail (Invoke-Tool 'delete_face' @{}) 'MISSING_PARAMETER' 'no faces' | Out-Null
    Fail (Invoke-Tool 'offset_surface' @{ body_point = '[0,0,0]'; distance = 0 }) 'INVALID_PARAMETER' 'distance 0' | Out-Null; Fail (Invoke-Tool 'offset_surface' @{ body_point = '[9,9,9]'; distance = 0.001 }) 'FACE_NOT_FOUND' 'point off the body' | Out-Null
    Fail (Invoke-Tool 'offset_surface' @{ body_point = '[0,0,0]'; distance = 0.001; direction = 'sideways' }) 'INVALID_PARAMETER' 'direction' | Out-Null
}

Test 'C01 a tray converts in-process: 4 bends, PropertyManager closed, folded size = surface + t on every side (reverse thickness), flat blank = CLOSED FORM' {
    Tray; $sv = StateVer
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572; reverse_thickness = 'on' }) 'convert')
    Assert ($r.verified -eq $true -and $r.accepted -eq $true -and -not $r.problems) "result: $($r | ConvertTo-Json -Compress -Depth 4)"
    Assert ($r.lists.bend_edges -eq 4 -and $r.lists.rip_sketches -eq 0 -and $r.lists.rip_edges_found -ge 4) "lists: $($r.lists | ConvertTo-Json -Compress)"
    Assert ((StateVer) -eq $sv + 1) 'state_version must rise by one'; Assert ((PmWindows) -eq 0) 'the PropertyManager must be closed'
    Assert (@(Converts).Count -eq 1) 'one Convert feature expected'
    $p = Params; $c = $p.cut_list[0].properties; Assert ([int]$c.Bends -eq 4) "bends: $($c.Bends)"
    $x = Extent 0; $y = Extent 1; $z = Extent 2
    Near (Mm ($x[1] - $x[0])) 103.048 1e-3 'folded X (100 + 2 t)'; Near (Mm ($y[1] - $y[0])) 63.048 1e-3 'folded Y'; Near (Mm ($z[1] - $z[0])) 31.524 1e-3 'folded Z (30 + t: the front is open)'
    $f = Flat; $cf = TrayBlank 100 60 30 1.524 4.572 0.5 $true; Near (Mm $f.flat.size[0]) $cf[0] 2e-3 'flat X = W + 2 (BA + D - 2 r)'; Near (Mm $f.flat.size[1]) $cf[1] 2e-3 'flat Y'
    Assert (@($r.picks).Count -eq 4 -and (@($r.picks | Where-Object { $_.clearance_px -lt 4 }).Count -eq 0)) 'every pick must have been made well clear of other edges'
}

Test 'C02 every setting reaches the feature: thickness, radius, reverse OFF, overlap corner, rip gap, overlap ratio, tear relief; outer-skin closed form; K-factor applied' {
    Tray
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.002; bend_radius = 0.003; reverse_thickness = 'off'; corner = 'overlap'; rip_gap = 0.0001; overlap_ratio = 1; relief = 'tear'; k_factor = 0.45 }) 'convert with settings')
    Assert ($r.verified -eq $true) "problems: $($r.problems -join ' | ')"
    $d = $r.definition; Near $d.SheetThickness 0.002 1e-9 'thickness'; Near $d.BendRadius 0.003 1e-9 'radius'; Assert ($d.CornerDefaults -eq 1) 'overlap corner'; Near $d.RipGap 0.0001 1e-9 'rip gap'; Near $d.RipOverlapRatio 1.0 1e-9 'overlap ratio'; Assert ($d.ReliefType -eq 2) 'tear relief'; Assert ($d.ReverseThickness -eq $false) 'reverse off'
    $p = Params; Near $p.global.bend_allowance.k_factor 0.45 1e-9 'global K'; Assert ($p.global.overrides.bend_allowance -eq $true) 'the K override is on'
    $x = Extent 0; $y = Extent 1; $z = Extent 2
    Near (Mm ($x[1] - $x[0])) 100 1e-3 'folded X (the surface IS the outer skin)'; Near (Mm ($y[1] - $y[0])) 60 1e-3 'folded Y'; Near (Mm ($z[1] - $z[0])) 30 1e-3 'folded Z'
    $f = Flat; $cf = TrayBlank 100 60 30 2 3 0.45 $false; Near (Mm $f.flat.size[0]) $cf[0] 2e-3 'flat X = W + 2 (BA + D - 2 (r + t)) with K 0.45'; Near (Mm $f.flat.size[1]) $cf[1] 2e-3 'flat Y'
}

Test 'C03 THE v4 RECIPE at v4 scale: 12 bends + 4 rip sketches picked in the requested ORDER (back plate, rips, rim outer, lip root), differential rim 1.00 / 0.75 in, size and blank in closed form' {
    $names = RimFixture; $steps = RimSteps $names
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' (Merge @{ fixed_face = '[0,0,0]'; steps = $steps } $V4)) 'convert v4')
    Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    Assert ($r.lists.bend_edges -eq 12 -and $r.lists.rip_sketches -eq 4 -and $r.lists.rip_edges_found -ge 4) "lists: $($r.lists | ConvertTo-Json -Compress)"
    $want = @(1..4 | ForEach-Object { "bend $_" }) + @($names | ForEach-Object { "rip $_" }) + @(5..12 | ForEach-Object { "bend $_" })
    $got = @($r.picks | ForEach-Object { $_.target }); Assert (($got -join ',') -eq ($want -join ',')) "pick order: got [$($got -join ', ')] expected [$($want -join ', ')]"
    $lists = @($r.picks | ForEach-Object { $_.list }); $wantLists = @(1..4 | ForEach-Object { 'BendList' }) + @(1..4 | ForEach-Object { 'RipSketches' }) + @(1..8 | ForEach-Object { 'BendList' })
    Assert (($lists -join ',') -eq ($wantLists -join ',')) "list order: $($lists -join ',')"
    $d = $r.definition; Assert ($d.CornerDefaults -eq 1 -and $d.ReliefType -eq 2 -and $d.ReverseThickness -eq $true) 'v4 settings'; Near $d.RipOverlapRatio 1.0 1e-9 'overlap ratio 1'; Near $d.RipGap 0.00003 1e-9 'rip gap 0.03 mm'
    $p = Params; $c = $p.cut_list[0].properties; Assert ([int]$c.Bends -eq 12) "bends: $($c.Bends)"; Near $p.global.bend_allowance.k_factor 0.45 1e-9 'K 0.45'
    $x = Extent 0; $y = Extent 1; $z = Extent 2          # planar faces: exact (the body's bounding box is conservative by ~0.004 mm per side where bends reach the outside)
    Near (Mm $x[0]) -153.924 1e-3 'x min = -(152.4 + t)'; Near (Mm $x[1]) 153.924 1e-3 'x max'; Near (Mm $y[0]) -230.124 1e-3 'y min = -(228.6 + t)'; Near (Mm $y[1]) 230.124 1e-3 'y max'
    Near (Mm $z[0]) -1.524 1e-3 'z min = -t (the back plate)'; Near (Mm $z[1]) 95.25 1e-3 'z max = 3.25 in + 0.5 in lip'
    $f = Flat; $cf = RimBlank 0.3048 0.4572 0.08255 0.0254 0.01905 0.0127 0.001524 0.004572 0.45
    Near (Mm $f.flat.size[0]) (Mm $cf[0]) 2e-3 'flat X: rim 1.00 in'; Near (Mm $f.flat.size[1]) (Mm $cf[1]) 2e-3 'flat Y: rim 0.75 in (the DIFFERENTIAL flange)'
    Assert ((PmWindows) -eq 0) 'the PropertyManager must be closed'
}

Test 'C04 the ORDER is the CALLER''s: the order inside each bend series is rotated, the picks follow it exactly (by model position) and the part comes out identical' {
    $names = RimFixture; $steps = RimSteps $names 2; $stepObj = $steps | ConvertFrom-Json
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' (Merge @{ fixed_face = '[0,0,0]'; steps = $steps } $V4)) 'convert v4, rotated series')
    Assert ($r.verified -eq $true) "problems: $($r.problems -join ' | ')"
    $wantMids = @(); foreach ($s in $stepObj) { if ($s.list -eq 'bend') { foreach ($e in $s.edges) { $wantMids += , @($e) } } }
    $gotMids = @($r.picks | Where-Object { $_.list -eq 'BendList' } | ForEach-Object { , @($_.mid) })
    Assert ($gotMids.Count -eq 12) "bend picks: $($gotMids.Count)"
    for ($i = 0; $i -lt 12; $i++) { for ($k = 0; $k -lt 3; $k++) { Near $gotMids[$i][$k] $wantMids[$i][$k] 1e-3 "bend pick $($i + 1) coordinate $k (the request's order)" } }
    $f = Flat; $cf = RimBlank 0.3048 0.4572 0.08255 0.0254 0.01905 0.0127 0.001524 0.004572 0.45
    Near (Mm $f.flat.size[0]) (Mm $cf[0]) 2e-3 'flat X equals the closed form (same part)'; Near (Mm $f.flat.size[1]) (Mm $cf[1]) 2e-3 'flat Y equals the closed form'
}

Test 'C05 requests that cannot work are refused BEFORE anything is opened (fast, no PropertyManager, state_version unchanged)' {
    Tray; $sv = StateVer; $sw0 = [Diagnostics.Stopwatch]::StartNew()
    $base = @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572 }
    Fail (Invoke-Tool 'sheet_metal_convert' @{ bend_edges = $TrayEdges }) 'MISSING_PARAMETER' 'no fixed_face' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]' }) 'MISSING_PARAMETER' 'no bend edges' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0.5,0.5,0.5]'; bend_edges = $TrayEdges }) 'FACE_NOT_FOUND' 'fixed face off the body' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; bend_radius = 0.004572 }) 'MISSING_PARAMETER' 'no thickness (SolidWorks remembers the last one: the call must say it)' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524 }) 'MISSING_PARAMETER' 'no bend radius' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ bend_edges = '[[0.01,0.01,0]]' })) 'EDGE_NOT_FOUND' 'a point that is on no edge' | Out-Null
    $amb = Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ bend_edges = '[[0.05,0.03,0]]' })) 'EDGE_NOT_FOUND' 'a corner point lies on several edges'; Assert ($amb.error.message -like '*ambiguous*') "message should say ambiguous: $($amb.error.message)"
    Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ rip_sketches = '["Sketch99"]' })) 'SKETCH_NOT_FOUND' 'unknown rip sketch' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ corner = 'round' })) 'INVALID_PARAMETER' 'bad corner' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ relief = 'star' })) 'INVALID_PARAMETER' 'bad relief' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ relief = 'obround'; relief_ratio = 0.5 })) 'INVALID_PARAMETER' 'an obround relief stores no ratio' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ thickness = 0.1 })) 'INVALID_PARAMETER' 'a 100 mm "sheet"' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' (Merge $base @{ corner_timing = 'never' })) 'INVALID_PARAMETER' 'bad corner_timing' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; steps = '[{"list":"fold","edges":[]}]' }) 'INVALID_PARAMETER' 'bad step kind' | Out-Null
    Assert ((PmWindows) -eq 0) 'no PropertyManager may have been opened'; Assert ((StateVer) -eq $sv) 'state_version must not change on a refusal'; Assert ($sw0.Elapsed.TotalSeconds -lt 25) "refusals took $([int]$sw0.Elapsed.TotalSeconds) s"
    # the bend zones: a rim and lip too small for the radius (the kind of fixture that HUNG SolidWorks): refused in a fraction of a second, with the numbers
    $n = RimFixture 0.12 0.08 0.04 0.010 0.008 0.005; $st = RimSteps $n 0 0.12 0.08 0.04 0.010 0.008; $t = [Diagnostics.Stopwatch]::StartNew()
    $z = Fail (Invoke-Tool 'sheet_metal_convert' (Merge @{ fixed_face = '[0,0,0]'; steps = $st } $V4)) 'BEND_ZONES_DO_NOT_FIT' 'rim and lip too small for the radius'
    Assert ($z.error.message -like '*12.192*') "the message should carry the numbers: $($z.error.message)"; Assert ($t.Elapsed.TotalSeconds -lt 10) 'the check must be quick'; Assert ((PmWindows) -eq 0) 'nothing opened'
}

Test 'C06 accept=false leaves the PropertyManager open with the lists filled; a second call says PM_ALREADY_OPEN; Cancel leaves NO feature; a normal convert then works' {
    $prev = [int](([regex]::Match((Invoke-Bridge 'snapshots' 5000), '"intervalSeconds":(\d+)')).Groups[1].Value); [void](Invoke-Bridge 'snapshot_policy off' 5000)      # a snapshot would close the open PropertyManager
    try {
        Tray
        $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572; accept = $false }) 'convert (accept=false)')
        Assert ($r.accepted -eq $false -and $r.pm_left_open -eq $true -and $r.lists.bend_edges -eq 4) "result: $($r | ConvertTo-Json -Compress -Depth 4)"
        Assert ((PmWindows) -eq 1) 'the PropertyManager must still be open'
        Fail (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges }) 'PM_ALREADY_OPEN' 'a second convert' | Out-Null
        Ok (Invoke-Tool 'ui_inspect' @{ action = 'act'; name = 'OnCancelButton'; op = 'invoke' }) 'cancel' | Out-Null; Start-Sleep 3
        Assert ((PmWindows) -eq 0) 'Cancel must close the PropertyManager'; Assert (@(Converts).Count -eq 0) 'Cancel must leave no Convert feature'
        $ok = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572 }) 'convert after cancel'); Assert ($ok.verified -eq $true) 'the convert after a cancel must work'
    } finally { RestoreSnapshots $prev }
}

Test 'C07 other bodies are hidden while picking and visible again afterwards (a decoy solid beside the tray is never touched)' {
    Tray
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Right Plane'; offset = 0.2 }) 'decoy plane' | Out-Null
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Plane1'; width = 0.02; height = 0.02 }) 'decoy sketch' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.02; feature_type = 'boss' }) 'decoy extrude' | Out-Null
    $before = @(Bodies); Assert ($before.Count -eq 2) "two bodies expected before: $($before -join ' | ')"
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572 }) 'convert with a decoy body')
    Assert ($r.verified -eq $true) "problems: $($r.problems -join ' | ')"
    $after = @(Bodies); Assert ($after.Count -eq 2) "the converted part and the decoy must remain: $($after -join ' | ')"
    Assert (@($after | Where-Object { $_ -match 'visible=False' }).Count -eq 0) "every body must be visible again: $($after -join ' | ')"
}

Test 'C08 an INCH document: the PropertyManager shows 0.060in, the settings and the pre-check still compare in millimetres' {
    Tray; Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'in' }) 'inches' | Out-Null
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572; reverse_thickness = 'on' }) 'convert in an inch document')
    Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"; Near $r.definition.SheetThickness 0.001524 1e-9 'thickness (m)'; Near $r.definition.BendRadius 0.004572 1e-9 'radius (m)'
    Assert ((Params).units.length -eq 'in') 'the document stays in inches'
}

Test 'C09 a dialog raised INSIDE a click is answered by the watchdog and reported: the call completes instead of standing still' {
    $names = RimFixture; $steps = RimSteps $names
    $job = Start-Job -ScriptBlock { param($common) . $common; Start-Sleep -Seconds 9; Invoke-Bridge 'msgbox watchdog-test-dialog' 90000 } -ArgumentList "$Probes\_common.ps1"
    try {
        $t = [Diagnostics.Stopwatch]::StartNew()
        $r = Ok (Invoke-Tool 'sheet_metal_convert' (Merge @{ fixed_face = '[0,0,0]'; steps = $steps } $V4)) 'convert while a dialog pops up'
        $g = Rg $r; Assert ($t.Elapsed.TotalSeconds -lt 115) "the call took $([int]$t.Elapsed.TotalSeconds) s"
        Assert (($g.problems -join ' | ') -like '*watchdog-test-dialog*') "the dialog must be reported: $($g.problems -join ' | ')"
        Assert ($g.feature -like 'Convert-Solid*' -and $g.lists.bend_edges -eq 12) 'the feature must have been created all the same'
    } finally { Receive-Job $job -Wait -AutoRemoveJob -ErrorAction SilentlyContinue | Out-Null }
    $d = Invoke-Bridge 'dialogs' 3000; Assert ($d -eq '[]') "a dialog is still open: $d"
}

Test 'C10 an ABANDONED call (the gate gives up) cancels the PropertyManager and stops: no feature, no PropertyManager, the gate idle again' {
    $names = RimFixture; $steps = RimSteps $names
    [void](Invoke-Bridge 'gate_timeout 8' 5000)
    try {
        $r = Invoke-Tool 'sheet_metal_convert' (Merge @{ fixed_face = '[0,0,0]'; steps = $steps } $V4)
        Assert ($r.status -eq 'FAILED' -and $r.error.code -eq 'STA_TIMEOUT') "expected STA_TIMEOUT after 8 s, got $($r.status) $($r.error.code)"
        $idle = $false; for ($i = 0; $i -lt 40; $i++) { Start-Sleep 2; if (-not (ToolBusy)) { $idle = $true; break } }
        Assert $idle 'the abandoned call must stop by itself (cooperative cancel)'
    } finally { [void](Invoke-Bridge 'gate_timeout reset' 5000) }
    Start-Sleep 2; Assert ((PmWindows) -eq 0) 'the PropertyManager must have been cancelled'; Assert (@(Converts).Count -eq 0) 'no Convert feature may exist'
    Ok (Invoke-Tool 'verify_state' @{}) 'the gate must be usable again' | Out-Null
}

Test 'C11 ui_inspect: windows / view / project / window_at / selection / act and its refusals' {
    Tray; Assert ((PmWindows) -eq 0) 'no PropertyManager open'
    Ok (Invoke-Tool 'ui_inspect' @{ action = 'view'; name = '*Back' }) 'view' | Out-Null
    $p = Rg (Ok (Invoke-Tool 'ui_inspect' @{ action = 'project'; points = '[[0,0,0],[0.05,0,0]]' }) 'project'); Assert (@($p.screen).Count -eq 2) "two points projected (a JSON string is accepted): $($p | ConvertTo-Json -Compress -Depth 4)"
    Assert ($p.screen[1][0] -ne $p.screen[0][0]) 'a point 50 mm away must land on another pixel column'
    Fail (Invoke-Tool 'ui_inspect' @{ action = 'project' }) 'MISSING_PARAMETER' 'project without points' | Out-Null
    $w = Rg (Ok (Invoke-Tool 'ui_inspect' @{ action = 'window_at'; sx = [int]$p.screen[0][0]; sy = [int]$p.screen[0][1] }) 'window_at'); Assert ($w.hwnd -and $w.class -and $w.ours -eq $true) "SolidWorks' own graphics window must answer at the model centre: $($w | ConvertTo-Json -Compress -Depth 3)"
    $s = Rg (Ok (Invoke-Tool 'ui_inspect' @{ action = 'selection' }) 'selection'); Assert ($s.count -ge 0) 'selection readable'
    # a pixel that belongs to ANOTHER application (an editor, a browser, the taskbar) is never clicked: look for one on the primary screen (none when SolidWorks fills it - C14 builds one then)
    Add-Type -AssemblyName System.Windows.Forms; $sb = [Windows.Forms.Screen]::PrimaryScreen.Bounds; $foreign = $null
    foreach ($fx in 0.5, 0.15, 0.85) { foreach ($fy in 0.5, 0.15, 0.85) {
        if ($foreign) { break }
        $px = [int]($sb.Left + $sb.Width * $fx); $py = [int]($sb.Top + $sb.Height * $fy); $o = Rg (Invoke-Tool 'ui_inspect' @{ action = 'window_at'; sx = $px; sy = $py })
        if ($o -and $o.ours -eq $false) { $foreign = @($px, $py, $o.who) } } }
    if ($foreign) { $cl = Fail (Invoke-Tool 'ui_inspect' @{ action = 'post_click'; sx = $foreign[0]; sy = $foreign[1] }) 'FOREIGN_WINDOW' "post_click on another application's window ($($foreign[2]))"; Assert ($cl.error.message -like '*another application*') 'the message must say why' }
    else { '      (no pixel of another application on the primary screen: the post_click refusal is covered by C15)' }
    Fail (Invoke-Tool 'ui_inspect' @{ action = 'tree' }) 'WINDOW_NOT_FOUND' 'tree without a PropertyManager' | Out-Null; Fail (Invoke-Tool 'ui_inspect' @{ action = 'act'; name = 'OnOKButton'; op = 'invoke' }) 'WINDOW_NOT_FOUND' 'act without one' | Out-Null
    Fail (Invoke-Tool 'ui_inspect' @{ action = 'explode' }) 'INVALID_PARAMETER' 'unknown action' | Out-Null
}

Test 'C12 keep_body: the original surface body stays next to the new sheet-metal body' {
    Tray
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572; keep_body = $true }) 'convert, keep the body')
    Assert ($r.verified -eq $true -and $r.definition.KeepBody -eq $true) "problems: $($r.problems -join ' | ') keep: $($r.definition.KeepBody)"
    $b = @(Bodies); Assert ($b.Count -eq 2 -and @($b | Where-Object { $_ -match 'type=1' }).Count -eq 1) "a surface body and the sheet-metal solid expected: $($b -join ' | ')"
}

Test 'C13 defaults never leak: after a call with unusual settings, a call that gives only thickness and radius gets the FACTORY defaults (reverse on, keep body off, butt corner, 2 mm gap, rectangular relief 0.5)' {
    Tray
    $a = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.002; bend_radius = 0.003; reverse_thickness = 'off'; keep_body = $true; corner = 'overlap'; rip_gap = 0.0001; overlap_ratio = 1; relief = 'tear'; relief_ratio = 0.8 }) 'convert with unusual settings')
    Assert ($a.verified -eq $true -and $a.definition.KeepBody -eq $true -and $a.definition.ReverseThickness -eq $false -and $a.definition.CornerDefaults -eq 1 -and $a.definition.ReliefType -eq 2) "unusual settings: $($a.problems -join ' | ') $($a.definition | ConvertTo-Json -Compress)"
    Near $a.definition.ReliefRatio 0.8 1e-6 'relief ratio 0.8 reached the feature'
    Tray
    $b = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572 }) 'convert with only thickness and radius')
    Assert ($b.verified -eq $true) "problems: $($b.problems -join ' | ')"
    $d = $b.definition; Assert ($d.ReverseThickness -eq $true -and $d.KeepBody -eq $false -and $d.CornerDefaults -eq 0 -and $d.ReliefType -eq 0) "factory defaults expected, got $($d | ConvertTo-Json -Compress)"
    Near $d.RipGap 0.002 1e-9 'rip gap 2 mm'; Near $d.RipOverlapRatio 0.5 1e-6 'overlap ratio 0.5'; Near $d.ReliefRatio 0.5 1e-6 'relief ratio = the template default 0.5 (the 0.8 of the first call must not stick)'
    $bodies = @(Bodies); Assert ($bodies.Count -eq 1) "keep body is off again: one body expected, got $($bodies.Count): $($bodies -join ' | ')"
}

Test 'C14 soak: three build + convert + close cycles - no PropertyManager left over, no failure, bounded handle growth' {
    $before = (Sw).HandleCount
    for ($i = 1; $i -le 3; $i++) {
        Tray; $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572 }) "cycle $i")
        Assert ($r.verified -eq $true) "cycle $i problems: $($r.problems -join ' | ')"; Assert ((PmWindows) -eq 0) "cycle $i left a PropertyManager"
    }
    Fresh-Part; $grow = ((Sw).HandleCount - $before) / 3; "      soak: handles per cycle $([math]::Round($grow, 1))"; Assert ($grow -lt 150) "handle growth per cycle: $grow"
}

Test 'C16 a JOG converts: side walls that step inward 2 mm through a 45 degree slope = 3 right-angle bends + 4 jog bends of exactly 45 degrees (the bend-fit guard measures each bend''s deflection instead of assuming 90 degrees)' {
    Fresh-Part
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Top Plane' }) 'jog section sketch' | Out-Null
    $seg = '[{"entity_type":"line","x1":-0.05,"y1":0,"x2":0.05,"y2":0},{"entity_type":"line","x1":0.05,"y1":0,"x2":0.05,"y2":-0.024},{"entity_type":"line","x1":0.05,"y1":-0.024,"x2":0.048,"y2":-0.026},{"entity_type":"line","x1":0.048,"y1":-0.026,"x2":0.048,"y2":-0.03},{"entity_type":"line","x1":0.048,"y1":-0.03,"x2":-0.048,"y2":-0.03},{"entity_type":"line","x1":-0.048,"y1":-0.03,"x2":-0.048,"y2":-0.026},{"entity_type":"line","x1":-0.048,"y1":-0.026,"x2":-0.05,"y2":-0.024},{"entity_type":"line","x1":-0.05,"y1":-0.024,"x2":-0.05,"y2":0}]'
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $seg }) 'jog section lines' | Out-Null
    Ok (Invoke-Tool 'sketch_fully_define' @{}) 'jog section fully defined' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.06; feature_type = 'boss'; mid_plane = $true }) 'jog block' | Out-Null
    Ok (Invoke-Tool 'delete_face' @{ faces = '[[0,0,0.03],[0,-0.03,0.015]]' }) 'open top and front' | Out-Null
    $steps = '[{"list":"bend","edges":[[-0.05,0,0],[0.05,0,0],[0,0.03,0]]},{"list":"bend","edges":[[-0.05,0,0.024],[0.05,0,0.024],[-0.048,0,0.026],[0.048,0,0.026]]}]'
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; steps = $steps; thickness = 0.0012; bend_radius = 0.0012; corner = 'underlap'; rip_gap = 0.0000762; overlap_ratio = 1; relief = 'tear'; k_factor = 0.46 }) 'convert the jogged tray')
    Assert ($r.accepted -eq $true -and $r.feature_error -eq 0 -and $r.lists.bend_edges -eq 7) "result: $($r | ConvertTo-Json -Compress -Depth 4)"
    Assert ((PmWindows) -eq 0) 'the PropertyManager must be closed'
    $p = Params; $ang = @($p.bends | ForEach-Object { [math]::Round([double]$_.angle * 180 / [math]::PI, 3) })
    Assert (@($ang | Where-Object { $_ -eq 90 }).Count -eq 3 -and @($ang | Where-Object { $_ -eq 45 }).Count -eq 4) "bend angles (deg): $($ang -join ', ')"
}

Test 'C17 a SECOND body with its OWN thickness and radius (master-sketch build, 2026-11-02): ONE two-contour master section (tray with a 20 degree jog, cover top + skirts) -> ONE surface extrusion -> tray 1.2 / 1.2 mm, then cover 1.0 / 1.4 mm: the cover feature carries ITS thickness and radius (the part''s 1.2 mm used to win), the cover is 430 wide outside, the jogged tray (step 1.1 on 1.2) converts' {
    Fresh-Part
    function L($a, $b) { @{ entity_type = 'line'; x1 = $a[0]; y1 = $a[1]; x2 = $b[0]; y2 = $b[1] } }
    $h = 0.215; $f = -0.022225; $s = 0.0011028; $jx = 0.2139; $jy = 0.004125; $wt = 0.019825; $ct = 0.022225; $sk = 0.004225
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'master sketch' | Out-Null
    $segs = @((L @(-$h, $f) @($h, $f)), (L @($h, $f) @($h, $s)), (L @($h, $s) @($jx, $jy)), (L @($jx, $jy) @($jx, $wt)), (L @(-$h, $f) @(-$h, $s)), (L @(-$h, $s) @(-$jx, $jy)), (L @(-$jx, $jy) @(-$jx, $wt)),
              (L @(-$h, $ct) @($h, $ct)), (L @($h, $ct) @($h, $sk)), (L @(-$h, $ct) @(-$h, $sk)))
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $segs }) 'section' | Out-Null
    $d = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{ relations = 'first'; relate = @(@{ type = 'collinear'; a = @(0.215, -0.01); b = @(0.215, 0.015) }) }) 'master relations first'); Assert ($d.verified -eq $true -and @($d.dimensions).Count -le 9) "master: $(@($d.dimensions).Count) dimensions"
    Ok (Invoke-Tool 'edit_feature' @{ feature_name = $d.sketch; action = 'rename'; new_name = 'MASTER_SECTION' }) 'rename the master' | Out-Null
    $e = Rg (Ok (Invoke-Tool 'surface_extrude' @{ sketch = 'MASTER_SECTION'; depth = 0.3; reverse = $true }) 'both surfaces'); Assert ($e.surface_bodies -eq 2) "two surface bodies from one extrusion: $($e.surface_bodies)"
    $tray = '[{"list":"bend","edges":[[-0.215,-0.022225,-0.15],[0.215,-0.022225,-0.15]]},{"list":"bend","edges":[[-0.215,0.0011028,-0.15],[0.215,0.0011028,-0.15]]},{"list":"bend","edges":[[-0.2139,0.004125,-0.15],[0.2139,0.004125,-0.15]]}]'
    $t = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,-0.022225,-0.15]'; steps = $tray; thickness = 0.0012; bend_radius = 0.0012; k_factor = 0.44; reverse_thickness = 'off'; corner = 'underlap'; rip_gap = 0.0000762; overlap_ratio = 1; relief = 'rectangular' }) 'convert the jogged tray')
    Assert ($t.accepted -eq $true -and $t.feature_error -eq 0 -and $t.lists.bend_edges -eq 6) "tray: $($t | ConvertTo-Json -Compress -Depth 4)"
    $cv = '[{"list":"bend","edges":[[-0.215,0.022225,-0.15],[0.215,0.022225,-0.15]]}]'
    $c = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0.022225,-0.15]'; steps = $cv; thickness = 0.001; bend_radius = 0.0014; k_factor = 0.44; reverse_thickness = 'on'; corner = 'underlap'; rip_gap = 0.0000762; overlap_ratio = 1; relief = 'rectangular' }) 'convert the cover')
    Assert ($c.accepted -eq $true -and $c.feature_error -eq 0) "cover: $($c | ConvertTo-Json -Compress -Depth 4)"
    Assert ([math]::Abs([double]$c.definition.SheetThickness - 0.001) -lt 1e-9 -and [math]::Abs([double]$c.definition.BendRadius - 0.0014) -lt 1e-9) "the cover feature must carry 1.0 / 1.4 mm: $($c.definition.SheetThickness) / $($c.definition.BendRadius)"
    Assert ($c.verified -eq $true) "the cover convert is verified (no problems): $($c.problems -join '; ')"
    # each body has ITS OWN K-factor (the new body's Sheet-Metal feature): a later body used to stay at the template's 0.5
    $sms = @((Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'features')).features | Where-Object { $_.type -eq 'SheetMetal' })
    Assert ($sms.Count -ge 2) "two Sheet-Metal features expected, got $($sms.Count)"
    foreach ($sm in $sms) { $k = [double](Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'get'; feature = $sm.name }) "get $($sm.name)")).properties.KFactor.value; Near $k 0.44 1e-9 "K-factor of $($sm.name)" }
    # nothing the picker hid stays hidden: both bodies are listed as visible solids
    $bd = Rg (Ok (Invoke-Tool 'analyze_model' @{ analysis_type = 'bodies' }) 'bodies'); Assert (@($bd.bodies).Count -eq 2) "two solid bodies expected after the two Converts: $(@($bd.bodies).Count)"
}

if ($Cover) {
Test 'C15 a window of ANOTHER application over the graphics area is never clicked: the convert is refused (GRAPHICS_COVERED), post_click is refused (FOREIGN_WINDOW), the covering window receives 0 clicks' {
    Add-Type -AssemblyName System.Windows.Forms
    Tray; $names = $null; Ok (Invoke-Tool 'ui_inspect' @{ action = 'view'; name = '*Back' }) 'view' | Out-Null
    $vb = (Rg (Ok (Invoke-Tool 'ui_inspect' @{ action = 'project'; points = '[[0,0,0]]' }) 'project')).visible_box          # left, top, right, bottom of the graphics area, in screen pixels
    $out = Join-Path ([IO.Path]::GetTempPath()) 'solidpilot-cover.txt'; Remove-Item $out, "$out.stop" -ErrorAction SilentlyContinue
    $proc = Start-Process powershell -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$PSScriptRoot\cover-window.ps1", '-Left', $vb[0], '-Top', $vb[1], '-Width', ($vb[2] - $vb[0]), '-Height', ($vb[3] - $vb[1]), '-Out', $out
    try {
        $up = $false; for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; if ((Test-Path $out) -and (Get-Content $out -ErrorAction SilentlyContinue) -eq 'shown') { $up = $true; break } }; Assert $up 'the covering window did not appear'
        $c = [int]((($vb[0] + $vb[2]) / 2)); $d = [int]((($vb[1] + $vb[3]) / 2))
        $w = Rg (Ok (Invoke-Tool 'ui_inspect' @{ action = 'window_at'; sx = $c; sy = $d }) 'window_at'); Assert ($w.ours -eq $false) "the covering window must be on top at the centre of the view: $($w | ConvertTo-Json -Compress -Depth 3)"
        $r = Fail (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572 }) 'GRAPHICS_COVERED' 'convert under a covering window'; Assert ($r.error.message -like '*another application*') "message: $($r.error.message)"
        Fail (Invoke-Tool 'ui_inspect' @{ action = 'post_click'; sx = $c; sy = $d }) 'FOREIGN_WINDOW' 'post_click under a covering window' | Out-Null
        Start-Sleep 1; Assert ((PmWindows) -eq 0) 'the PropertyManager must have been cancelled'; Assert (@(Converts).Count -eq 0) 'no Convert feature may exist'
    } finally {
        Set-Content "$out.stop" 'stop'; $null = $proc.WaitForExit(15000); if (-not $proc.HasExited) { $proc.Kill() }
    }
    $res = if (Test-Path $out) { Get-Content $out } else { '' }; Assert ($res -eq 'clicks=0') "the covering window must have received no click: '$res'"
    Remove-Item $out, "$out.stop" -ErrorAction SilentlyContinue
    $ok = Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572 }) 'convert once the window is gone'); Assert ($ok.verified -eq $true) 'with the window gone the convert works'
}
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
