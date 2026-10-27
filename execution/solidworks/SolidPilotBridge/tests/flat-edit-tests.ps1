# Regression tests for editing a sheet-metal part in its FLAT state: sheet_metal_fold (Unfold / Fold as the PropertyManager does them) and the multiple-radius fillet (add_edge_feature with `radii`).
# Everything is checked against closed forms: the unfolded blank equals W + 2 (BA + D - 2 r) in both directions, a partial unfold flattens exactly the asked axis, a fold returns the SAME folded size, and the
# volume a multi-radius fillet removes is the sum of (1 - pi/4) r^2 h over the rounded corners.
# Needs SolidWorks running with the bridge (shell >= 0.9) and the tools loaded, NOTHING else open (every document is closed at the end).
# Usage: flat-edit-tests.ps1 [-Force] [-Json out.json] [-Only 'F03']
param([switch]$Force, [string]$Json = '', [string]$Only = '')
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
function ConvertedTray { Tray; Rg (Ok (Invoke-Tool 'sheet_metal_convert' @{ fixed_face = '[0,0,0]'; bend_edges = $TrayEdges; thickness = 0.001524; bend_radius = 0.004572; reverse_thickness = 'on' }) 'convert the tray') }
function Fold($action, $bends = $null, $face = '[0,0,0]') { $p = @{ action = $action; fixed_face = $face }; if ($null -ne $bends) { $p.bends = $bends }; Invoke-Tool 'sheet_metal_fold' $p }
function Box100x60x20 { Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.1; height = 0.06 }) 'box rectangle' | Out-Null; Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.02; feature_type = 'boss' }) 'box extrude' | Out-Null }
$VerticalEdges = '[[0.05,0.03,0.01],[-0.05,0.03,0.01],[-0.05,-0.03,0.01],[0.05,-0.03,0.01]]'      # the four vertical edges of the box, by midpoint

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== Unfold / Fold and the multiple-radius fillet  ($script:ToolBase)"

Test 'F00 unfold then fold a converted tray: the blank equals the CLOSED FORM, the fold returns the SAME folded size, the features are counted' {
    $c = ConvertedTray; Assert ($c.verified -eq $true) "convert: $($c.problems -join ' | ')"
    $x0 = Extent 0; $y0 = Extent 1; $z0 = Extent 2; $sv = StateVer
    $u = Rg (Ok (Fold 'unfold') 'unfold')
    Assert ($u.verified -eq $true -and $u.flat -eq $true -and $u.bends_selected -eq 4 -and $u.bends_in_feature -eq 4 -and $u.bends_in_part -eq 4 -and $u.feature -like 'Unfold*') "unfold: $($u | ConvertTo-Json -Compress -Depth 4)"
    Assert ((StateVer) -eq $sv + 1) 'state_version must rise by one'; Near $u.fixed_face_deviation 0 1e-9 'the feature keeps the fixed face asked for'
    $cf = TrayBlank 100 60 30 1.524 4.572 0.5 $true
    Near (Mm $u.size_after[0]) $cf[0] 2e-3 'flat X = W + 2 (BA + D - 2 r)'; Near (Mm $u.size_after[1]) $cf[1] 2e-3 'flat Y'; Near (Mm $u.size_after[2]) 1.524 1e-6 'flat Z = the sheet thickness'
    $f = Rg (Ok (Fold 'fold') 'fold'); Assert ($f.verified -eq $true -and $f.feature -like 'Fold*' -and $f.bends_in_feature -eq 4) "fold: $($f | ConvertTo-Json -Compress -Depth 4)"
    $x1 = Extent 0; $y1 = Extent 1; $z1 = Extent 2
    Near $x1[0] $x0[0] 1e-9 'x min back'; Near $x1[1] $x0[1] 1e-9 'x max back'; Near $y1[0] $y0[0] 1e-9 'y min back'; Near $y1[1] $y0[1] 1e-9 'y max back'; Near $z1[0] $z0[0] 1e-9 'z min back'; Near $z1[1] $z0[1] 1e-9 'z max back'
    $names = FeatureNames; Assert ((CountOfType $names 'UnFold') -eq 1 -and (CountOfType $names 'Fold') -eq 1) "one Unfold and one Fold expected: $($names -join ' | ')"
    $iu = IndexOfType $names 'UnFold'; $if = IndexOfType $names 'Fold'; $ip = IndexOfType $names 'FlatPattern'
    Assert ($iu -ge 0 -and $if -gt $iu -and ($ip -lt 0 -or $ip -gt $if)) "order Unfold < Fold < Flat-Pattern expected: $($names -join ' | ')"
}

