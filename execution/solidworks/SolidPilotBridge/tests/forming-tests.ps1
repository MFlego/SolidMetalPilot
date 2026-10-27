# Regression tests for the sheet-metal FORMING tools (sheet_metal_hem, sheet_metal_break_corner, sheet_metal_miter_flange) against the IN-PROCESS add-in.
# Needs SolidWorks running with the bridge, NOTHING else open in it (the suite builds a tray and a plate with the SolidPilot tools and closes every document at the end), tools loaded.
# The tests check GEOMETRY (the part's size and face counts before / after) as well as the read-back, and one test switches the SolidWorks view to an awkward orientation first: the tools pick entities
# by geometry (Select4 on the object), never through the view, which is what SelectByID2(x,y,z) does.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File forming-tests.ps1 [-Force] [-Json out.json]
param([switch]$Force, [string]$Json = '')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\probes\_common.ps1"
. "$PSScriptRoot\..\probes\_tools.ps1"
$script:results = New-Object System.Collections.ArrayList
function Test([string]$Name, [scriptblock]$Body) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $true; $why = ''
    try { & $Body } catch { $ok = $false; $why = $_.Exception.Message }
    "{0}  {1,-84} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.ElapsedMilliseconds, $why
    [void]$script:results.Add([pscustomobject]@{ name = $Name; pass = $ok; ms = $sw.ElapsedMilliseconds; why = $why })
}
function Assert($cond, [string]$msg) { if (-not $cond) { throw $msg } }
function Near([double]$a, [double]$b, [double]$tol, [string]$what) { if ([math]::Abs($a - $b) -gt $tol) { throw "$what : $a vs $b (tolerance $tol)" } }
function StateVer { [int](Invoke-RestMethod "$script:ToolBase/api/tool/state" -TimeoutSec 30).stateVersion }
function Rg($reply) { $reply.result_geometry }
function Ok($reply, [string]$what) { Assert ($reply.status -eq 'COMPLETED') "$what : $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Fail($reply, [string]$code, [string]$what) { Assert ($reply.status -eq 'FAILED' -and $reply.error.code -eq $code) "$what : expected FAILED/$code, got $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Mm($v) { [math]::Round([double]$v * 1000, 3) }
function Sw { Get-Process SLDWORKS | Select-Object -First 1 }
function Del-Feature($name) { Ok (Invoke-Tool 'edit_feature' @{ action = 'delete'; feature_name = $name }) "delete $name" | Out-Null }
function Set-View($name) { $r = Invoke-Bridge "run_cs $PSScriptRoot\..\probes\probe42_set_view.cs|$name" 30000; Assert ($r -like '*-> True*') "view $name : $r" }
function Edge-At([double]$x, [double]$y, [double]$z) {           # the index of the straight edge whose midpoint is at (x, y, z) mm
    $hit = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[0] * 1000 - $x) -lt 0.05 -and [math]::Abs($_.mid[1] * 1000 - $y) -lt 0.05 -and [math]::Abs($_.mid[2] * 1000 - $z) -lt 0.05 } | Select-Object -First 1
    Assert ($null -ne $hit) "no edge with its midpoint at ($x, $y, $z) mm"; [int]$hit.i }
function Face-With([double[]]$normal, [string]$axis, [double]$atMm) {   # a planar wall face with the given normal whose point lies at atMm along axis (x|y|z)
    $k = @{ x = 0; y = 1; z = 2 }[$axis]
    $hit = (Get-Faces).faces | Where-Object { $_.planar -and $_.area -gt 1.5E-3 -and ([math]::Abs($_.normal[0] - $normal[0]) + [math]::Abs($_.normal[1] - $normal[1]) + [math]::Abs($_.normal[2] - $normal[2])) -lt 0.05 -and [math]::Abs($_.point[$k] * 1000 - $atMm) -lt 0.05 } | Select-Object -First 1
    Assert ($null -ne $hit) "no planar face with normal ($($normal -join ',')) at $axis = $atMm mm"; [int]$hit.i }
