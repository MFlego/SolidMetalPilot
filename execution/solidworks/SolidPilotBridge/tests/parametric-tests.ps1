# Regression tests for the parametric building blocks (document_units, equations, sketch_center_rectangle, the dimensioned mitre profile) and for the cabinet they make possible: the NEMA cabinet body of
# Path B built ONLY through tools, compared with the user's own reference enclosure_nema_cabinet_dfm_cr_steel_16ga_copy_v2 (flat blank 27.931 x 21.931 in, outer cutting length 95.821 in, 12 bends).
# Needs SolidWorks running with the bridge, NOTHING else open (every document is closed at the end), tools loaded. Usage: parametric-tests.ps1 [-Force] [-Json out.json] [-Only 'P03']
# -Only runs the tests whose name matches the regex (P06 needs the cabinet P05 builds: run them together, e.g. -Only 'P0[56]').
param([switch]$Force, [string]$Json = '', [string]$Only = '')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\probes\_common.ps1"
. "$PSScriptRoot\..\probes\_tools.ps1"
$script:results = New-Object System.Collections.ArrayList
$script:tmpDirs = @()
function Test([string]$Name, [scriptblock]$Body) {
    if ($Only -and $Name -notmatch $Only) { return }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $true; $why = ''
    try { & $Body } catch { $ok = $false; $why = $_.Exception.Message }
    "{0}  {1,-88} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.ElapsedMilliseconds, $why
    [void]$script:results.Add([pscustomobject]@{ name = $Name; pass = $ok; ms = $sw.ElapsedMilliseconds; why = $why })
}
function Assert($cond, [string]$msg) { if (-not $cond) { throw $msg } }
function Near([double]$a, [double]$b, [double]$tol, [string]$what) { if ([math]::Abs($a - $b) -gt $tol) { throw "$what : $a vs $b (tolerance $tol)" } }
function StateVer { [int](Invoke-RestMethod "$script:ToolBase/api/tool/state" -TimeoutSec 30).stateVersion }
function Rg($reply) { $reply.result_geometry }
function Ok($reply, [string]$what) { Assert ($reply.status -eq 'COMPLETED') "$what : $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Fail($reply, [string]$code, [string]$what) { Assert ($reply.status -eq 'FAILED' -and $reply.error.code -eq $code) "$what : expected FAILED/$code, got $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Sw { Get-Process SLDWORKS | Select-Object -First 1 }
function Fresh-Part { Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null; Ok (Invoke-Tool 'open_new_part' @{}) 'new part' | Out-Null }
function Params { Rg (Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'get' }) 'sheet_metal_params get') }
function Eq($action, $p = @{}) { $p['action'] = $action; Invoke-Tool 'equations' $p }
function Snap {                                    # folded size (in), flat blank (in, from the cut list), cutting length, bends, thickness (in)
    $p = Params; $c = $p.cut_list[0].properties; $f = (Rg (Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flat')).folded.size
    [pscustomobject]@{ x = $f[0] / 0.0254; y = $f[1] / 0.0254; z = $f[2] / 0.0254; blankL = [double]$c.'Bounding Box Length'; blankW = [double]$c.'Bounding Box Width'; cut = [double]$c.'Cutting Length-Outer'; bends = [int]$c.Bends; t = $p.global.thickness / 0.0254; units = $p.units.length }
}
function Build-Cabinet([switch]$NoProfileDims) {   # the Path B cabinet from tools only; returns the profile sketch's name
    Fresh-Part
    Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'in' }) 'units' | Out-Null
    Ok (Eq 'set' @{ variables = '{"BoxWidth":"12in","BoxHeight":"18in","SheetThickness":"0.06in","InsideRadius":"0.18in","KFactor":0.45,"WallHeight":"4in","LipLength":"0.75in","ReturnLip":"0.5in"}' }) 'variables' | Out-Null
    $s = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.3048; height = 0.4572 }) 'master sketch'); $sk = $s.sketch
    Ok (Eq 'set' @{ links = "{`"D1@${sk}`":`"BoxWidth`",`"D2@${sk}`":`"BoxHeight`"}" }) 'sketch links' | Out-Null
    Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.001524; bend_radius = 0.004572; k_factor = 0.45 }) 'base flange' | Out-Null
    Ok (Eq 'set' @{ links = '{"Thickness@Sheet-Metal":"SheetThickness","D1@Sheet-Metal":"InsideRadius","D2@Sheet-Metal1":"KFactor"}' }) 'sheet-metal links' | Out-Null
    $e = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2]) -lt 1e-6 } | Sort-Object { [math]::Atan2($_.mid[1], $_.mid[0]) }; $loop = "[$((@($e | ForEach-Object { $_.i })) -join ',')]"
    $m = Rg (Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; profile_points = '[[0,0.1016],[0.01905,0.1016],[0.01905,0.1143]]'; rip_gap = 0.000254; trim_side_bends = $false; dimension_profile = (-not $NoProfileDims) }) 'mitre flange')
    if (-not $NoProfileDims) { $d = @($m.profile_drawn.defined.dimensions); Ok (Eq 'set' @{ links = "{`"$($d[0])`":`"WallHeight`",`"$($d[1])`":`"LipLength`",`"$($d[2])`":`"ReturnLip`"}" }) 'profile links' | Out-Null }
    $m
}

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== parametric building blocks and the Path B cabinet  ($script:ToolBase)"

Test 'P01 document_units: get / set / read-back, 3 decimals for inches by default, bad input refused; the cut list follows the unit' {
    Fresh-Part
    $g = Rg (Ok (Invoke-Tool 'document_units' @{ action = 'get' }) 'get'); Assert ($g.length -eq 'mm' -and $g.decimals -eq 2) "template unit: $($g | ConvertTo-Json -Compress)"
    $i = Rg (Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'in' }) 'set in'); Assert ($i.length -eq 'in' -and $i.decimals -eq 3 -and $i.verified -eq $true) "inches: $($i | ConvertTo-Json -Compress)"; Near $i.units_per_meter (1 / 0.0254) 1e-9 'units per meter'
    $m = Rg (Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'mm'; decimals = 4 }) 'set mm'); Assert ($m.length -eq 'mm' -and $m.decimals -eq 4 -and $m.verified -eq $true) 'mm / 4 decimals'
    Ok (Invoke-Tool 'document_units' @{ action = 'set'; decimals = 2 }) 'decimals only' | Out-Null
    Fail (Invoke-Tool 'document_units' @{ action = 'set'; length = 'furlong' }) 'INVALID_PARAMETER' 'unit' | Out-Null; Fail (Invoke-Tool 'document_units' @{ action = 'set'; decimals = 12 }) 'INVALID_PARAMETER' 'decimals' | Out-Null; Fail (Invoke-Tool 'document_units' @{ action = 'set' }) 'MISSING_PARAMETER' 'nothing' | Out-Null
}

Test 'P02 equations: variables + links, an existing name is UPDATED not duplicated, delete, a refused equation is named, list values in the document unit' {
    Fresh-Part; Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'in' }) 'units' | Out-Null
    $r = Rg (Ok (Eq 'set' @{ variables = '{"BoxWidth":"12in","KFactor":0.45}' }) 'set'); Assert ($r.verified -eq $true -and $r.count -eq 2 -and $r.unit -eq 'in') "set: $($r | ConvertTo-Json -Compress -Depth 4)"
    $bw = $r.equations | Where-Object { $_.name -eq 'BoxWidth' }; Near $bw.value 12 1e-9 'BoxWidth in inches'; Assert ($bw.global_variable -eq $true) 'a variable is a global variable'; Assert ($bw.text -eq '"BoxWidth"= 12in') "equation text: $($bw.text)"
    $u = Rg (Ok (Eq 'set' @{ variables = '{"BoxWidth":"14in"}' }) 'update'); Assert ($u.count -eq 2) "update must not duplicate: $($u.count) equations"; Near (($u.equations | Where-Object { $_.name -eq 'BoxWidth' }).value) 14 1e-9 'updated value'
    $x = Rg (Ok (Eq 'set' @{ variables = '{"Twice":"\"BoxWidth\" * 2"}' }) 'expression'); Near (($x.equations | Where-Object { $_.name -eq 'Twice' }).value) 28 1e-9 'an expression over another variable'
    $d = Rg (Ok (Eq 'delete' @{ delete = 'Twice' }) 'delete (comma list)'); Assert ($d.count -eq 2 -and -not ($d.equations | Where-Object { $_.name -eq 'Twice' })) 'Twice not deleted'
    $d2 = Rg (Ok (Eq 'set' @{ delete = '["KFactor"]' }) 'delete (array in set)'); Assert ($d2.count -eq 1) 'KFactor not deleted'
    $miss = Rg (Ok (Eq 'delete' @{ delete = 'Nope' }) 'delete unknown'); Assert ($miss.verified -eq $false -and ($miss.problems -join ';') -like "*no equation named 'Nope'*") 'deleting an unknown name must be reported'
    $bad = Rg (Ok (Eq 'set' @{ links = '{"D1@NoSuchSketch":"BoxWidth"}' }) 'link to a missing dimension'); Assert ($bad.verified -eq $false -and ($bad.problems -join ';') -like '*D1@NoSuchSketch*') "a refused equation must be named: $($bad | ConvertTo-Json -Compress -Depth 4)"
    Fail (Eq 'set' @{}) 'MISSING_PARAMETER' 'nothing to set' | Out-Null; Fail (Eq 'explode' @{}) 'INVALID_PARAMETER' 'bad action' | Out-Null; Fail (Eq 'delete' @{}) 'MISSING_PARAMETER' 'delete without names' | Out-Null
}

Test 'P03 sketch_center_rectangle: FULLY DEFINED (D1 width, D2 height, centre on the origin), dimensions follow equations, errors' {
    Fresh-Part; Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'in' }) 'units' | Out-Null
    $s = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.3048; height = 0.4572 }) 'rectangle')
    Assert ($s.constrained -eq 'fully_defined' -and $s.verified -eq $true -and $s.constrained_status -eq 3) "status: $($s | ConvertTo-Json -Compress)"; Assert (($s.dimensions -join ',') -eq 'D1@Sketch1,D2@Sketch1') "dimension names: $($s.dimensions -join ',')"
    Fail (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.1; height = 0.1 }) 'SKETCH_ALREADY_ACTIVE' 'a second sketch while one is active' | Out-Null
    $l = Rg (Ok (Eq 'set' @{ variables = '{"BoxWidth":"12in","BoxHeight":"18in"}'; links = '{"D1@Sketch1":"BoxWidth","D2@Sketch1":"BoxHeight"}' }) 'link'); Assert ($l.verified -eq $true) 'links'
    Near (($l.equations | Where-Object { $_.name -eq 'D1@Sketch1' }).value) 12 1e-9 'D1 = BoxWidth'
    Ok (Eq 'set' @{ variables = '{"BoxWidth":"13in"}' }) 'resize' | Out-Null; Near (($(Eq 'list').result_geometry.equations | Where-Object { $_.name -eq 'D1@Sketch1' }).value) 13 1e-9 'the sketch follows the variable'
    Ok (Eq 'set' @{ variables = '{"BoxWidth":"12in"}' }) 'restore' | Out-Null
    Fresh-Part; Fail (Invoke-Tool 'sketch_center_rectangle' @{ width = 0; height = 0.1 }) 'MISSING_PARAMETER' 'width 0' | Out-Null; Fail (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.1; height = 0.1; plane = 'Nope Plane' }) 'PLANE_NOT_FOUND' 'plane' | Out-Null
}

function SketchSize([string]$name) {              # width x height (m) of a sketch's real (non-construction) lines, read independently of the tool that drew it
    $s = Invoke-Tool 'analyze_model' @{ analysis_type = 'sketch'; name = $name }; $j = $s.cadState.features[0] | ConvertFrom-Json; $xs = @(); $ys = @()
    foreach ($g in $j.sketch.segments) { if (-not $g.construction) { $xs += $g.x1, $g.x2; $ys += $g.y1, $g.y2 } }
    @((($xs | Measure-Object -Maximum).Maximum - ($xs | Measure-Object -Minimum).Minimum), (($ys | Measure-Object -Maximum).Maximum - ($ys | Measure-Object -Minimum).Minimum))
}

Test 'P03b a rectangle in a part that ALREADY HAS geometry is exactly the size asked: sketch inference must not snap it to model edges (it did: 0.3 x 0.45 became 0.2987 x 0.4492)' {
    Fresh-Part
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.3048; height = 0.4572 }) 'plate sketch' | Out-Null
    Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.001524; bend_radius = 0.004572 }) 'plate' | Out-Null
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = 0.05 }) 'offset plane' | Out-Null
    $s = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Plane1'; width = 0.30; height = 0.45 }) 'a rectangle whose corners lie 2-4 mm inside the plate edges')
    $z = SketchSize $s.sketch; Near $z[0] 0.30 1e-6 'width read back from the sketch'; Near $z[1] 0.45 1e-6 'height read back from the sketch'
    Assert ($s.verified -eq $true -and $s.constrained -eq 'fully_defined') "result: $($s | ConvertTo-Json -Compress)"
    Near $s.size[0] 0.30 1e-6 'width reported by the tool'; Near $s.size[1] 0.45 1e-6 'height reported by the tool'
    # D1 / D2 must DRIVE the size: an equation on them re-sizes the rectangle
    Ok (Eq 'set' @{ variables = '{"W2":"250mm"}'; links = "{`"D1@$($s.sketch)`":`"W2`"}" }) 'drive D1' | Out-Null
    $z2 = SketchSize $s.sketch; Near $z2[0] 0.25 1e-6 'the width follows its equation'; Near $z2[1] 0.45 1e-6 'the height stays'
}