Test 'F01 a PARTIAL unfold flattens exactly the asked axis: two opposite bends -> X is the flat X, Y and Z stay folded; the rest then flattens the part completely' {
    $c = ConvertedTray; Assert ($c.verified -eq $true) 'convert'
    $u = Rg (Ok (Fold 'unfold' '["SharpBend1","SharpBend3"]') 'partial unfold'); Assert ($u.verified -eq $true -and $u.bends_selected -eq 2 -and $u.bends_in_part -eq 4 -and -not $u.flat) "partial: $($u | ConvertTo-Json -Compress -Depth 4)"
    $cf = TrayBlank 100 60 30 1.524 4.572 0.5 $true; $x = Extent 0; $y = Extent 1; $z = Extent 2
    Near (Mm ($x[1] - $x[0])) $cf[0] 2e-3 'X is the flat blank width now'; Near (Mm ($y[1] - $y[0])) 63.048 1e-3 'Y is still folded'; Near (Mm ($z[1] - $z[0])) 31.524 1e-3 'Z is still folded (the other two walls stand)'
    $r = Rg (Ok (Fold 'unfold' '["SharpBend2","SharpBend4"]') 'unfold the rest'); Assert ($r.verified -eq $true -and $r.bends_selected -eq 2) 'the rest'
    $x = Extent 0; $y = Extent 1; $z = Extent 2
    Near (Mm ($x[1] - $x[0])) $cf[0] 2e-3 'flat X'; Near (Mm ($y[1] - $y[0])) $cf[1] 2e-3 'flat Y'; Near (Mm ($z[1] - $z[0])) 1.524 1e-6 'flat Z'
    $f1 = Rg (Ok (Fold 'fold' '["SharpBend2","SharpBend4"]') 'fold two back'); $f2 = Rg (Ok (Fold 'fold' '["SharpBend1","SharpBend3"]') 'fold the others back')
    $x = Extent 0; $y = Extent 1; $z = Extent 2; Near (Mm ($x[1] - $x[0])) 103.048 1e-3 'folded X'; Near (Mm ($y[1] - $y[0])) 63.048 1e-3 'folded Y'; Near (Mm ($z[1] - $z[0])) 31.524 1e-3 'folded Z'
}

Test 'F02 refusals: nothing happens and nothing is left behind (no sheet metal, no bends, a wrong state, a missing face, an unknown bend, a bad action)' {
    Box100x60x20
    Fail (Fold 'unfold') 'NOT_SHEET_METAL' 'a plain solid' | Out-Null
    Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = 0.1; height = 0.06 }) 'plate' | Out-Null; Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.0015; bend_radius = 0.0015 }) 'base flange' | Out-Null
    Fail (Fold 'unfold') 'NO_BENDS' 'a flat plate has no bends' | Out-Null
    $c = ConvertedTray; $names0 = FeatureNames; $sv = StateVer
    Fail (Fold 'bend') 'INVALID_PARAMETER' 'bad action' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_fold' @{ action = 'unfold' }) 'MISSING_PARAMETER' 'no fixed face' | Out-Null
    Fail (Fold 'unfold' $null '[0.5,0.5,0.5]') 'FACE_NOT_FOUND' 'a point off the body' | Out-Null
    Fail (Fold 'unfold' $null '9999') 'FACE_NOT_FOUND' 'a face index out of range' | Out-Null
    Fail (Fold 'unfold' '["SharpBend9"]') 'BEND_NOT_FOUND' 'an unknown bend' | Out-Null
    Fail (Fold 'fold') 'FOLD_FAILED' 'folding a part that is already folded' | Out-Null
    Assert ((StateVer) -eq $sv) 'state_version must not change on a refusal'; Assert (((FeatureNames) -join '|') -eq ($names0 -join '|')) 'no feature may have been added by a refusal'
    Ok (Fold 'unfold') 'unfold' | Out-Null
    Fail (Fold 'unfold') 'FOLD_FAILED' 'unfolding a part that is already flat' | Out-Null
    $fi = Rg (Ok (Fold 'fold' $null '0') 'fold with the fixed face given as an index'); Assert ($fi.verified -eq $true) "an index is a valid fixed_face: $($fi | ConvertTo-Json -Compress -Depth 3)"
}