function Body($r) { $a = (Rg $r).body_after; @{ x = [double]$a.size[0]; y = [double]$a.size[1]; z = [double]$a.size[2]; faces = [int]$a.faces } }
function New-Plate {
    Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null
    Ok (Invoke-Tool 'open_new_part' @{}) 'new part' | Out-Null; Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch' | Out-Null
    $w = 0.05; $h = 0.03
    $segs = @(@{ entity_type = 'line'; x1 = -$w; y1 = -$h; x2 = $w; y2 = -$h }, @{ entity_type = 'line'; x1 = $w; y1 = -$h; x2 = $w; y2 = $h }, @{ entity_type = 'line'; x1 = $w; y1 = $h; x2 = -$w; y2 = $h }, @{ entity_type = 'line'; x1 = -$w; y1 = $h; x2 = -$w; y2 = -$h })
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $segs }) 'rectangle' | Out-Null
    Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.0015; bend_radius = 0.0015; k_factor = 0.5 }) 'base flange' | Out-Null
}

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open in SolidWorks (activeDoc = $ad). The suite closes every document at the end: close them first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== sheet-metal forming tools  ($script:ToolBase)"

$script:fixtureOk = $false
Test 'F00 build the tray (base flange + four 25 mm edge flanges)' {
    $out = & "$PSScriptRoot\..\probes\sm_fixture.ps1" 2>&1 | Out-String; Assert ($out -match 'Edge-Flange4\[EdgeFlange\]') "fixture did not finish: $out"; $script:fixtureOk = $true
}
if (-not $script:fixtureOk) { 'fixture failed - the remaining tests need it'; exit 1 }
$script:outerTop = Edge-At 0 30 25; $script:innerTop = Edge-At 0 28.5 25; $script:eastTop = Edge-At 50 -1.5 25      # the +y wall's outer / inner top edges; a +x wall top edge
$script:wallFace = Face-With @(0, 1, 0) 'y' 30

Test 'F01 closed hem on the OUTER top edge folds outward: +1.6 mm in y, +8 faces, definition read back' {
    $r = Ok (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'closed'; length = 0.01 }) 'closed hem'; $g = Rg $r; $b = Body $r
    Assert ($g.verified -eq $true -and $g.feature_error -eq 0) "report: $($g | ConvertTo-Json -Compress -Depth 4)"; Near $b.y 0.0616 5e-5 'y grew by wall + gap'; Near $b.z 0.0265 5e-5 'height unchanged (material inside)'; Assert ($b.faces -eq $g.body_before.faces + 8) "faces $($g.body_before.faces) -> $($b.faces)"
    Assert ($g.definition.Type -eq 1 -and $g.definition.BendPosition -eq 0) 'Type / BendPosition'; Near $g.definition.Length 0.01 1e-9 'Length'; Near $g.definition.GapDistance 0.0001 1e-9 'a closed hem keeps SolidWorks 0.1 mm gap'
    Del-Feature $g.feature
}

Test 'F02 the INNER top edge folds inward: the part does not grow; bend_outside adds to the height' {
    $r = Ok (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:innerTop]"; hem_type = 'closed'; length = 0.01 }) 'inward hem'; $b = Body $r; Near $b.y 0.060 5e-5 'inward hem leaves y alone'; Del-Feature (Rg $r).feature
    $r2 = Ok (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'closed'; length = 0.01; bend_position = 'bend_outside' }) 'bend outside'; $b2 = Body $r2; Assert ($b2.z -gt 0.0275) "z $($b2.z) should exceed 27.5 mm"; Del-Feature (Rg $r2).feature
}

Test 'F03 hem types: open, tear_drop 225, rolled 270 build; tear_drop 270, rolled 360 and double are refused with HEM_FAILED' {
    foreach ($case in @(@{ t = 'open'; length = 0.01 }, @{ t = 'tear_drop'; radius = 0.003; angle = 225 }, @{ t = 'rolled'; radius = 0.003; angle = 270 })) {
        $p = @{ edges = "[$script:outerTop]"; hem_type = $case.t }; foreach ($k in 'length', 'radius', 'angle') { if ($case.ContainsKey($k)) { $p[$k] = $case[$k] } }
        $r = Ok (Invoke-Tool 'sheet_metal_hem' $p) "hem $($case.t)"; Assert ((Rg $r).verified -eq $true) "$($case.t) not verified"; Assert ((Body $r).y -gt 0.0605) "$($case.t) did not grow the part"; Del-Feature (Rg $r).feature }
    Fail (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'tear_drop'; radius = 0.003; angle = 270 }) 'HEM_FAILED' 'tear drop 270' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'rolled'; radius = 0.003; angle = 360 }) 'HEM_FAILED' 'rolled 360' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'double'; length = 0.01 }) 'HEM_FAILED' 'double' | Out-Null
    Assert (@((Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.type -eq 'Hem' }).Count -eq 0) 'the failed hems left a Hem feature behind'
}

