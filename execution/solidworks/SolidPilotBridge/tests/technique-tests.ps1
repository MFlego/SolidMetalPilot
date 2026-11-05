# Regression tests for the three tool improvements of 2026-11-05 (tuning round 2): base_flange `depth` (an OPEN profile), extrude_feature `scope_bodies` (a cut that acts on ONE body of a
# multi-body part) and sketch_convert_entities on ARCS (a curved master edge is found by its own closest point). Each test builds its own part, so any one can run alone.
# Needs SolidWorks running with the bridge, NOTHING else open, the in-process tools loaded.
# Usage: technique-tests.ps1 [-Force] [-Only 'TE2']
param([switch]$Force, [string]$Only = '')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\probes\_common.ps1"
. "$PSScriptRoot\..\probes\_tools.ps1"
. "$PSScriptRoot\sheetmetal-fixtures.ps1"
$script:results = New-Object System.Collections.ArrayList
function Test([string]$Name, [scriptblock]$Body) {
    if ($Only -and $Name -notmatch $Only) { return }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $true; $why = ''
    try { & $Body } catch { $ok = $false; $why = $_.Exception.Message }
    "{0}  {1,-100} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.ElapsedMilliseconds, $why
    [void]$script:results.Add([pscustomobject]@{ name = $Name; pass = $ok; ms = $sw.ElapsedMilliseconds; why = $why })
}
$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
"== technique tools  ($script:ToolBase)"

# an open L (40 mm along x, 30 mm up) on the Front Plane (or on a plane offset from it), returns the sketch name
function LSketch([string]$plane = 'Front Plane') {
    $s = (Ok (Invoke-Tool 'create_sketch' @{ plane = $plane }) 'sketch').cadState.activeSketch
    $segs = @(@{ entity_type = 'line'; x1 = 0; y1 = 0; x2 = 0.04; y2 = 0 }, @{ entity_type = 'line'; x1 = 0.04; y1 = 0; x2 = 0.04; y2 = 0.03 })
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $segs }) 'L lines' | Out-Null
    $s
}

Test 'TE1 base_flange depth: an OPEN L profile becomes a 2 mm sheet extruded 100 mm, read back and verified' {
    Fresh-Part
    $s = LSketch
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = $s; thickness = 0.002; bend_radius = 0.002; k_factor = 0.45; depth = 0.1 }) 'base flange with depth')
    Assert ($r.verified -eq $true -and [math]::Abs($r.depth - 0.1) -lt 1e-7) "depth read back: $($r | ConvertTo-Json -Compress)"
    $z = Extent 2; Near (Mm ($z[1] - $z[0])) 100 1e-3 'extruded length (z)'
    $p = Params; Assert ([int]$p.cut_list[0].properties.Bends -eq 1) "one bend expected: $($p.cut_list[0].properties.Bends)"
}

Test 'TE2 extrude_feature scope_bodies: a slot across TWO bodies cuts only the body named by a point (2000 mm3 off, not 4000), scope read back as applied' {
    Fresh-Part; Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'mm'; decimals = 2 }) 'units' | Out-Null
    $s1 = LSketch; Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = $s1; thickness = 0.002; bend_radius = 0.002; k_factor = 0.45; depth = 0.1 }) 'body 1' | Out-Null
    $pl = (Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = 0.15 }) 'plane').cadState.features[0]
    $s2 = LSketch $pl; Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = $s2; thickness = 0.002; bend_radius = 0.002; k_factor = 0.45; depth = 0.1 }) 'body 2' | Out-Null
    $v0 = Volume
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Top Plane' }) 'slot sketch' | Out-Null
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = @(@{ entity_type = 'rectangle'; x1 = 0.01; y1 = -0.05; x2 = 0.03; y2 = -0.2 }) }) 'slot' | Out-Null
    $r = Rg (Ok (Invoke-Tool 'extrude_feature' @{ feature_type = 'cut'; through = $true; scope_bodies = '[[0.005,0,0.18]]' }) 'scoped cut')
    Assert ($r.scope.applied -eq $true -and $r.scope.scope_count -eq 1 -and $r.scope.auto_select -eq $false) "scope: $($r.scope | ConvertTo-Json -Compress)"
    $v1 = Volume; Near (($v0 - $v1) * 1e9) 2000 5 'volume removed (mm3): one slot, one body'
}

Test 'TE3 sketch_convert_entities on an ARC: a half disc (line + arc) is converted by one point on each edge into a fully defined sketch' {
    Fresh-Part
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane' }) 'disc sketch' | Out-Null
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = @(@{ entity_type = 'line'; x1 = -0.03; y1 = 0; x2 = 0.03; y2 = 0 }, @{ entity_type = 'arc_center'; cx = 0; cy = 0; x1 = 0.03; y1 = 0; x2 = -0.03; y2 = 0 }) }) 'half disc' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.01; feature_type = 'boss' }) 'half disc solid' | Out-Null
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane' }) 'target sketch' | Out-Null
    $r = Rg (Ok (Invoke-Tool 'sketch_convert_entities' @{ points = '[[0.01,0,0],[0,0.03,0]]' }) 'convert line + arc')
    Assert ($r.edges -eq 2 -and $r.created_segments -eq 2 -and $r.verified -eq $true) "result: $($r | ConvertTo-Json -Compress)"
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
"{0} tests, {1} passed, {2} failed." -f $script:results.Count, ($script:results.Count - $fail), $fail
exit $fail
