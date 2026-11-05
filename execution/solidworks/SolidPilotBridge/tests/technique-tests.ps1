# Regression tests for the tool improvements of 2026-11-05 (tuning rounds 2 and 3): base_flange `depth` (an OPEN profile), extrude_feature `scope_bodies` (a cut that acts on ONE body of a
# multi-body part), sketch_convert_entities on ARCS (a curved master edge is found by its own closest point) and create_pattern `bodies` (a sheet-metal body repeated by a linear / circular pattern). Each test builds its own part, so any one can run alone.
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

Test 'TE4 create_pattern bodies: one L body becomes 3 (linear, 50 mm), each with its own flat pattern; the count is an equation-drivable dimension (D1@LPattern1 = 4 gives 4 bodies); a circular copy works; a mirror of bodies is refused' {
    Fresh-Part; Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'mm'; decimals = 2 }) 'units' | Out-Null
    $s = LSketch; Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = $s; thickness = 0.002; bend_radius = 0.002; k_factor = 0.45; depth = 0.04 }) 'seed body' | Out-Null
    $r = Rg (Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'linear'; direction = 'X'; spacing = 0.06; count = 3; bodies = '["Base-Flange1"]' }) 'linear body pattern')
    Assert ($r.body_pattern.bodies_before -eq 1 -and $r.body_pattern.bodies_after -eq 3) "bodies: $($r.body_pattern | ConvertTo-Json -Compress)"
    $f = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flats')
    Assert (@($f.flat_patterns).Count -eq 3) "one flat pattern per body: $(@($f.flat_patterns) -join ',')"
    Ok (Invoke-Tool 'equations' @{ action = 'set'; variables = '{"N_Rib":"4"}'; links = '{"D1@LPattern1":"N_Rib"}' }) 'count equation' | Out-Null
    $f = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flats after the count change')
    Assert (@($f.flat_patterns).Count -eq 4) "4 flats after N_Rib = 4: $(@($f.flat_patterns) -join ',')"
    $ax = Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'axis'; entity1_name = 'Right Plane'; entity1_type = 'PLANE'; entity2_name = 'Front Plane'; entity2_type = 'PLANE' }) 'axis'
    $c = Rg (Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'circular'; axis_name = 'Axis1'; count = 3; angle = 360; equal_spacing = $true; bodies = '["Base-Flange1"]' }) 'circular body pattern')
    Assert ($c.body_pattern.bodies_after -eq 6) "circular: $($c.body_pattern | ConvertTo-Json -Compress)"
    $m = Invoke-Tool 'create_pattern' @{ pattern_type = 'mirror'; plane = 'Right Plane'; bodies = '["Base-Flange1"]' }
    Assert ($m.status -eq 'FAILED' -and $m.error.code -eq 'NOT_SUPPORTED') "mirror of bodies must be refused: $($m.status) $($m.error.code)"
}

Test 'TE5 create_pattern grid: ONE cut pattern in two directions (3 x 2 holes, count2 / direction2) removes 6 holes of volume, not 3' {
    Fresh-Part; Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'mm'; decimals = 2 }) 'units' | Out-Null
    $s = LSketch; Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = $s; thickness = 0.002; bend_radius = 0.002; k_factor = 0.45; depth = 0.1 }) 'plate' | Out-Null
    $v0 = Volume
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Top Plane' }) 'hole sketch' | Out-Null
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = @(@{ entity_type = 'circle'; cx = 0.01; cy = -0.03; radius = 0.002 }) }) 'hole' | Out-Null
    $c = Ok (Invoke-Tool 'extrude_feature' @{ feature_type = 'cut'; through = $true }) 'seed hole'
    $name = $c.cadState.features[0]
    $v1 = Volume; $one = ($v0 - $v1) * 1e9
    Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'linear'; feature_name = $name; direction = 'X'; spacing = 0.008; count = 3; direction2 = 'Z'; spacing2 = 0.02; count2 = 2 }) 'grid pattern' | Out-Null
    $v2 = Volume; Near (($v0 - $v2) * 1e9) (6 * $one) 1.0 'volume removed (mm3): 6 holes'
}

Test 'TE7 create_pattern grid WITH scope_bodies: the 3 x 2 grid still has both directions after the scope is set (6 holes, not 3)' {
    Fresh-Part; Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'mm'; decimals = 2 }) 'units' | Out-Null
    $s = LSketch; Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = $s; thickness = 0.002; bend_radius = 0.002; k_factor = 0.45; depth = 0.1 }) 'plate' | Out-Null
    $v0 = Volume
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Top Plane' }) 'hole sketch' | Out-Null
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = @(@{ entity_type = 'circle'; cx = 0.01; cy = -0.03; radius = 0.002 }) }) 'hole' | Out-Null
    $c = Ok (Invoke-Tool 'extrude_feature' @{ feature_type = 'cut'; through = $true }) 'seed hole'
    $name = $c.cadState.features[0]
    $v1 = Volume; $one = ($v0 - $v1) * 1e9
    $body = (Rg (Ok (Invoke-Tool 'analyze_model' @{ analysis_type = 'bodies' }) 'bodies')) 2>$null
    Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'linear'; feature_name = $name; direction = 'X'; spacing = 0.008; count = 3; direction2 = 'Z'; spacing2 = 0.02; count2 = 2; scope_bodies = ('["' + $name + '"]') }) 'scoped grid pattern' | Out-Null
    $v2 = Volume; Near (($v0 - $v2) * 1e9) (6 * $one) 1.0 'volume removed (mm3): 6 holes'
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
"{0} tests, {1} passed, {2} failed." -f $script:results.Count, ($script:results.Count - $fail), $fail
exit $fail