Test 'F04 two edges in one call make ONE hem feature with both edges' {
    $r = Ok (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop,$script:eastTop]"; hem_type = 'closed'; length = 0.01 }) 'two-edge hem'; $g = Rg $r
    Assert (@($g.definition.Edges).Count -eq 2) "edges in the definition: $(@($g.definition.Edges).Count)"; $hems = @((Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.type -eq 'Hem' }); Assert ($hems.Count -eq 1) "$($hems.Count) Hem features"
    Assert ((Body $r).x -gt 0.1015) 'the +x wall hem should widen the part'; Del-Feature $g.feature
}

Test 'F05 a hem is edited through feature_data: Length applies (and is verified), BendPosition applies, Type is refused' {
    $r = Ok (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'closed'; length = 0.01 }) 'hem'; $n = (Rg $r).feature
    $a = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = $n; properties = @{ Length = 0.015 } }) 'length'); Assert ($a.verified -eq $true) 'Length not applied'
    $b = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = $n; properties = @{ BendPosition = 1 } }) 'bend position'); Assert ($b.verified -eq $true) 'BendPosition not applied'
    Fail (Invoke-Tool 'feature_data' @{ action = 'set'; feature = $n; properties = @{ Type = 0 } }) 'MODIFY_FAILED' 'Type is not editable' | Out-Null
    $c = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = $n; properties = @{ GapDistance = 0.0003 } }) 'gap'); Assert ($c.verified -eq $false -and @($c.ignored).Count -eq 1) 'a closed hem keeps its fixed gap: the write-verify must flag the ignored value'
    Del-Feature $n
}

Test 'F06 hem errors: no length, no edge, edge out of range, a wall FACE edge' {
    Fail (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'closed' }) 'MISSING_PARAMETER' 'length' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_hem' @{ hem_type = 'closed'; length = 0.01 }) 'MISSING_PARAMETER' 'no edge' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_hem' @{ edges = '[99999]'; hem_type = 'closed'; length = 0.01 }) 'EDGE_NOT_FOUND' 'range' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_hem' @{ edges = "[$script:outerTop]"; hem_type = 'frilly'; length = 0.01 }) 'INVALID_PARAMETER' 'type' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_hem' @{ points = '[[5,5,5]]'; hem_type = 'closed'; length = 0.01 }) 'EDGE_NOT_FOUND' 'point off the part' | Out-Null
    $r = Ok (Invoke-Tool 'sheet_metal_hem' @{ points = '[[0,0.03,0.025]]'; hem_type = 'closed'; length = 0.01 }) 'hem by a point on the edge'; Assert ((Rg $r).verified -eq $true) 'point-addressed hem'; Del-Feature (Rg $r).feature
}

Test 'F07 break corner: fillet and chamfer on a wall face (+2 faces each), option flags read back' {
    foreach ($kind in 'fillet', 'chamfer') {
        $r = Ok (Invoke-Tool 'sheet_metal_break_corner' @{ faces = "[$script:wallFace]"; corner_type = $kind; distance = 0.003 }) $kind; $g = Rg $r; Assert ($g.verified -eq $true -and $g.feature_error -eq 0) "$kind : $($g | ConvertTo-Json -Compress -Depth 4)"
        Assert ($g.body_after.faces -eq $g.body_before.faces + 2) "$kind faces $($g.body_before.faces) -> $($g.body_after.faces)"; Assert ($g.definition.BreakType -eq $(if ($kind -eq 'fillet') { 0 } else { 1 })) 'BreakType'; Near $g.definition.Distance 0.003 1e-9 'Distance'; Del-Feature $g.feature }
    $f = Rg (Ok (Invoke-Tool 'sheet_metal_break_corner' @{ faces = "[$script:wallFace]"; corner_type = 'chamfer'; distance = 0.003; internal_corners_only = $true; centered_on_bend_lines = $true }) 'flags'); Assert ($f.verified -eq $true -and $f.definition.InternalCornersOnly -eq $true -and $f.definition.CenteredOnBendLines -eq $true) 'flags did not read back'; Del-Feature $f.feature
}