Test 'F03 multiple-radius fillet on a box: ONE feature, four edges, four radii (2 / 3 / 4 / 5 mm) read back edge by edge, volume = closed form' {
    Box100x60x20; $v0 = Volume; Near ($v0 * 1e9) 120000 1e-3 'box volume 100 x 60 x 20 mm'
    $r = Rg (Ok (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = $VerticalEdges; radii = '[0.002,0.003,0.004,0.005]' }) 'multi-radius fillet')
    Assert ($r.verified -eq $true -and $r.multi_radius -eq $true -and $r.edges -eq 4 -and $r.edges_in_feature -eq 4 -and $r.feature -like 'Fillet*') "fillet: $($r | ConvertTo-Json -Compress -Depth 4)"
    $got = @($r.radii_read_back | ForEach-Object { [math]::Round([double]$_.radius * 1000, 3) } | Sort-Object); Assert ($got.Count -eq 4 -and [math]::Abs($got[0] - 2) + [math]::Abs($got[1] - 3) + [math]::Abs($got[2] - 4) + [math]::Abs($got[3] - 5) -lt 1e-9) "radii read back: $($got -join ',')"
    $want = @(@(0.05, 0.03, 2), @(-0.05, 0.03, 3), @(-0.05, -0.03, 4), @(0.05, -0.03, 5))        # the radius each corner must carry (the request's order = the order of the points)
    foreach ($w in $want) { $hit = @($r.radii_read_back | Where-Object { [math]::Abs($_.mid[0] - $w[0]) -lt 1e-6 -and [math]::Abs($_.mid[1] - $w[1]) -lt 1e-6 }); Assert ($hit.Count -eq 1 -and [math]::Abs($hit[0].radius * 1000 - $w[2]) -lt 1e-6) "the corner at ($($w[0]), $($w[1])) must carry $($w[2]) mm: $($hit | ConvertTo-Json -Compress)" }
    $removed = (2, 3, 4, 5 | ForEach-Object { (1 - [math]::PI / 4) * $_ * $_ * 20 } | Measure-Object -Sum).Sum        # mm3: (1 - pi/4) r^2 h per rounded corner
    Near (((Volume)) * 1e9) (120000 - $removed) 0.05 'volume after = box - sum of (1 - pi/4) r^2 h'
    $names = FeatureNames; Assert ((CountOfType $names 'Fillet') -eq 1) "ONE fillet feature expected: $($names -join ' | ')"
}