Test 'P03c sketch_center_rectangle with an OFF-ORIGIN centre (center_x / center_y): fully defined by one more driving dimension per offset (a base 0.040 in off-centre under a cover, Phase 7), the offsets drive' {
    Fresh-Part
    # Y offset only: D3 = the offset, the centre on the Y axis by a relation (sgVERTPOINTS2D)
    $a = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.256286; height = 0.144018; center_y = -0.001016 }) 'y offset')
    Assert ($a.constrained -eq 'fully_defined' -and $a.verified -eq $true) "y offset: $($a | ConvertTo-Json -Compress)"; Assert (($a.dimensions -join ',') -eq 'D1@Sketch1,D2@Sketch1,D3@Sketch1') "dimension names: $($a.dimensions -join ',')"
    Near $a.size[0] 0.256286 1e-6 'width'; Near $a.size[1] 0.144018 1e-6 'height'
    $sg = (Invoke-Tool 'analyze_model' @{ analysis_type = 'sketch'; name = 'Sketch1' }).cadState.features[0] | ConvertFrom-Json
    $ys = @($sg.sketch.segments | Where-Object { -not $_.construction } | ForEach-Object { $_.y1; $_.y2 }); $xs = @($sg.sketch.segments | Where-Object { -not $_.construction } | ForEach-Object { $_.x1; $_.x2 })
    Near (($ys | Measure-Object -Minimum).Minimum) (-0.001016 - 0.072009) 1e-7 'bottom edge'; Near (($ys | Measure-Object -Maximum).Maximum) (-0.001016 + 0.072009) 1e-7 'top edge'; Near (($xs | Measure-Object -Maximum).Maximum + ($xs | Measure-Object -Minimum).Minimum) 0 1e-9 'symmetric about the Y axis'
    # D3 DRIVES the offset
    Ok (Eq 'set' @{ variables = '{"Off":"0.5in"}'; links = '{"D3@Sketch1":"Off"}' }) 'drive D3' | Out-Null
    $sg2 = (Invoke-Tool 'analyze_model' @{ analysis_type = 'sketch'; name = 'Sketch1' }).cadState.features[0] | ConvertFrom-Json
    $ys2 = @($sg2.sketch.segments | Where-Object { -not $_.construction } | ForEach-Object { $_.y1; $_.y2 }); Near ((($ys2 | Measure-Object -Maximum).Maximum + ($ys2 | Measure-Object -Minimum).Minimum) / 2) (-0.0127) 1e-7 'the centre follows D3 (0.5 in below the origin)'
    # both offsets, X only, and the plain centre rectangle is unchanged
    Fresh-Part; $b = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.10; height = 0.06; center_x = 0.02; center_y = -0.01 }) 'both offsets')
    Assert ($b.constrained -eq 'fully_defined' -and $b.verified -eq $true -and ($b.dimensions -join ',') -eq 'D1@Sketch1,D2@Sketch1,D3@Sketch1,D4@Sketch1') "both offsets: $($b | ConvertTo-Json -Compress)"
    Fresh-Part; $c = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.10; height = 0.06; center_x = 0.02 }) 'x offset only')
    Assert ($c.constrained -eq 'fully_defined' -and $c.verified -eq $true -and ($c.dimensions -join ',') -eq 'D1@Sketch1,D2@Sketch1,D3@Sketch1') "x offset: $($c | ConvertTo-Json -Compress)"
    Fresh-Part; $d = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.10; height = 0.06 }) 'no offset'); Assert ($d.constrained -eq 'fully_defined' -and ($d.dimensions -join ',') -eq 'D1@Sketch1,D2@Sketch1' -and -not $d.center) "unchanged default: $($d | ConvertTo-Json -Compress)"
    # the offset body feeds a base flange: the two-body part of Phase 7 (cover over a base that is 0.040 in off-centre)
    Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.256286; height = 0.14605; center_y = -0.001016 }) 'plate sketch' | Out-Null
    Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.00127; bend_radius = 0.001905; k_factor = 0.46 }) 'base flange' | Out-Null
    $bx = (Rg (Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flat')).folded.size; Near $bx[0] 0.256286 1e-6 'plate X'; Near $bx[1] 0.14605 1e-6 'plate Y'
}

Test 'P03d sketch_fully_define (user rule: EVERY sketch fully defined, even hole sketches): an off-origin rectangle + circles, a polyline, a profile with an arc, circles ON the axes and a corner on the origin all end at status 3 (dimensions to the origin, redundant ones deleted, relations for points on an axis); an already defined sketch is a no-op; errors' {
    function DefineSketch($segs) {
        Fresh-Part; Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch' | Out-Null
        Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $segs }) 'entities' | Out-Null
        $r = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{}) 'sketch_fully_define'); Assert ($r.status_before -eq 'under_defined') "before: $($r.status_before)"
        Assert ($r.constrained -eq 'fully_defined' -and $r.verified -eq $true -and $r.constrained_status -eq 3) "not fully defined: $($r | ConvertTo-Json -Compress -Depth 5)"; $r
    }
    $r = DefineSketch @(@{ entity_type = 'rectangle'; x1 = -0.05; y1 = -0.03; x2 = 0.05; y2 = 0.03 }, @{ entity_type = 'circle'; cx = -0.02; cy = 0.01; radius = 0.004 }, @{ entity_type = 'circle'; cx = 0.025; cy = -0.012; radius = 0.003 })
    Assert (@($r.dimensions).Count -eq 10) "off-origin rectangle + two circles = 10 dimensions: $(@($r.dimensions).Count)"; Assert ($r.redundant_dimensions_removed -ge 1) 'a redundant (driven) dimension must have been removed again'
    $vals = @($r.dimensions | ForEach-Object { [math]::Round($_.value * 1000, 3) }); Assert (($vals -contains 8) -and ($vals -contains 6)) "the two circle diameters 8 and 6 mm are among the dimensions: $($vals -join ',')"
    $r = DefineSketch @(@{ entity_type = 'line'; x1 = 0.01; y1 = 0.01; x2 = 0.06; y2 = 0.01 }, @{ entity_type = 'line'; x1 = 0.06; y1 = 0.01; x2 = 0.06; y2 = 0.04 }, @{ entity_type = 'line'; x1 = 0.06; y1 = 0.04; x2 = 0.03; y2 = 0.05 }, @{ entity_type = 'line'; x1 = 0.03; y1 = 0.05; x2 = 0.01; y2 = 0.05 }, @{ entity_type = 'line'; x1 = 0.01; y1 = 0.05; x2 = 0.01; y2 = 0.01 })
    Assert (@($r.dimensions).Count -eq 10) "five-point polyline = 10 ordinates: $(@($r.dimensions).Count)"
    $r = DefineSketch @(@{ entity_type = 'line'; x1 = 0.0; y1 = 0.0; x2 = 0.04; y2 = 0.0 }, @{ entity_type = 'arc_center'; cx = 0.04; cy = 0.01; x1 = 0.04; y1 = 0.0; x2 = 0.05; y2 = 0.01; direction = 1 }, @{ entity_type = 'line'; x1 = 0.05; y1 = 0.01; x2 = 0.05; y2 = 0.04 }, @{ entity_type = 'line'; x1 = 0.05; y1 = 0.04; x2 = 0.0; y2 = 0.04 }, @{ entity_type = 'line'; x1 = 0.0; y1 = 0.04; x2 = 0.0; y2 = 0.0 })
    Assert (@($r.relations).Count -ge 1) "points on the axes get relations: $(@($r.relations) -join '; ')"
    $r = DefineSketch @(@{ entity_type = 'circle'; cx = 0.0; cy = 0.0; radius = 0.005 }, @{ entity_type = 'circle'; cx = 0.03; cy = 0.0; radius = 0.004 }, @{ entity_type = 'circle'; cx = 0.0; cy = -0.02; radius = 0.003 })
    Assert (@($r.relations).Count -eq 3) "three axis relations (coincident, horizontal points, vertical points): $(@($r.relations) -join '; ')"; Assert (($r.relations -join ' ') -match 'sgVERTICALPOINTS2D') 'the vertical-points relation is sgVERTICALPOINTS2D'
    $r = DefineSketch @(@{ entity_type = 'rectangle'; x1 = 0.0; y1 = 0.0; x2 = 0.1; y2 = 0.06 }); Assert (@($r.dimensions).Count -eq 2 -and @($r.relations).Count -eq 1) "a corner on the origin: 2 dimensions + 1 coincident relation: $($r | ConvertTo-Json -Compress -Depth 5)"
    # an already fully defined sketch is a no-op that still reports verified
    Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.1; height = 0.06 }) 'centre rectangle' | Out-Null
    $n = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{}) 'no-op'); Assert ($n.verified -eq $true -and @($n.dimensions).Count -eq 0 -and $n.status_before -eq 'fully_defined') "no-op: $($n | ConvertTo-Json -Compress -Depth 5)"
    # sketch_center_rectangle with the centre on the Y axis ties it with sgVERTICALPOINTS2D (sgVERTPOINTS2D is a silent no-op)
    Fresh-Part; $c = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.1; height = 0.06; center_y = 0.05 }) 'centre on the Y axis'); Assert ($c.constrained -eq 'fully_defined' -and $c.verified -eq $true) "centre on the Y axis: $($c | ConvertTo-Json -Compress)"
    # the pure error cases
    Fresh-Part; Fail (Invoke-Tool 'sketch_fully_define' @{}) 'NO_ACTIVE_SKETCH' 'no sketch' | Out-Null
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch' | Out-Null; Fail (Invoke-Tool 'sketch_fully_define' @{ sketch = 'Sketch99' }) 'SKETCH_ALREADY_ACTIVE' 'another sketch is active' | Out-Null
}

Test 'P03d2 sketch_fully_define relations=first / only + relate (MASTER SKETCH rule 2026-11-02: relations cut dimensions): a two-contour master section (tray with a jog + cover) needs 24 origin dimensions with relations=none but at most 10 with relations=first (a construction centre line on the Y axis, symmetric pairs, horizontal / vertical lines), only leaves it under defined, an explicit collinear relation removes one more dimension, a bogus mode is refused' {
    function MasterSegs {
        $h = 0.215; $f = -0.022225; $s = 0.001103; $jx = 0.2139; $jy = 0.004125; $wt = 0.020725; $ct = 0.022225; $sk = 0.004225
        function L($a, $b) { @{ entity_type = 'line'; x1 = $a[0]; y1 = $a[1]; x2 = $b[0]; y2 = $b[1] } }
        @((L @(-$h, $f) @($h, $f)), (L @($h, $f) @($h, $s)), (L @($h, $s) @($jx, $jy)), (L @($jx, $jy) @($jx, $wt)), (L @(-$h, $f) @(-$h, $s)), (L @(-$h, $s) @(-$jx, $jy)), (L @(-$jx, $jy) @(-$jx, $wt)),
          (L @(-$h, $ct) @($h, $ct)), (L @($h, $ct) @($h, $sk)), (L @(-$h, $ct) @(-$h, $sk)))
    }
    function Section { Fresh-Part; Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch' | Out-Null; Ok (Invoke-Tool 'add_sketch_entities' @{ segments = (MasterSegs) }) 'section' | Out-Null }
    Section; $n = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{}) 'relations none'); Assert ($n.verified -eq $true -and @($n.dimensions).Count -eq 24) "origin-only: 24 dimensions expected, got $(@($n.dimensions).Count)"
    Section; $f = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{ relations = 'first' }) 'relations first')
    Assert ($f.verified -eq $true -and $f.constrained_status -eq 3) "relations first not fully defined: $($f | ConvertTo-Json -Compress -Depth 5)"; Assert (@($f.dimensions).Count -le 10) "relations first: at most 10 dimensions, got $(@($f.dimensions).Count)"
    Assert ((@($f.geometric_relations) -join ' ') -match 'symmetric' -and (@($f.geometric_relations) -join ' ') -match 'vertical' -and (@($f.geometric_relations) -join ' ') -match 'horizontal') "the symmetric / vertical / horizontal relations are listed: $(@($f.geometric_relations) -join '; ')"
    Section; $o = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{ relations = 'only' }) 'relations only'); Assert ($o.verified -eq $false -and @($o.dimensions).Count -eq 0 -and $o.constrained -ne 'fully_defined') "relations only must stop before the dimensions: $($o | ConvertTo-Json -Compress -Depth 5)"
    $d = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{}) 'the rest'); Assert ($d.verified -eq $true) 'closing the rest after relations only'
    Section; $c = Rg (Ok (Invoke-Tool 'sketch_fully_define' @{ relations = 'first'; relate = @(@{ type = 'collinear'; a = @(0.215, -0.01); b = @(0.215, 0.015) }) }) 'relate collinear')
    Assert ($c.verified -eq $true -and @($c.dimensions).Count -lt @($f.dimensions).Count) "a collinear relation between the tray wall and the cover skirt removes a dimension: $(@($c.dimensions).Count) vs $(@($f.dimensions).Count)"
    Fail (Invoke-Tool 'sketch_fully_define' @{ relations = 'bogus' }) 'INVALID_PARAMETER' 'a bogus relations mode' | Out-Null
}

Test 'P03e sketch_convert_entities: the MASTER SURFACE DRIVES the base flange (user rule: master sketches come from the surface construction): master rectangle -> surface extrude -> new sketch -> the master''s four z = 0 edges converted (fully defined, 100 x 60) -> base flange; widening the MASTER to 120 mm moves the flat blank to 120 x 60' {
    Fresh-Part
    $m = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.1; height = 0.06 }) 'master sketch'); $msk = $m.sketch
    Ok (Invoke-Tool 'surface_extrude' @{ sketch = $msk; depth = 0.03 }) 'master surface' | Out-Null
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'target sketch' | Out-Null
    $pts = @(@(0.0, 0.03, 0.0), @(0.0, -0.03, 0.0), @(0.05, 0.0, 0.0), @(-0.05, 0.0, 0.0))
    $c = Rg (Ok (Invoke-Tool 'sketch_convert_entities' @{ points = $pts }) 'convert the master edges')
    Assert ($c.verified -eq $true -and $c.created_segments -eq 4 -and $c.constrained_status -eq 3) "convert: $($c | ConvertTo-Json -Compress -Depth 5)"; Near $c.size[0] 0.1 1e-6 'converted width'; Near $c.size[1] 0.06 1e-6 'converted height'
    Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.00127; bend_radius = 0.001905; k_factor = 0.45 }) 'base flange from the converted sketch' | Out-Null
    $cl = @((Params).cut_list)[0].properties; Near $cl.'Bounding Box Length' 100 1e-3 'blank X (mm)'; Near $cl.'Bounding Box Width' 60 1e-3 'blank Y (mm)'
    Ok (Invoke-Tool 'modify_dimension' @{ name = "D1@$msk"; value = 0.12 }) 'widen the MASTER' | Out-Null
    $cl = @((Params).cut_list)[0].properties; Near $cl.'Bounding Box Length' 120 1e-3 'blank X after the master change (mm)'; Near $cl.'Bounding Box Width' 60 1e-3 'blank Y (mm)'
    # refusals: no active sketch; a point that is on no edge
    Fresh-Part; Fail (Invoke-Tool 'sketch_convert_entities' @{ points = @(,@(0, 0, 0)) }) 'NO_ACTIVE_SKETCH' 'no sketch' | Out-Null
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.1; height = 0.06 }) 'sketch' | Out-Null; Ok (Invoke-Tool 'surface_extrude' @{ sketch = 'Sketch1'; depth = 0.03 }) 'surface' | Out-Null
    Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch 2' | Out-Null; Fail (Invoke-Tool 'sketch_convert_entities' @{ points = @(,@(0.3, 0.3, 0.3)) }) 'EDGE_NOT_FOUND' 'a point on no edge' | Out-Null
}

Test 'P04 an equation change closes the active sketch - the tools put it back; base_flange without any sketch says NO_PROFILE_SKETCH; sketch_name selects one' {
    Fresh-Part
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.1; height = 0.06 }) 'rectangle' | Out-Null
    Ok (Eq 'set' @{ variables = '{"Anything":"1in"}' }) 'an equation call while the sketch is active' | Out-Null
    $v = Invoke-Tool 'verify_state' @{}; Assert ($v.cadState.activeSketch -eq 'Sketch1') "the active sketch after the equation call: [$($v.cadState.activeSketch)]"
    Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.0015; bend_radius = 0.0015; k_factor = 0.5 }) 'base flange from the restored sketch' | Out-Null
    Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.1; height = 0.06 }) 'rectangle' | Out-Null; Ok (Invoke-Tool 'document_units' @{ action = 'set'; decimals = 3 }) 'unit change' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.0015; bend_radius = 0.0015; sketch_name = 'Nope9' }) 'FEATURE_NOT_FOUND' 'unknown sketch_name' | Out-Null
    Ok (Invoke-Tool 'edit_feature' @{ action = 'rename'; feature_name = 'Sketch1'; new_name = 'MasterSketch' }) 'rename' | Out-Null; Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null
}

Test 'P05 THE CABINET (Path B) from tools only matches the user reference: blank 27.931 x 21.931 in, cutting length 95.821 in, 12 bends, 12 x 18 x 4.56 in folded' {
    $m = Build-Cabinet; $g = $m
    Assert ($g.verified -eq $true -and $g.feature_error -eq 0) "mitre flange: $($g | ConvertTo-Json -Compress -Depth 4)"
    $s = Snap; Assert ($s.units -eq 'in') "document unit $($s.units)"
    Near $s.blankL 27.931 5e-4 'flat blank length (in) vs the reference'; Near $s.blankW 21.931 5e-4 'flat blank width (in) vs the reference'; Near $s.cut 95.821 5e-4 'outer cutting length (in) vs the reference'; Assert ($s.bends -eq 12) "bends $($s.bends)"
    Near $s.x 12 1e-4 'folded X'; Near $s.y 18 1e-4 'folded Y'; Near $s.z 4.56 1e-4 'folded depth = 4 + 0.5 + t'; Near $s.t 0.06 1e-6 'thickness (in)'
    $p = Params; Near $p.global.bend_allowance.k_factor 0.45 1e-9 'K'; Near $p.global.bend_radius 0.004572 1e-9 'inside radius (m)'; Assert ($p.global.overrides.bend_allowance -eq $true) 'K override'
    $def = $g.profile_drawn.defined; Assert ($def.constrained -eq 'fully_defined' -and @($def.dimensions).Count -eq 3) "profile sketch: $($def | ConvertTo-Json -Compress)"; Assert ((@($def.dimensions) -join ',') -match '^D1@Sketch\d+,D2@Sketch\d+,D3@Sketch\d+$') "profile dimension names: $(@($def.dimensions) -join ',')"
    Near $g.definition.GapDistance 0.000254 1e-9 'mitre gap 0.010 in'; Assert ($g.definition.UsePositionTrimSideBends -eq $false) 'trim side bends off'
    $q = Rg (Eq 'list'); Assert ($q.count -eq 16) "equations: $($q.count)"; foreach ($n in 'BoxWidth', 'BoxHeight', 'SheetThickness', 'InsideRadius', 'KFactor', 'WallHeight', 'LipLength', 'ReturnLip') { Assert ($null -ne ($q.equations | Where-Object { $_.name -eq $n -and $_.global_variable })) "global variable $n missing" }
    $err = (Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.error }; Assert (-not $err) "rebuild errors: $(($err | ForEach-Object { $_.name }) -join ',')"
}

Test 'P06 the cabinet is RESILIENT: WallHeight, BoxWidth, rim / lip and the gauge are driven by variables; restoring them restores the reference blank' {
    $base = Snap
    Ok (Eq 'set' @{ variables = '{"WallHeight":"5in"}' }) 'wall 5 in' | Out-Null; $a = Snap; Near $a.z 5.56 1e-4 'depth with a 5 in wall'; Near $a.blankL 29.931 5e-4 'blank grows by 2 in'; Assert ($a.bends -eq 12) 'still 12 bends'
    Ok (Eq 'set' @{ variables = '{"WallHeight":"4in","BoxWidth":"14in"}' }) 'width 14 in' | Out-Null; $b = Snap; Near $b.x 14 1e-4 'folded X follows BoxWidth'; Near $b.z 4.56 1e-4 'depth restored'; Assert ($b.blankW -gt $base.blankW + 1.9) "the blank width must grow with the box: $($b.blankW)"
    Ok (Eq 'set' @{ variables = '{"BoxWidth":"12in","LipLength":"1in","ReturnLip":"0.75in"}' }) 'rim 1 in / lip 0.75 in' | Out-Null; $c = Snap; Near $c.z 4.81 1e-3 'depth = 4 + 0.75 + t'
    Ok (Eq 'set' @{ variables = '{"LipLength":"0.75in","ReturnLip":"0.5in","SheetThickness":"0.0747in","InsideRadius":"0.2in"}' }) '14 gauge' | Out-Null; $d = Snap; Near $d.t 0.0747 1e-4 'the gauge follows SheetThickness'
    Ok (Eq 'set' @{ variables = '{"SheetThickness":"0.06in","InsideRadius":"0.18in"}' }) '16 gauge again' | Out-Null; $e = Snap
    Near $e.blankL $base.blankL 5e-4 'blank length restored'; Near $e.blankW $base.blankW 5e-4 'blank width restored'; Near $e.cut $base.cut 5e-4 'cutting length restored'; Near $e.z $base.z 1e-4 'depth restored'
    $err = (Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.error }; Assert (-not $err) "rebuild errors after the round trip: $(($err | ForEach-Object { $_.name }) -join ',')"
}

Test 'P07 the flat DXF is written in the DOCUMENT UNIT: inches ($INSUNITS 1, extents 21.931 x 27.931) and millimetres (4)' {
    $dir = Join-Path $env:TEMP ('par_' + [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory $dir | Out-Null; $script:tmpDirs += $dir
    Ok (Invoke-Tool 'save_document' @{ file_path = "$dir\cabinet_test.SLDPRT" }) 'save' | Out-Null
    Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = "$dir\flat_in.dxf"; dxf_flags = 5 }) 'dxf in inches' | Out-Null
    Ok (Invoke-Tool 'document_units' @{ action = 'set'; length = 'mm' }) 'mm' | Out-Null; Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = "$dir\flat_mm.dxf"; dxf_flags = 5 }) 'dxf in mm' | Out-Null
    $rd = { param($file) $t = Get-Content $file -Raw; [pscustomobject]@{ ins = [int][regex]::Match($t, '\$INSUNITS\s+70\s+(\d+)').Groups[1].Value; maxx = [double][regex]::Match($t, '\$EXTMAX\s+10\s+([-\d.E+]+)\s+20\s+([-\d.E+]+)').Groups[1].Value; maxy = [double][regex]::Match($t, '\$EXTMAX\s+10\s+([-\d.E+]+)\s+20\s+([-\d.E+]+)').Groups[2].Value } }
    $i = & $rd "$dir\flat_in.dxf"; $m = & $rd "$dir\flat_mm.dxf"
    Assert ($i.ins -eq 1) "INSUNITS of the inch DXF: $($i.ins)"; Near ($i.maxx * 2) 21.931 2e-3 'inch DXF width'; Near ($i.maxy * 2) 27.931 2e-3 'inch DXF length'
    Assert ($m.ins -eq 4) "INSUNITS of the mm DXF: $($m.ins)"; Near ($m.maxx * 2) (21.931 * 25.4) 0.05 'mm DXF width'; Near ($m.maxy * 2) (27.931 * 25.4) 0.05 'mm DXF length'
}

Test 'P08 dimension_profile=false: the flange still builds, its profile is left undefined (no dimensions) - the default defines it' {
    $m = Build-Cabinet -NoProfileDims; Assert ($m.verified -eq $true) 'the flange must build without profile dimensions'; Assert ($null -eq $m.profile_drawn.defined) 'no profile definition expected'
    $s = Snap; Near $s.blankL 27.931 5e-4 'the same blank either way'
}

Test 'P09 idempotency and state_version: a replayed equations set is DUPLICATE; a list ignores a stale version, a set refuses it' {
    Fresh-Part; $op = [guid]::NewGuid().ToString('N')
    $send = { param($json) (Invoke-RestMethod -Method Post -ContentType 'application/json' -Body $json "$script:ToolBase/api/tool/execute" -TimeoutSec 120) }
    $body = { param($tool, $params, $version, $opid) @{ operationId = $opid; tool = $tool; stateVersion = $version; params = $params } | ConvertTo-Json -Depth 8 -Compress }
    $a = & $send (& $body 'equations' @{ action = 'set'; variables = @{ Probe = '1in' } } (StateVer) $op); Assert ($a.status -eq 'COMPLETED') "first: $($a.status) $($a.error.message)"
    $b = & $send (& $body 'equations' @{ action = 'set'; variables = @{ Probe = '1in' } } (StateVer) $op); Assert ($b.status -eq 'DUPLICATE') "replay gave $($b.status)"
    $c = & $send (& $body 'equations' @{ action = 'list' } 999999 ([guid]::NewGuid().ToString('N'))); Assert ($c.status -eq 'COMPLETED') "list with a stale version: $($c.status)"
    $d = & $send (& $body 'equations' @{ action = 'set'; variables = @{ Probe2 = '1in' } } 999999 ([guid]::NewGuid().ToString('N'))); Assert ($d.status -eq 'FAILED' -and $d.error.code -eq 'INVALID_STATE_VERSION') "set with a stale version: $($d.status)"
}

Test 'P10 THE LID AS A SECOND BODY: beside the cabinet (geometry all around) a centre rectangle is exactly the size asked, a second base flange makes a second sheet-metal body, the cabinet is unchanged' {
    $null = Build-Cabinet; $before = Snap
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = 0.2 }) 'a plane above the cabinet' | Out-Null
    $s = Rg (Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Plane1'; width = 0.30; height = 0.45 }) 'lid rectangle')
    $z = SketchSize $s.sketch; Near $z[0] 0.30 1e-6 'lid width read back from the sketch (the cabinet around it must not pull the corners)'; Near $z[1] 0.45 1e-6 'lid height read back from the sketch'
    Assert ($s.verified -eq $true -and $s.constrained -eq 'fully_defined') "result: $($s | ConvertTo-Json -Compress)"
    Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.001524; bend_radius = 0.004572; sketch_name = $s.sketch }) 'second base flange' | Out-Null
    $b = (Invoke-Tool 'analyze_model' @{ analysis_type = 'bodies' }).cadState.features[0] | ConvertFrom-Json; Assert ($b.body_count -eq 2) "bodies: $($b.body_count)"
    $p = Params; $items = @($p.cut_list); Assert ($items.Count -eq 2) "cut-list items: $($items.Count)"
    $a = $items[0].properties; Near ([double]$a.'Bounding Box Length') $before.blankL 5e-4 'the cabinet blank length is unchanged'; Near ([double]$a.'Bounding Box Width') $before.blankW 5e-4 'the cabinet blank width is unchanged'; Near ([double]$a.'Cutting Length-Outer') $before.cut 5e-4 'the cabinet cutting length is unchanged'; Assert ([int]$a.Bends -eq 12) 'the cabinet keeps its 12 bends'
    $l = $items[1].properties; $dims = @([double]$l.'Bounding Box Length', [double]$l.'Bounding Box Width') | Sort-Object -Descending; Near $dims[0] (0.45 / 0.0254) 5e-4 'lid blank long side (in)'; Near $dims[1] (0.30 / 0.0254) 5e-4 'lid blank short side (in)'; Assert ([int]$l.Bends -eq 0) 'a plain plate has no bends'
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
foreach ($d in $script:tmpDirs) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
"" ; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