Test 'F08 break corner errors: no distance, bad type, no target, a face that cannot be broken; nothing created and state_version unchanged' {
    $sv = StateVer
    Fail (Invoke-Tool 'sheet_metal_break_corner' @{ faces = "[$script:wallFace]" }) 'MISSING_PARAMETER' 'distance' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_break_corner' @{ faces = "[$script:wallFace]"; corner_type = 'round'; distance = 0.003 }) 'INVALID_PARAMETER' 'type' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_break_corner' @{ distance = 0.003 }) 'MISSING_PARAMETER' 'no target' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_break_corner' @{ points = '[[0,0,0]]'; distance = 0.003 }) 'BREAK_CORNER_FAILED' 'base flange face' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_break_corner' @{ faces = '[99999]'; distance = 0.003 }) 'FACE_NOT_FOUND' 'range' | Out-Null
    Assert ((StateVer) -eq $sv) 'failed calls must not change state_version'
    Assert (@((Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.type -eq 'BreakCorner' }).Count -eq 0) 'a failed call left a Break-Corner behind'
}

Test 'F09 the tools do not depend on the SolidWorks VIEW: hem + break corner from the back, bottom and left views' {
    foreach ($view in '*Back', '*Bottom', '*Left') {
        Set-View $view
        $h = Ok (Invoke-Tool 'sheet_metal_hem' @{ points = '[[0,0.03,0.025]]'; hem_type = 'closed'; length = 0.01 }) "hem ($view)"; Near (Body $h).y 0.0616 5e-5 "hem in $view grew the wrong side"; Del-Feature (Rg $h).feature
        $b = Ok (Invoke-Tool 'sheet_metal_break_corner' @{ points = '[[0,0.03,0.0125]]'; corner_type = 'fillet'; distance = 0.003 }) "break corner ($view)"; Assert ((Rg $b).verified -eq $true) "break corner in $view"; Del-Feature (Rg $b).feature }
    Set-View '*Isometric'
}

Test 'F10 idempotency: a replayed forming call is DUPLICATE and builds nothing twice' {
    $op = [guid]::NewGuid().ToString('N'); $send = { param($json) (Invoke-RestMethod -Method Post -ContentType 'application/json' -Body $json "$script:ToolBase/api/tool/execute" -TimeoutSec 120) }
    $body = { param($sv) @{ operationId = $op; tool = 'sheet_metal_hem'; stateVersion = $sv; params = @{ edges = "[$script:outerTop]"; hem_type = 'closed'; length = 0.01 } } | ConvertTo-Json -Depth 8 -Compress }
    $a = & $send (& $body (StateVer)); Assert ($a.status -eq 'COMPLETED') "first: $($a.status) $($a.error.message)"; $b = & $send (& $body (StateVer)); Assert ($b.status -eq 'DUPLICATE') "replay gave $($b.status)"
    Assert (@((Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.type -eq 'Hem' }).Count -eq 1) 'the replay built a second hem'; Del-Feature $a.result_geometry.feature
}

Test 'F11 miter flange on a plate: ONE feature, four mitred bends, the closed loop in either direction (20 mm, material inside)' {
    New-Plate; $edges = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2]) -lt 1e-6 }; Assert (@($edges).Count -eq 4) "top edges: $(@($edges).Count)"
    $ccw = (@($edges | Sort-Object { [math]::Atan2($_.mid[1], $_.mid[0]) }) | ForEach-Object { $_.i }) -join ','; $cw = (@($edges | Sort-Object { -[math]::Atan2($_.mid[1], $_.mid[0]) }) | ForEach-Object { $_.i }) -join ','
    foreach ($order in $ccw, $cw) {
        $r = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = "[$order]"; height = 0.02 }) "miter ($order)"; $g = Rg $r; $b = Body $r
        Assert ($g.verified -eq $true -and $g.feature_error -eq 0) "report: $($g | ConvertTo-Json -Compress -Depth 5)"; Assert ($b.faces -eq 38) "faces $($b.faces)"; Near $b.z 0.0215 5e-5 'height = 20 + 1.5'; Near $b.x 0.100 5e-5 'material inside keeps x'; Near $b.y 0.060 5e-5 'material inside keeps y'
        Assert (@($g.definition.Edges).Count -eq 4 -and $g.definition.PositionType -eq 1) 'definition'; Near $g.definition.GapDistance 0.0000762 1e-9 'rip gap default 0.0762 mm'; Assert ($null -ne $g.profile_drawn.sketch) 'the drawn profile must be reported'
        Del-Feature $g.feature }
}