Test 'F04 fillet refusals and the single-radius path: wrong counts, corners, duplicates, too-big radii are named; add_edge_feature with one radius still works' {
    Box100x60x20; $sv = StateVer; $n0 = (FeatureNames).Count
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = $VerticalEdges; radii = '[0.002,0.003]' }) 'INVALID_PARAMETER' 'two radii for four edges' | Out-Null
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = '[[0.05,0.03,0.0]]'; radii = '[0.002]' }) 'EDGE_AMBIGUOUS' 'a point on a corner lies on several edges' | Out-Null
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = '[[0.01,0.01,0.01]]'; radii = '[0.002]' }) 'EDGE_NOT_FOUND' 'a point on no edge' | Out-Null
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = '[[0.05,0.03,0.01],[0.05,0.03,0.012]]'; radii = '[0.002,0.003]' }) 'INVALID_PARAMETER' 'the same edge twice' | Out-Null
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = '[[0.05,0.03,0.01]]'; radii = '[-0.002]' }) 'INVALID_PARAMETER' 'a negative radius' | Out-Null
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; edge_indices = '[999]'; radii = '[0.002]' }) 'EDGE_NOT_FOUND' 'an index out of range' | Out-Null
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = $VerticalEdges; radii = '[0.002,0.002,0.002,0.2]' }) 'INVALID_PARAMETER' 'a 200 mm radius' | Out-Null
    Assert ((StateVer) -eq $sv) 'state_version must not change on a refusal'; Assert ((FeatureNames).Count -eq $n0) 'a refusal must not add a feature'
    Fail (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = '[[0,0.03,0.02],[0,-0.03,0.02],[0.05,0,0.02],[-0.05,0,0.02]]'; radii = '[0.05,0.05,0.05,0.05]' }) 'FILLET_FAILED' '50 mm radii round the top of a 20 mm high box: SolidWorks refuses' | Out-Null
    Assert ((FeatureNames).Count -eq $n0) 'a failed fillet leaves no feature behind'
    Box100x60x20; $s = Ok (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.004; ex = 0.05; ey = 0.03; ez = 0.01 }) 'the classic single-radius fillet'
    Near ((Volume) * 1e9) (120000 - (1 - [math]::PI / 4) * 16 * 20) 0.05 'one 4 mm corner removes (1 - pi/4) r^2 h'
}

Test 'F05 THE REAL USE: unfold a tray, round the eight flap-end corners of the flat blank with ONE multi-radius fillet, fold back - volume in closed form, folded size unchanged, order Unfold < Fillet < Fold' {
    $c = ConvertedTray; Assert ($c.verified -eq $true) 'convert'; $x0 = Extent 0; $y0 = Extent 1; $z0 = Extent 2
    Ok (Fold 'unfold') 'unfold' | Out-Null; $v0 = Volume
    $te = @(ThicknessEdges 0.001524 | Sort-Object { -[math]::Sqrt($_.mid[0] * $_.mid[0] + $_.mid[1] * $_.mid[1]) })
    $far = @($te | Select-Object -First 8); Assert ($far.Count -eq 8) "outline corners: $($te.Count) thickness edges"
    $minFar = ($far | ForEach-Object { [math]::Sqrt($_.mid[0] * $_.mid[0] + $_.mid[1] * $_.mid[1]) } | Measure-Object -Minimum).Minimum; Assert ($minFar -gt 0.07) "the eight farthest corners are the flap ends (|r| > 70 mm), the next one is at $([math]::Round($minFar * 1000, 1)) mm"
    $radii = @(2, 3, 4, 5, 2, 3, 4, 5) | ForEach-Object { $_ / 1000.0 }; $pts = '[' + (($far | ForEach-Object { '[' + ((Num $_.mid[0]), (Num $_.mid[1]), (Num $_.mid[2]) -join ',') + ']' }) -join ',') + ']'
    $r = Rg (Ok (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.002; points = $pts; radii = ('[' + (($radii | ForEach-Object { Num $_ }) -join ',') + ']') }) 'fillet the flap ends')
    Assert ($r.verified -eq $true -and $r.multi_radius -eq $true -and $r.edges_in_feature -eq 8) "fillet: $($r | ConvertTo-Json -Compress -Depth 4)"
    $removed = ($radii | ForEach-Object { (1 - [math]::PI / 4) * ($_ * 1000) * ($_ * 1000) * 1.524 } | Measure-Object -Sum).Sum
    Near (((Volume) - $v0) * 1e9) (-$removed) 0.01 'eight convex corners lose (1 - pi/4) r^2 t each'
    $f = Rg (Ok (Fold 'fold') 'fold back'); Assert ($f.verified -eq $true) "fold: $($f | ConvertTo-Json -Compress -Depth 4)"
    $x1 = Extent 0; $y1 = Extent 1; $z1 = Extent 2; Near $x1[1] $x0[1] 1e-9 'folded x max'; Near $y1[1] $y0[1] 1e-9 'folded y max'; Near $z1[1] $z0[1] 1e-9 'folded z max'
    $names = FeatureNames; $iu = IndexOfType $names 'UnFold'; $ii = IndexOfType $names 'Fillet'; $if = IndexOfType $names 'Fold'; $ip = IndexOfType $names 'FlatPattern'
    Assert ($iu -ge 0 -and $ii -gt $iu -and $if -gt $ii -and ($ip -lt 0 -or $ip -gt $if)) "order Unfold < Fillet < Fold < Flat-Pattern expected: $($names -join ' | ')"
}

function RibbonToy {     # a 100 x 60 x 20 mm box (z 0..20) and an exited OPEN sketch on the Front Plane: the line x + y = 70 mm cuts the corner (40,30)-(50,20)-(50,30) off. Returns the sketch name.
    Box100x60x20
    $s = Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane' }) 'ribbon sketch'; $nm = (($s | ConvertTo-Json -Depth 6 -Compress) -replace '.*?"(Sketch\d+)".*', '$1')
    Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = 0.035; y1 = 0.035; x2 = 0.055; y2 = 0.015 }) 'ribbon line' | Out-Null
    $nm
}

Test 'F06 surface_extrude + surface_cut on a box: the ribbon is 20 x 20 x 30 mm, the right flip removes EXACTLY the corner (1000 mm3), a wrong flip and a miss are refused and taken out again' {
    $sk = RibbonToy; Near ((Volume) * 1e9) 120000 1e-3 'box'
    $e = Rg (Ok (Invoke-Tool 'surface_extrude' @{ sketch = $sk; depth = 0.03 }) 'surface extrude')
    Assert ($e.verified -eq $true -and $e.feature -like 'Surface-Extrude*' -and $e.surface_bodies -eq 1) "extrude: $($e | ConvertTo-Json -Compress -Depth 4)"
    Near (Mm $e.surface_size[0]) 20 1e-3 'ribbon x'; Near (Mm $e.surface_size[1]) 20 1e-3 'ribbon y'; Near (Mm $e.surface_size[2]) 30 1e-3 'ribbon z = the depth'
    Fail (Invoke-Tool 'surface_cut' @{ surface = $e.feature; flip = $true; max_removed_volume = 0.000005 }) 'CUT_REMOVED_TOO_MUCH' 'the wrong side is the large one' | Out-Null
    Near ((Volume) * 1e9) 120000 1e-3 'the refused cut leaves the volume alone'; Assert ((CountOfType (FeatureNames) 'SurfCut') -eq 0) 'the refused cut is taken out again'
    $c = Rg (Ok (Invoke-Tool 'surface_cut' @{ surface = $e.feature; flip = $false; max_removed_volume = 0.000005 }) 'surface cut')
    Assert ($c.verified -eq $true -and $c.feature -like 'SurfaceCut*') "cut: $($c | ConvertTo-Json -Compress -Depth 4)"
    Near ($c.removed_volume * 1e9) 1000 1e-3 'the corner: 1/2 x 10 x 10 x 20'; Near ((Volume) * 1e9) 119000 1e-3 'volume after'
    $away = Rg (Ok (Invoke-Tool 'surface_extrude' @{ sketch = $sk; depth = 0.03; reverse = $true }) 'a ribbon on the other side of the sketch plane')
    Fail (Invoke-Tool 'surface_cut' @{ surface = $away.feature }) 'NOTHING_CUT' 'a ribbon that misses the box' | Out-Null
    Fail (Invoke-Tool 'surface_extrude' @{ sketch = 'Sketch99'; depth = 0.01 }) 'SKETCH_NOT_FOUND' 'unknown sketch' | Out-Null
    Fail (Invoke-Tool 'surface_extrude' @{ sketch = $sk; depth = 0 }) 'INVALID_PARAMETER' 'depth 0' | Out-Null
    Fail (Invoke-Tool 'surface_cut' @{ surface = 'Nothing1' }) 'FEATURE_NOT_FOUND' 'unknown feature' | Out-Null
    Fail (Invoke-Tool 'surface_cut' @{}) 'MISSING_PARAMETER' 'no surface' | Out-Null
    Near ((Volume) * 1e9) 119000 1e-3 'the refusals changed nothing'
}

Test 'F07 a surface cut can be MIRRORED (create_pattern mirror about the Right Plane): the opposite corner goes too, 2000 mm3 in all' {
    $sk = RibbonToy; $e = Rg (Ok (Invoke-Tool 'surface_extrude' @{ sketch = $sk; depth = 0.03 }) 'surface extrude')
    $c = Rg (Ok (Invoke-Tool 'surface_cut' @{ surface = $e.feature; max_removed_volume = 0.000005 }) 'surface cut'); Near ((Volume) * 1e9) 119000 1e-3 'one corner gone'
    $m = Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'mirror'; features_json = "[`"$($c.feature)`"]"; plane = 'Right Plane' }) 'mirror the cut'
    Near ((Volume) * 1e9) 118000 1e-3 'two corners gone: 120000 - 2 x 1000'
    Assert ((CountOfType (FeatureNames) 'MirrorPattern') -eq 1) "one mirror feature: $((FeatureNames) -join ' | ')"
}

Test 'F08 trim a flap corner of an UNFOLDED tray with a surface cut, then fold: the cut removes the triangle x t (closed form), the fold still works and the volume is the same folded (K = 0.5)' {
    $c = ConvertedTray; Assert ($c.verified -eq $true) 'convert'; $vFolded = Volume
    Ok (Fold 'unfold') 'unfold' | Out-Null; $vFlat = Volume; Near ($vFlat * 1e9) ($vFolded * 1e9) 1e-3 'K = 0.5: the flat and the folded sheet have the same volume'
    $te = @(ThicknessEdges 0.001524 | Where-Object { $_.mid[0] -gt 0 -and $_.mid[1] -gt 0 } | Sort-Object { -($_.mid[0] + $_.mid[1]) }); $cx = $te[0].mid[0] * 1000; $cy = $te[0].mid[1] * 1000      # the flap-end corner farthest out in +x +y (mm)
    $x1 = ($cx - 8) / 1000; $y1 = ($cy + 3) / 1000; $x2 = ($cx + 3) / 1000; $y2 = ($cy - 8) / 1000          # the line x + y = cx + cy - 5, 3 mm beyond the outline at both ends: cuts a triangle with 5 mm legs off the corner
    $sk = Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane' }) 'trim sketch'; $trimSketch = (($sk | ConvertTo-Json -Depth 6 -Compress) -replace '.*?"(Sketch\d+)".*', '$1')
    Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = $x1; y1 = $y1; x2 = $x2; y2 = $y2 }) 'trim line' | Out-Null
    $e = Rg (Ok (Invoke-Tool 'surface_extrude' @{ sketch = $trimSketch; depth = 0.005; reverse = $true }) 'ribbon down through the plate'); Assert ($e.verified -eq $true) "ribbon: $($e | ConvertTo-Json -Compress -Depth 4)"
    $cut = Invoke-Tool 'surface_cut' @{ surface = $e.feature; flip = $false; max_removed_volume = 1e-7 }
    if ($cut.status -ne 'COMPLETED' -and $cut.error.code -eq 'CUT_REMOVED_TOO_MUCH') { $cut = Invoke-Tool 'surface_cut' @{ surface = $e.feature; flip = $true; max_removed_volume = 1e-7 } }
    $g = Rg (Ok $cut 'surface cut with the right flip'); Near ($g.removed_volume * 1e9) (12.5 * 1.524) 0.01 'the corner triangle: 1/2 x 5 x 5 x t'
    Near ((Volume) * 1e9) (($vFlat * 1e9) - 12.5 * 1.524) 0.01 'volume after the cut'
    $f = Rg (Ok (Fold 'fold') 'fold back'); Assert ($f.verified -eq $true) "fold after the cut: $($f | ConvertTo-Json -Compress -Depth 4)"
    Near ((Volume) * 1e9) (($vFolded * 1e9) - 12.5 * 1.524) 0.02 'folded volume = original - the trimmed triangle'
    $iu = IndexOfType (FeatureNames) 'UnFold'; $ic = IndexOfType (FeatureNames) 'SurfCut'; $if = IndexOfType (FeatureNames) 'Fold'; Assert ($iu -ge 0 -and $ic -gt $iu -and $if -gt $ic) "order Unfold < SurfaceCut < Fold: $((FeatureNames) -join ' | ')"
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