Test 'F12 miter flange options: material_outside widens by 2 x thickness, 45 degrees flares out, bend_radius, an open chain of 3 edges, one edge' {
    New-Plate
    $edges = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2]) -lt 1e-6 }; $ccw = @($edges | Sort-Object { [math]::Atan2($_.mid[1], $_.mid[0]) } | ForEach-Object { $_.i }); $loop = "[$($ccw -join ',')]"
    $o = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02; position = 'material_outside' }) 'outside'; Near (Body $o).x 0.103 5e-5 'material outside: x + 2 x 1.5'; Del-Feature (Rg $o).feature
    $f = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.015; angle = 45 }) '45 deg'; Assert ((Body $f).x -gt 0.115 -and (Body $f).z -lt 0.0165) 'a 45 degree flange flares out and is lower'; Del-Feature (Rg $f).feature
    $rr = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02; position = 'bend_outside'; bend_radius = 0.003 }) 'radius'; Near (Rg $rr).definition.BendRadius 0.003 1e-9 'BendRadius'; Assert ((Rg $rr).definition.UseDefaultBendRadius -eq $false) 'UseDefaultBendRadius'; Del-Feature (Rg $rr).feature
    $three = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = "[$(($ccw | Select-Object -First 3) -join ',')]"; height = 0.02 }) '3 edges'; Assert ((Body $three).faces -eq 30) "3-edge faces $((Body $three).faces)"; Del-Feature (Rg $three).feature
    $one = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = "[$($ccw[0])]"; height = 0.02 }) '1 edge'; Assert ((Body $one).faces -eq 14) "1-edge faces $((Body $one).faces)"; Del-Feature (Rg $one).feature
    $k = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02; k_factor = 0.42 }) 'K'; Assert ((Rg $k).definition.UseDefaultBendAllowance -eq $false) 'a custom K must switch off the default allowance'; Del-Feature (Rg $k).feature
}

Test 'F13 miter flange errors: no height, no edges, bad position, silly rip gap / radius, angle 0, a vertical edge; failed calls leave no sketch behind' {
    New-Plate
    $edges = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2]) -lt 1e-6 }; $loop = "[$((@($edges | ForEach-Object { $_.i })) -join ',')]"
    function Sketch-Count { @((Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.type -eq 'ProfileFeature' }).Count }
    $s0 = Sketch-Count; $sv = StateVer
    Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop }) 'MISSING_PARAMETER' 'no height' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ height = 0.02 }) 'MISSING_PARAMETER' 'no edges' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02; position = 'sideways' }) 'INVALID_PARAMETER' 'position' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02; rip_gap = 0.05 }) 'INVALID_PARAMETER' 'a 50 mm rip gap' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02; bend_radius = 5 }) 'INVALID_PARAMETER' 'a 5 m radius' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; profile_sketch = 'Nope9' }) 'FEATURE_NOT_FOUND' 'unknown profile sketch' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02; angle = 0 }) 'MITER_FLANGE_FAILED' 'angle 0 (flat extension)' | Out-Null
    $v = Fail (Invoke-Tool 'sheet_metal_miter_flange' @{ points = '[[0.05,0.03,-0.00075]]'; height = 0.02 }) 'MITER_FLANGE_FAILED' 'a vertical corner edge'; Assert ($v.error.message -like '*Profile drawn*') 'the failure must report the profile it drew'
    Assert ((Sketch-Count) -eq $s0) "profile sketches $s0 -> $(Sketch-Count): a failed call left its sketch behind"; Assert ((StateVer) -eq $sv) 'failed calls must not change state_version'
}

Test 'F14 the mitre box flattens: the flat pattern rebuilds and the blank is larger than the plate (flange + bend allowance on every side)' {
    New-Plate
    $edges = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2]) -lt 1e-6 }; $loop = "[$((@($edges | ForEach-Object { $_.i })) -join ',')]"
    $m = Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; height = 0.02 }) 'miter'
    $f = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flat'); Assert ($f.verified -eq $true) 'flat pattern'; Near $f.flat.size[2] 0.0015 1e-6 'flat thickness'
    Assert ($f.flat.size[0] -gt 0.100 + 0.036 -and $f.flat.size[1] -gt 0.060 + 0.036) "flat blank $(Mm $f.flat.size[0]) x $(Mm $f.flat.size[1]) mm"
    $p = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list'); $mf = $p.features | Where-Object { $_.type -eq 'SMMiteredFlange' }; Assert ($mf.definition -eq 'IMiterFlangeFeatureData') 'the mitre flange must be a known definition'
    Del-Feature (Rg $m).feature
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
"" ; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail


