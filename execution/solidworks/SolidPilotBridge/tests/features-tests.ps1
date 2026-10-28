# Regression tests for the sheet-metal FEATURES that have no PropertyManager-free API call of their own and are built through CreateDefinition / the typed API: the GUSSET (sheet_metal_gusset) and the other
# features of this suite as they are added. What they prove: the feature is built, read back from SolidWorks and checked (type, thickness, offset, dimensions), the gusset stands where the arithmetic says
# (the START vertex of the bend plus the offset), its size follows the dimensions (the rib's inner faces are indent_width apart, a flat gusset adds ~ 1/2 depth x width x thickness of material), and every request
# that cannot work is refused with the reason and builds nothing.
# Needs SolidWorks running with the bridge (shell >= 0.9), the in-process tools loaded and NOTHING else open. Every test builds its own part (the plate with ONE 25 mm flange of probes\sm_fixture.ps1).
# Usage: features-tests.ps1 [-Force] [-Json out.json] [-Only 'G01']
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
    "{0}  {1,-110} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.ElapsedMilliseconds, $why
    [void]$script:results.Add([pscustomobject]@{ name = $Name; pass = $ok; ms = $sw.ElapsedMilliseconds; why = $why })
}
$fixture = Join-Path $PSScriptRoot '..\probes\sm_fixture.ps1'
# the plate (100 x 60 x 1.5 mm, z -1.5 .. 0) with ONE 25 mm flange on its +y edge (a vertical wall y 28.5 .. 30, z 0 .. 25); the bend runs along x from the vertex (-50, 27, 0) to (50, 27, 0)
# Once (2026-10-27, the first test after a restart of SolidWorks) the fixture came back with NO document open and the same fixture worked three times in a row afterwards; it is built again once, and the note says so.
function Fixture([int]$Flanges) {
    foreach ($try in 1..2) {
        Fresh-Part; & $fixture -Flanges $Flanges | Out-Null
        if ((Invoke-Bridge 'activeDoc' 5000) -ne 'none') { return }
        "   (the fixture left no document open - building it again, try $($try + 1) of 2)"
    }
    Assert $false 'the fixture part is open'
}
function Bent-Part { Fixture 1 }
$GFaces = '[[0,0,0],[0,0.0285,0.012]]'      # the plate's top face (z = 0) and the flange's inner face (y = 28.5)
function Gusset([hashtable]$more) { Invoke-Tool 'sheet_metal_gusset' (Merge @{ points = $GFaces } $more) }
# the planar faces whose normal runs along x, away from the part's end faces (x = +-50): the gusset's side faces -> their x positions (mm), sorted
function GussetXs { @((Get-Faces).faces | Where-Object { $_.planar -and [math]::Abs([math]::Abs($_.normal[0]) - 1) -lt 1e-6 -and [math]::Abs($_.point[0]) -lt 0.0499 } | ForEach-Object { [math]::Round($_.point[0] * 1000, 3) } | Sort-Object -Unique) }

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== Sheet-metal features  ($script:ToolBase)"

Test 'G00 FLAT GUSSET: built, read back, 8 faces added, ~1/2 depth x width x thickness of material (4 x 10 x 1.5 -> 29.9 mm3), centred at the start vertex of the bend + the offset' {
    Bent-Part; $f0 = (Get-Faces).face_count
    $r = Rg (Ok (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.01; offset = 0.02 }) 'flat gusset'); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    Assert ($r.feature -like 'Sheet Metal Gusset*') "feature $($r.feature)"
    Near $r.reference_point[0] -0.05 1e-9 'the reference point is the START vertex of the bend: x'; Near $r.reference_point[1] 0.027 1e-9 'y'; Near $r.reference_point[2] 0 1e-9 'z'
    Near $r.gusset_centre[0] -0.03 1e-9 'the gusset stands at the start vertex + offset 20 mm: x = -30'; Near $r.gusset_centre[1] 0.027 1e-9 'y'
    $dv = $r.volume_added * 1e9; Assert ($dv -gt 29.4 -and $dv -lt 30.0) "volume added $dv mm3 (1/2 x 4 x 10 x 1.5 = 30)"
    Assert (((Get-Faces).face_count - $f0) -eq 8) "faces added: $(((Get-Faces).face_count) - $f0)"
    $xs = GussetXs; Assert ($xs -contains -35 -and $xs -contains -25) "the inner side faces are indent_width = 10 mm apart around x = -30: $($xs -join ', ')"
    Assert ($xs -contains -33.5 -and $xs -contains -26.5) "the outer side faces (the 5 degree default draft of the rib): $($xs -join ', ')"
}

Test 'G01 ROUNDED GUSSET (the pressed rib): built and read back; 6 faces added; the material barely changes (it is formed, not added)' {
    Bent-Part; $f0 = (Get-Faces).face_count
    $r = Rg (Ok (Gusset @{ indent_depth = 0.004; indent_width = 0.01; offset = 0.03 }) 'rounded gusset'); Assert ($r.verified -eq $true -and $r.gusset_type -eq 'rounded') "problems: $($r.problems -join ' | ')"
    Assert (((Get-Faces).face_count - $f0) -eq 6) "faces added: $(((Get-Faces).face_count) - $f0)"
    Assert ([math]::Abs($r.volume_added * 1e9) -lt 5) "a rib is formed out of the sheet: volume added $($r.volume_added * 1e9) mm3"
    Near $r.gusset_centre[0] -0.02 1e-9 'centred at -50 + 30 = -20 mm'
}

Test 'G02 the size follows the dimensions: a deeper / wider flat gusset (6 x 12) is bigger (volume, side faces 12 mm apart) than the 4 x 10 one; two gussets side by side stand at their own offsets' {
    Bent-Part
    $a = Rg (Ok (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.01; position = 0.02 }) 'gusset 4 x 10')
    $b = Rg (Ok (Gusset @{ gusset_type = 'flat'; indent_depth = 0.006; indent_width = 0.012; position = 0.07 }) 'gusset 6 x 12')
    Assert ($b.volume_added -gt 1.5 * $a.volume_added) "6 x 12 adds $($b.volume_added * 1e9) mm3, 4 x 10 adds $($a.volume_added * 1e9) mm3"
    Near $b.gusset_centre[0] 0.02 1e-9 'the second gusset: -50 + 70 = +20 mm'
    $xs = GussetXs; Assert ($xs -contains 14 -and $xs -contains 26) "the 12 mm wide gusset around x = +20: $($xs -join ', ')"; Assert ($xs -contains -35 -and $xs -contains -25) "the first one is untouched: $($xs -join ', ')"
}

Test 'G03 the profile scheme: length 10 + height 6 (a bigger triangle than the 4 x 10 indent: 50.7 mm3), and the height replaced by an angle; draft and fillets are read back' {
    Bent-Part
    $r = Rg (Ok (Gusset @{ gusset_type = 'flat'; dimension_scheme = 'profile'; profile_length = 0.01; profile_height = 0.006; indent_width = 0.01; offset = 0.02 }) 'profile 10 x 6'); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    $dv = $r.volume_added * 1e9; Assert ($dv -gt 45 -and $dv -lt 56) "volume added $dv mm3"
    Bent-Part
    $r2 = Rg (Ok (Gusset @{ gusset_type = 'flat'; dimension_scheme = 'profile'; profile_length = 0.01; profile_angle = 30; indent_width = 0.01; offset = 0.02 }) 'profile 10 + 30 deg'); Assert ($r2.verified -eq $true) "problems: $($r2.problems -join ' | ')"
    Assert ($r2.volume_added -gt 2 * $r.volume_added) "the 30 degree profile is much longer: $($r2.volume_added * 1e9) mm3"
    Bent-Part
    $r3 = Rg (Ok (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.01; offset = 0.02; draft = $true; draft_angle = 10; fillet_edges = $true; fillet_inner_corners = $true; fillet_outer_corners = $true }) 'draft + fillets'); Assert ($r3.verified -eq $true) "problems: $($r3.problems -join ' | ')"
    Assert (((Get-Faces).face_count) -gt 40) "the fillets add faces: $((Get-Faces).face_count)"
}

Test 'G04 refusals before anything is built: one face, no offset, no dimensions, a bad type, an offset beyond the bend, two faces that do not meet at a bend - the part is untouched' {
    Bent-Part; $v0 = StateVer; $n0 = @(FeatureNames).Count
    Fail (Invoke-Tool 'sheet_metal_gusset' @{ points = '[[0,0,0]]'; indent_depth = 0.004; indent_width = 0.01; offset = 0.02 }) 'MISSING_PARAMETER' 'one face' | Out-Null
    Fail (Gusset @{ indent_depth = 0.004; indent_width = 0.01 }) 'MISSING_PARAMETER' 'no offset' | Out-Null
    Fail (Gusset @{ offset = 0.02 }) 'MISSING_PARAMETER' 'no depth / width' | Out-Null
    Fail (Gusset @{ offset = 0.02; indent_depth = 0.004 }) 'MISSING_PARAMETER' 'no width' | Out-Null
    Fail (Gusset @{ offset = 0.02; indent_depth = 0.004; indent_width = 0.01; gusset_type = 'triangle' }) 'INVALID_PARAMETER' 'a gusset type that does not exist' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_gusset' @{ points = '[[0,0,0],[0,0,-0.0015]]'; indent_depth = 0.004; indent_width = 0.01; offset = 0.02 }) 'GUSSET_FAILED' 'the sheet top and bottom face: no bend between them' | Out-Null
    Fail (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.01; offset = 0.2 }) 'GUSSET_FAILED' 'an offset beyond the 100 mm bend' | Out-Null
    Assert ((StateVer) -eq $v0) 'a refused gusset leaves the state version alone'; Assert (@(FeatureNames).Count -eq $n0) "no feature was left behind: $n0 -> $(@(FeatureNames).Count)"
}

Test 'G05 POSITION is where the gusset stands, whatever SolidWorks takes as its reference: three gussets at 20 / 50 / 80 mm from the bend''s start stand at x = -30 / 0 / +30 (the bend edge is split after the first, so the offset form moved with the reference vertex), a gusset given the two faces in the OTHER order still stands where asked, and what cannot be placed is refused with the part untouched' {
    Bent-Part
    foreach ($pos in 0.02, 0.05, 0.08) {
        $r = Rg (Ok (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.006; position = $pos }) "gusset at $pos"); Assert ($r.verified -eq $true -and -not $r.problems) "position $pos : $($r.problems -join ' | ') | attempts $($r.attempts | ConvertTo-Json -Compress -Depth 4)"
        Near $r.position $pos 1e-6 "the centre stands $pos m from the bend's start"; Near $r.gusset_centre[0] (-0.05 + $pos) 1e-6 'x of the centre'
    }
    $xs = GussetXs; foreach ($want in -33, -27, -3, 3, 27, 33) { Assert ($xs -contains $want) "the inner side faces of the three 6 mm gussets: $($xs -join ', ')" }
    $swapped = Rg (Ok (Invoke-Tool 'sheet_metal_gusset' @{ points = '[[0,0.0285,0.012],[0,0,0]]'; gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.006; position = 0.035 }) 'the faces in the other order'); Assert ($swapped.verified -eq $true) "swapped: $($swapped.problems -join ' | ')"
    Near $swapped.gusset_centre[0] -0.015 1e-6 'still at 35 mm from the start (x = -15), the faces in the other order'
    $v0 = StateVer; $n0 = @(FeatureNames).Count
    Fail (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.006; position = 0.01; offset = 0.01 }) 'INVALID_PARAMETER' 'position and offset together' | Out-Null
    $e = Fail (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.006; position = 0.2 }) 'GUSSET_FAILED' 'a position beyond the end of the 100 mm bend'; Assert ($e.error.message -like '*off the end of the bend*') $e.error.message
    $e = Fail (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.006; position = 0.001 }) 'GUSSET_FAILED' 'a position before the start of the bend'; Assert ($e.error.message -like '*off the start of the bend*') $e.error.message
    Fail (Gusset @{ gusset_type = 'flat'; indent_depth = 0.004; indent_width = 0.006; position = 0.0505 }) 'GUSSET_FAILED' 'a gusset on top of the one at 50 mm' | Out-Null
    Assert (@(FeatureNames).Count -eq $n0) "no feature was left behind by the refusals (probes are taken out again): $n0 -> $(@(FeatureNames).Count)"; Assert ((StateVer) -eq $v0) 'refusals leave the state version alone'
}


# ---- JOG (sheet_metal_jog): two sketched bends on the plain plate (100 x 60 x 1.5 mm, z -1.5 .. 0, r 1.5, K 0.5) ---------------------------------------------------------------------------------------------
$JT = 0.0015; $JR = 0.0015; $JK = 0.5
function Plate-Part { Fixture 0 }
function JogArgs([double]$h, [double]$ang = 90, [string]$dir = 'up') { @{ line = '[[0,-0.03,0],[0,0.03,0]]'; fixed_point = '[-0.03,0,0]'; offset = $h; angle = $ang; direction = $dir } }
# closed forms (m): the flat distance between the bend lines, the web, the folded length of the 100 mm plate along x
function JogForms([double]$h, [double]$ang) {
    $th = $ang * [math]::PI / 180; $ba = $th * ($JR + $JK * $JT); $web = ($h - (2 * $JR + $JT) * (1 - [math]::Cos($th))) / [math]::Sin($th)
    @{ ba = $ba; web = $web; s = $web + $ba; folded = 0.1 - ($web + 2 * $ba) + (2 * ($JR + $JT / 2)) * [math]::Sin($th) + $web * [math]::Cos($th) }
}

Test 'J00 JOG 10 mm UP at x = 0: the far flat lies 10 mm above the near one (top faces at 0 and 10 mm), the lines are s = h - (2r + t) + BA = 9.034 mm apart, the folded length is 100 - h + 4r + 2t - 2BA = 91.931 mm, the blank is unchanged' {
    Plate-Part; $c = JogForms 0.01 90
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_jog' (JogArgs 0.01)) 'jog up'); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    Near $r.line_distance $c.s 1e-9 'the flat distance between the bend lines'; Near $r.bend_allowance $c.ba 1e-9 'BA'; Near $r.second_line[0][0] $c.s 1e-9 'the second line is drawn s from the first, away from the fixed point'
    $pl = @($r.planes_after | ForEach-Object { [math]::Round($_, 6) }); Assert ($pl -contains 0 -and $pl -contains 0.01 -and $pl -contains 0.0085) "flat planes after: $($pl -join ', ')"
    $x = Extent 0; Near (($x[1] - $x[0])) $c.folded 2e-6 'folded length along x (closed form)'; $z = Extent 2; Near $z[0] -0.0015 1e-9 'z min'; Near $z[1] 0.01 1e-9 'z max = the far flat''s top face'
    Near $r.flat_deviation 0 2e-4 'the flat blank is unchanged'; Assert (@($r.features).Count -eq 2) "two sketched bends: $($r.features -join ', ')"
}

Test 'J01 JOG DOWN: the far flat lies 10 mm BELOW (top faces at 0 and -10 mm); same lines, same folded length' {
    Plate-Part; $c = JogForms 0.01 90
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_jog' (JogArgs 0.01 90 'down')) 'jog down'); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    $pl = @($r.planes_after | ForEach-Object { [math]::Round($_, 6) }); Assert ($pl -contains -0.01 -and $pl -contains 0) "flat planes after: $($pl -join ', ')"
    $x = Extent 0; Near (($x[1] - $x[0])) $c.folded 2e-6 'folded length along x'; $z = Extent 2; Near $z[0] -0.0115 1e-9 'z min = the far flat''s bottom'; Near $z[1] 0 1e-9 'z max'
}

Test 'J02 a 45 degree JOG (two 45 degree bends and an inclined web): 10 mm step, lines s = (h - (2r + t)(1 - cos a)) / sin a + BA apart, folded length from the closed form, blank unchanged' {
    Plate-Part; $c = JogForms 0.01 45
    $r = Rg (Ok (Invoke-Tool 'sheet_metal_jog' (JogArgs 0.01 45)) 'jog 45'); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    Near $r.line_distance $c.s 1e-9 'line distance'; Near $r.web $c.web 1e-9 'web'
    $pl = @($r.planes_after | ForEach-Object { [math]::Round($_, 6) }); Assert ($pl -contains 0.01) "flat planes after: $($pl -join ', ')"
    $x = Extent 0; Near (($x[1] - $x[0])) $c.folded 5e-6 'folded length along x'; Near $r.flat_deviation 0 2e-4 'the flat blank is unchanged'
}

Test 'J03 refusals before anything is built: no line / fixed point / offset, an offset smaller than the two bends need, a bad direction or angle, a zero-length line - the part is untouched' {
    Plate-Part; $v0 = StateVer; $n0 = @(FeatureNames).Count
    Fail (Invoke-Tool 'sheet_metal_jog' @{ fixed_point = '[-0.03,0,0]'; offset = 0.01 }) 'MISSING_PARAMETER' 'no line' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_jog' @{ line = '[[0,-0.03,0],[0,0.03,0]]'; offset = 0.01 }) 'MISSING_PARAMETER' 'no fixed point' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_jog' @{ line = '[[0,-0.03,0],[0,0.03,0]]'; fixed_point = '[-0.03,0,0]' }) 'MISSING_PARAMETER' 'no offset' | Out-Null
    $e = Fail (Invoke-Tool 'sheet_metal_jog' (JogArgs 0.003)) 'JOG_TOO_SMALL' 'an offset of 3 mm needs more than the 4.5 mm of two 90 degree bends'; Assert ($e.error.message -like '*at least 4.5 mm*') $e.error.message
    Fail (Invoke-Tool 'sheet_metal_jog' (JogArgs 0.01 90 'sideways')) 'INVALID_PARAMETER' 'a direction that does not exist' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_jog' (JogArgs 0.01 120)) 'INVALID_PARAMETER' 'an angle over 90 degrees' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_jog' @{ line = '[[0,0,0],[0,0,0]]'; fixed_point = '[-0.03,0,0]'; offset = 0.01 }) 'INVALID_PARAMETER' 'a line without length' | Out-Null
    Assert ((StateVer) -eq $v0) 'a refused jog leaves the state version alone'; Assert (@(FeatureNames).Count -eq $n0) "nothing was left behind: $n0 -> $(@(FeatureNames).Count)"
}

# ---- LOFTED BEND (sheet_metal_lofted_bend): two OPEN profile sketches on parallel planes 30 mm apart (the Front Plane and an offset plane) --------------------------------------------------------------
# kind 'arcs' = one half circle in each sketch (r1 20 mm -> r2 10 mm: a half cone), 'u' = two lines + a half circle (40 x 30 -> 20 x 15 mm), 'circles' = CLOSED profiles (refused). The arc runs from (-r, 0) to (r, 0)
# counter-clockwise, i.e. through the bottom: over the lines the chain would double back, which SolidWorks refuses.
function LoftProfiles([string]$kind = 'arcs', [double]$r1 = 0.02, [double]$r2 = 0.01) {
    Fresh-Part
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = 0.03 }) 'loft plane' | Out-Null
    $names = @()
    foreach ($c in @(@('Front Plane', $r1, 0.03), @('Plane1', $r2, 0.015))) {
        $s = Ok (Invoke-Tool 'create_sketch' @{ plane = $c[0] }) 'loft sketch'; $names += (($s | ConvertTo-Json -Depth 6 -Compress) -replace '.*?"(Sketch\d+)".*', '$1'); $r = $c[1]
        if ($kind -eq 'circles') { Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'circle'; cx = 0.0; cy = 0.0; radius = $r }) 'loft circle' | Out-Null; continue }
        if ($kind -eq 'u') { Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = -$r; y1 = $c[2]; x2 = -$r; y2 = 0.0 }) 'u: left line' | Out-Null }
        Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'arc_center'; cx = 0.0; cy = 0.0; x1 = -$r; y1 = 0.0; x2 = $r; y2 = 0.0; direction = 1 }) 'loft arc' | Out-Null
        if ($kind -eq 'u') { Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = $r; y1 = 0.0; x2 = $r; y2 = $c[2] }) 'u: right line' | Out-Null }
    }
    , $names
}
function Loft($names, [hashtable]$more = @{}) { Invoke-Tool 'sheet_metal_lofted_bend' (Merge @{ sketches = "[`"$($names[0])`",`"$($names[1])`"]"; thickness = 0.0015 } $more) }
function LoftBuild([hashtable]$more = @{}, [string]$kind = 'arcs') { $n = LoftProfiles $kind; $r = Rg (Ok (Loft $n $more) "lofted bend $($more.Keys -join ',')"); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"; $r }

Test 'L00 LOFTED BEND of two half circles (r 20 -> 10 mm, 30 mm apart): built from the sketches alone, sheet thickness 1.5 mm, 4 facets = 3 bends, one body, a flat pattern, the volume of a half cone shell (pi (r1 + r2) slant / 2 x t)' {
    $r = LoftBuild @{ facets = 'number_of_bends'; number_of_bends = 4 }
    Assert ($r.bends -eq 3 -and $r.bodies -eq 1) "bends $($r.bends), bodies $($r.bodies)"; Near $r.sheet_thickness 0.0015 1e-9 'sheet thickness'
    Assert (@($r.new_features | Where-Object { $_ -like 'Lofted Bends*' }).Count -eq 1 -and @($r.new_features | Where-Object { $_ -like 'Sheet-Metal*' }).Count -eq 1) "features: $($r.new_features -join ', ')"
    $slant = [math]::Sqrt(0.03 * 0.03 + 0.01 * 0.01); $want = [math]::PI * (0.02 + 0.01) * $slant / 2 * 0.0015
    Assert ([math]::Abs($r.volume - $want) / $want -lt 0.05) "volume $($r.volume * 1e9) mm3, half cone shell $($want * 1e9) mm3"
    Assert ($r.blank_length -gt 0.05 -and $r.blank_width -gt 0.03 -and $r.cutting_length -gt 0.1) "blank $($r.blank_length) x $($r.blank_width), cutting length $($r.cutting_length)"
    Assert ($r.body_after.size[2] -gt 0.0304 -and $r.body_after.size[2] -lt 0.0306) "the transition is 30 mm high (+ the thickness at the slope): $($r.body_after.size[2])"
}

Test 'L01 the facet argument named is the one used: more bends, a tighter chord tolerance, a shorter segment, a smaller segment angle each give MORE faces; the arguments of the other options are ignored' {
    $f = { param($more) (LoftBuild $more).body_after.faces }
    $nb4 = & $f @{ facets = 'number_of_bends'; number_of_bends = 4 }; $nb8 = & $f @{ facets = 'number_of_bends'; number_of_bends = 8 }; Assert ($nb8 -gt $nb4) "number_of_bends 4 -> $nb4 faces, 8 -> $nb8"
    $c1 = & $f @{ facets = 'chord_tolerance'; chord_tolerance = 0.001 }; $c2 = & $f @{ facets = 'chord_tolerance'; chord_tolerance = 0.0001 }; Assert ($c2 -gt $c1) "chord tolerance 1 mm -> $c1 faces, 0.1 mm -> $c2"
    $c3 = & $f @{ facets = 'chord_tolerance'; chord_tolerance = 0.0001; number_of_bends = 30; segment_length = 0.001; segment_angle = 1 }; Assert ($c3 -eq $c2) "the other facet arguments are ignored under chord_tolerance: $c2 vs $c3 faces"
    $l1 = & $f @{ facets = 'segment_length'; segment_length = 0.02 }; $l2 = & $f @{ facets = 'segment_length'; segment_length = 0.005 }; Assert ($l2 -gt $l1) "segment length 20 mm -> $l1 faces, 5 mm -> $l2"
    $a1 = & $f @{ facets = 'segment_angle'; segment_angle = 28.65 }; $a2 = & $f @{ facets = 'segment_angle'; segment_angle = 5.73 }; Assert ($a2 -gt $a1) "segment angle 28.65 deg -> $a1 faces, 5.73 deg -> $a2"
}

Test 'L02 thickness_side: side1 puts the material OUTSIDE the first profile (the box is wider than the 40 mm half circle), side2 INSIDE (the first profile is the outer skin: 40.00 mm wide)' {
    $o = LoftBuild @{ facets = 'number_of_bends'; number_of_bends = 4; thickness_side = 'side1' }; $i = LoftBuild @{ facets = 'number_of_bends'; number_of_bends = 4; thickness_side = 'side2' }
    Assert ($o.body_after.size[0] -gt 0.0425) "side1 is $($o.body_after.size[0] * 1000) mm wide"; Near $i.body_after.size[0] 0.040 1e-5 'side2: the outer skin is the 40 mm profile'
    Assert ($o.volume -gt $i.volume) "side1 volume $($o.volume * 1e9) mm3 > side2 $($i.volume * 1e9) mm3 (the same thickness on the longer skin)"
}

Test 'L03 formed = true: the smooth surface instead of facets (a half cone: 6 faces, ONE bend), still a sheet-metal part with a flat pattern' {
    $r = LoftBuild @{ formed = $true; thickness_side = 'side1' }
    Assert ($r.body_after.faces -eq 6 -and $r.bends -eq 1) "faces $($r.body_after.faces), bends $($r.bends)"; Assert ($r.blank_length -gt 0.05) "blank $($r.blank_length) x $($r.blank_width)"
}

Test 'L04 a U of two lines and an arc builds; what SolidWorks refuses is refused with the reason and builds nothing: closed profiles, one sketch, the same sketch twice, a missing sketch, a bad thickness / side / facets' {
    $r = LoftBuild @{ facets = 'number_of_bends'; number_of_bends = 4 } 'u'; Assert ($r.bends -ge 3 -and $r.body_after.faces -gt 20) "the U: bends $($r.bends), faces $($r.body_after.faces)"
    $n = LoftProfiles 'circles'; $v0 = StateVer; $n0 = @(FeatureNames).Count
    $e = Fail (Loft $n) 'LOFTED_BEND_FAILED' 'closed profiles'; Assert ($e.error.message -like '*OPEN profile*') $e.error.message
    Fail (Invoke-Tool 'sheet_metal_lofted_bend' @{ sketches = "[`"$($n[0])`"]"; thickness = 0.0015 }) 'MISSING_PARAMETER' 'one sketch' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_lofted_bend' @{ sketches = "[`"$($n[0])`",`"$($n[0])`"]"; thickness = 0.0015 }) 'INVALID_PARAMETER' 'the same sketch twice' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_lofted_bend' @{ sketches = "[`"$($n[0])`",`"Sketch99`"]"; thickness = 0.0015 }) 'SKETCH_NOT_FOUND' 'a sketch that does not exist' | Out-Null
    Fail (Loft $n @{ thickness = 0 }) 'MISSING_PARAMETER' 'thickness 0' | Out-Null
    Fail (Loft $n @{ thickness_side = 'side3' }) 'INVALID_PARAMETER' 'a side that does not exist' | Out-Null
    Fail (Loft $n @{ facets = 'sideways' }) 'INVALID_PARAMETER' 'a facet option that does not exist' | Out-Null
    Assert ((StateVer) -eq $v0) 'a refused lofted bend leaves the state version alone'; Assert (@(FeatureNames).Count -eq $n0) "no feature was left behind: $n0 -> $(@(FeatureNames).Count)"
}

# ---- TAB AND SLOT (sheet_metal_tab_slot): a wall standing on a plate, two bodies ------------------------------------------------------------------------------------------------------------------------
# the plate 100 x 60 x 1.5 mm (z -1.5 .. 0) and the wall 80 x 1.5 x 30 mm (x -40 .. 40, y -0.75 .. 0.75, z 0 .. 30) as a body of its own (extrude_feature merge = false). The tab edge is the wall's bottom edge on +y,
# the slot face the plate's top face. The edge's START vertex is at x = +40 (the offsets and the tab positions are measured from it).
function WallOnPlate {
    Fresh-Part
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.1; height = 0.06 }) 'plate sketch' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.0015; feature_type = 'boss'; reverse = $true }) 'plate' | Out-Null
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.08; height = 0.0015 }) 'wall sketch' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.03; feature_type = 'boss'; merge = $false }) 'wall (its own body)' | Out-Null
}
function TabSlot([hashtable]$more = @{}) { Invoke-Tool 'sheet_metal_tab_slot' (Merge @{ edge_point = '[0,0.00075,0]'; slot_face_point = '[0.02,0.02,0]'; tab_length = 0.01 } $more) }
# the x (mm) of the planar faces that look along x, below the plate's top face and inside the plate: the ends of the tabs and of the slots, sorted
function TabXs { @((Get-Faces).faces | Where-Object { $_.planar -and [math]::Abs([math]::Abs($_.normal[0]) - 1) -lt 1e-6 -and [math]::Abs($_.point[0]) -lt 0.0499 -and $_.point[2] -lt 0 } | ForEach-Object { [double][math]::Round($_.point[0] * 1000, 3) } | Sort-Object) }
function SameXs($got, $want, [string]$what) {
    $g = @($got | ForEach-Object { [double]$_ }); $w = @($want | ForEach-Object { [double]$_ })
    $ok = $g.Count -eq $w.Count; if ($ok) { for ($i = 0; $i -lt $g.Count; $i++) { if ([math]::Abs($g[$i] - $w[$i]) -gt 0.0011) { $ok = $false } } }
    Assert $ok "$what : faces at x = $($g -join ', '), expected $($w -join ', ')"
}

Test 'T00 TAB AND SLOT: 3 tabs of 10 mm on the wall''s bottom edge, 5 mm from each end, clearance 0.2 mm - a Tab and a Slot feature, the wall gains 3 x 10 x 1.5 x 1.5 = 67.5 mm3, the plate loses 3 x 10.4 x 1.7 x 1.5 = 79.56 mm3, 6 -> 18 faces each, the tabs stand where the arithmetic says' {
    WallOnPlate; $r = Rg (Ok (TabSlot @{ count = 3; start_offset = 0.005; end_offset = 0.005; slot_clearance = 0.0002 }) 'tab and slot'); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    Assert (@($r.features).Count -eq 2 -and $r.features[0] -like 'Tab and Slot-Tab*' -and $r.features[1] -like 'Tab and Slot-Slot*') "features: $($r.features -join ', ')"
    Near ($r.tab_volume_added * 1e9) 67.5 1e-3 'volume added to the wall (mm3)'; Near ($r.slot_volume_removed * 1e9) 79.56 1e-3 'volume removed from the plate (mm3)'
    Assert ($r.before.tab_body.faces -eq 6 -and $r.after.tab_body.faces -eq 18 -and $r.before.slot_body.faces -eq 6 -and $r.after.slot_body.faces -eq 18) "faces: $($r.before | ConvertTo-Json -Compress) -> $($r.after | ConvertTo-Json -Compress)"
    Near $r.edge_start[0] 0.04 1e-9 'the edge starts at x = +40 mm'; Near $r.edge_end[0] -0.04 1e-9 'and ends at x = -40 mm'
    SameXs (TabXs) @(-35.2, -35, -25, -24.8, -5.2, -5, 5, 5.2, 24.8, 25, 35, 35.2) 'tabs of 10 mm and slots of 10.4 mm, 5 mm from each end, equally spaced'
    Near $r.slot_clearance_across_thickness 0.0002 2e-6 'the slot is 0.2 mm wider than the tab across the thickness, whatever slot_clearance says (measured)'
    $z = Extent 2; Near $z[0] -0.0015 1e-9 'the tabs reach the plate''s bottom face (z = -1.5 mm)'; Near $z[1] 0.03 1e-9 'the wall is still 30 mm high'
}

Test 'T01 count and offsets: ONE tab sits at the start, 2 tabs at both ends, equal spacing between the offsets, an offset of 12 mm at the START (x = +40) and 2 mm at the END shifts the tabs toward -x; a longer tab and a smaller clearance' {
    WallOnPlate; $r1 = Rg (Ok (TabSlot @{ count = 1; start_offset = 0.005; end_offset = 0.005 }) 'one tab'); Assert ($r1.verified -eq $true) "problems: $($r1.problems -join ' | ')"; Near $r1.tabs[0].from 0.005 1e-9 'one tab starts at the start offset'
    SameXs (TabXs) @(24.9, 25, 35, 35.1) 'one tab at 25 .. 35 mm (start offset 5 mm from x = +40), its slot 0.1 mm wider at each end'
    WallOnPlate; $r2 = Rg (Ok (TabSlot @{ count = 2; start_offset = 0.005; end_offset = 0.005 }) 'two tabs'); Assert ($r2.verified -eq $true) "problems: $($r2.problems -join ' | ')"; Near $r2.tabs[1].to 0.075 1e-9 'the last tab ends 5 mm before the end'
    WallOnPlate; $r3 = Rg (Ok (TabSlot @{ count = 3; start_offset = 0.012; end_offset = 0.002; slot_clearance = 0.0002 }) 'offsets 12 / 2'); Assert ($r3.verified -eq $true) "problems: $($r3.problems -join ' | ')"
    SameXs (TabXs) @(-38.2, -38, -28, -27.8, -10.2, -10, 0, 0.2, 17.8, 18, 28, 28.2) 'offsets 12 (start, x = +40) / 2 (end)'
    WallOnPlate; $r4 = Rg (Ok (TabSlot @{ count = 4; tab_length = 0.008; start_offset = 0.005; end_offset = 0.005; slot_clearance = 0.0001 }) 'four 8 mm tabs'); Assert ($r4.verified -eq $true) "problems: $($r4.problems -join ' | ')"; Assert ($r4.after.tab_body.faces -eq 6 + 4 * 4) "faces $($r4.after.tab_body.faces)"
}

Test 'T02 edge treatment: a fillet or a chamfer on the ends of the tabs adds faces to the wall (6 more for 3 tabs) and takes material away from the plain tabs; none leaves them plain' {
    WallOnPlate; $plain = Rg (Ok (TabSlot @{ count = 3; start_offset = 0.005; end_offset = 0.005 }) 'plain'); Assert ($plain.after.tab_body.faces -eq 18) "plain tabs: $($plain.after.tab_body.faces) faces"
    foreach ($tr in 'fillet', 'chamfer') {
        WallOnPlate; $r = Rg (Ok (TabSlot @{ count = 3; start_offset = 0.005; end_offset = 0.005; edge_treatment = $tr; edge_treatment_size = 0.0005 }) $tr); Assert ($r.verified -eq $true) "$tr problems: $($r.problems -join ' | ')"
        Assert ($r.after.tab_body.faces -eq 24) "$tr : $($r.after.tab_body.faces) faces on the wall"; Assert ($r.tab_volume_added -lt $plain.tab_volume_added -and $r.tab_volume_added -gt 0.97 * $plain.tab_volume_added) "$tr : the tabs add $($r.tab_volume_added * 1e9) mm3 against $($plain.tab_volume_added * 1e9) mm3 plain"
    }
}

Test 'T03 the tab face: found by itself (the wall''s bottom face), accepted when named, and the wrong one (the wall''s SIDE face) fails the way it does in SolidWorks - nothing is left behind; an edge that does not touch the slot face' {
    WallOnPlate; $v0 = StateVer
    $e = Fail (TabSlot @{ tab_face_point = '[0,0.00075,0.015]'; start_offset = 0.005; end_offset = 0.005 }) 'TAB_SLOT_FAILED' 'the wall''s side face as the tab face'; Assert ($e.error.message -like '*touches*' -or $e.error.message -like '*lies ON the slot face*') $e.error.message
    $f = Fail (TabSlot @{ edge_point = '[0,0.00075,0.03]'; start_offset = 0.005; end_offset = 0.005 }) 'TAB_FACE_NOT_FOUND' 'the wall''s TOP edge does not touch the slot face'; Assert ($f.error.message -like '*touches the slot face*') $f.error.message
    Assert ((StateVer) -eq $v0) 'refusals leave the state version alone'; Assert (@(FeatureNames | Where-Object { $_ -like '*Tab and Slot*' }).Count -eq 0) 'no tab or slot feature was left behind'
    $wb = @((Get-Faces).faces | Where-Object { $_.planar -and $_.normal[2] -lt -0.999999 -and [math]::Abs($_.point[2]) -lt 1e-6 -and $_.area -gt 1e-4 })[0].i      # the wall's bottom face (a coincident face of the plate is not reachable by a point)
    $ok = Rg (Ok (TabSlot @{ tab_face = $wb; start_offset = 0.005; end_offset = 0.005 }) 'the bottom face named explicitly'); Assert ($ok.verified -eq $true) "problems: $($ok.problems -join ' | ')"
}

Test 'T04 refusals before anything is built: no edge, no slot face, no tab length, count 0, tabs that do not fit, slots that would run into each other, no clearance, a treatment without a size or of a kind that does not exist - the part is untouched' {
    WallOnPlate; $v0 = StateVer; $n0 = @(FeatureNames).Count
    Fail (Invoke-Tool 'sheet_metal_tab_slot' @{ slot_face_point = '[0.02,0.02,0]'; tab_length = 0.01 }) 'MISSING_PARAMETER' 'no edge' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_tab_slot' @{ edge_point = '[0,0.00075,0]'; tab_length = 0.01 }) 'MISSING_PARAMETER' 'no slot face' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_tab_slot' @{ edge_point = '[0,0.00075,0]'; slot_face_point = '[0.02,0.02,0]' }) 'MISSING_PARAMETER' 'no tab length' | Out-Null
    Fail (TabSlot @{ count = 0 }) 'INVALID_PARAMETER' 'count 0' | Out-Null
    $e = Fail (TabSlot @{ tab_length = 0.1 }) 'INVALID_PARAMETER' 'a tab longer than the edge'; Assert ($e.error.message -like '*does not fit*') $e.error.message
    $e = Fail (TabSlot @{ count = 6; tab_length = 0.01; slot_clearance = 0.004 }) 'INVALID_PARAMETER' 'slots that run into each other'; Assert ($e.error.message -like '*run into each other*') $e.error.message
    Fail (TabSlot @{ edge_treatment = 'fillet' }) 'MISSING_PARAMETER' 'a fillet without a size' | Out-Null
    Fail (TabSlot @{ edge_treatment = 'bevel'; edge_treatment_size = 0.0005 }) 'INVALID_PARAMETER' 'a treatment that does not exist' | Out-Null
    Fail (TabSlot @{ start_offset = -0.001 }) 'INVALID_PARAMETER' 'a negative offset' | Out-Null
    Fail (TabSlot @{ slot_clearance = 0 }) 'INVALID_PARAMETER' 'no clearance: SolidWorks cuts slots that match no tab end' | Out-Null
    Assert ((StateVer) -eq $v0) 'refusals leave the state version alone'; Assert (@(FeatureNames).Count -eq $n0) "no feature was left behind: $n0 -> $(@(FeatureNames).Count)"
}

# ---- MOVE FACE (move_face): a 20 x 20 x 10 mm block on the Front Plane (z 0 .. 10), and the sheet-metal tray ----------------------------------------------------------------------------------------------
function Block { Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.02; height = 0.02 }) 'block sketch' | Out-Null; Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.01; feature_type = 'boss' }) 'block' | Out-Null }
$BlockSides = '[[0.01,0,0.005],[-0.01,0,0.005],[0,0.01,0.005],[0,-0.01,0.005]]'

Test 'M00 MOVE FACE offset: the top face of the block moves +3 mm (10 -> 13 mm, +1200 mm3), reverse shrinks it (10 -> 7 mm); the definition is read back (offset, 3 mm, 1 face) and the distance is the dimension D1@Move Face1' {
    Block; $r = Rg (Ok (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; distance = 0.003 }) 'move face'); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"
    Assert ($r.feature -like 'Move Face*' -and $r.definition.move_type -eq 0 -and $r.definition.faces -eq 1 -and $r.definition.reverse -eq $false) "definition: $($r.definition | ConvertTo-Json -Compress)"; Near $r.definition.distance 0.003 1e-9 'distance read back'
    Assert (($r.dimensions -join ',') -eq 'D1@Move Face1') "dimensions: $($r.dimensions -join ',')"; Near $r.body_after.size[2] 0.013 1e-9 'the block is 13 mm high'; Near ($r.volume_change * 1e9) 1200 1e-3 'volume change (mm3)'
    Block; $q = Rg (Ok (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; distance = 0.003; reverse = $true }) 'move face reversed'); Near $q.body_after.size[2] 0.007 1e-9 'reversed: 7 mm high'; Near ($q.volume_change * 1e9) -1200 1e-3 'reversed volume change'; Assert ($q.definition.reverse -eq $true) 'the direction flag is kept'
}

Test 'M01 several faces in ONE feature: the four side faces +1 mm (20 -> 22 mm, 4840 mm3), -1 mm reversed (-> 18 mm, 3240 mm3); faces may be named by index as well as by point' {
    Block; $r = Rg (Ok (Invoke-Tool 'move_face' @{ points = $BlockSides; distance = 0.001 }) 'four faces'); Assert ($r.verified -eq $true -and $r.definition.faces -eq 4) "problems: $($r.problems -join ' | '); faces $($r.definition.faces)"
    Near $r.body_after.size[0] 0.022 1e-9 'x'; Near $r.body_after.size[1] 0.022 1e-9 'y'; Near $r.body_after.size[2] 0.01 1e-9 'z'; Near ($r.volume_change * 1e9) 840 1e-3 'volume change = 4 x 200 + 4 corner columns of 10 mm3'
    Block; $top = @((Get-Faces).faces | Where-Object { $_.planar -and $_.normal[2] -gt 0.999999 })[0].i; $q = Rg (Ok (Invoke-Tool 'move_face' @{ faces = "[$top]"; distance = 0.002 }) 'by index'); Near $q.body_after.size[2] 0.012 1e-9 'moved by index: 12 mm'
    Block; $s = Rg (Ok (Invoke-Tool 'move_face' @{ points = $BlockSides; distance = 0.001; reverse = $true }) 'four faces reversed'); Near $s.body_after.size[0] 0.018 1e-9 'reversed x'; Near ($s.volume_change * 1e9) -760 1e-3 'reversed volume change'
}

Test 'M02 translate (a displacement vector) and rotate (axis + angle): the top face moves 3 mm along +z by the vector [0,0,0.003]; a 10 degree turn about an axis through the top edge changes the block and is read back' {
    Block; $t = Rg (Ok (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; move_type = 'translate'; translation = '[0,0,0.003]' }) 'translate'); Assert ($t.verified -eq $true) "problems: $($t.problems -join ' | ')"; Near $t.body_after.size[2] 0.013 1e-9 'translated 3 mm'; Assert ($t.definition.move_type -eq 1) "move type $($t.definition.move_type)"
    Block; $r = Rg (Ok (Invoke-Tool 'move_face' @{ points = '[[0.01,0,0.005]]'; move_type = 'rotate'; rotation = '{"axis_point":[0.01,0,0],"axis_direction":[0,1,0],"angle":10}' }) 'rotate'); Assert ($r.definition.move_type -eq 2) "move type $($r.definition.move_type)"
    Assert ([math]::Abs($r.volume_change) -gt 1e-12) "the rotation changed nothing: $($r.volume_change)"
}

Test 'M03 the distance is a DIMENSION an equation can drive: D1@Move Face1 = a variable (5 mm) -> the block is 15 mm high; a new value re-sizes it (the user''s chassis: D1@Move Face1 = SheetThickness + 0.1 mm)' {
    Block; $r = Rg (Ok (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; distance = 0.003 }) 'move face')
    $e = Rg (Ok (Invoke-Tool 'equations' @{ action = 'set'; variables = '{"Lift":"5mm"}'; links = "{`"$($r.dimensions[0])`":`"Lift`"}" }) 'link'); Assert ($e.verified -eq $true) "equations: $($e | ConvertTo-Json -Compress -Depth 4)"
    $h = (Extent 2); Near ($h[1]) 0.015 1e-9 'the block is 15 mm high'
    Ok (Invoke-Tool 'equations' @{ action = 'set'; variables = '{"Lift":"1.7mm"}' }) 'new value' | Out-Null; $h = (Extent 2); Near ($h[1]) 0.0117 1e-9 'the block follows the variable: 11.7 mm'
}

Test 'M04 refusals before anything is built: no faces, no distance, a bad type, a translation or rotation missing / malformed, a point that is on no face - the part is untouched' {
    Block; $v0 = StateVer; $n0 = @(FeatureNames).Count
    Fail (Invoke-Tool 'move_face' @{ distance = 0.003 }) 'MISSING_PARAMETER' 'no faces' | Out-Null
    Fail (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]' }) 'MISSING_PARAMETER' 'no distance' | Out-Null
    Fail (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; distance = 0.003; move_type = 'twist' }) 'INVALID_PARAMETER' 'a move type that does not exist' | Out-Null
    Fail (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; move_type = 'translate' }) 'INVALID_PARAMETER' 'translate without a vector' | Out-Null
    Fail (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; move_type = 'translate'; translation = '[0,0,0]' }) 'MISSING_PARAMETER' 'translate by nothing' | Out-Null
    Fail (Invoke-Tool 'move_face' @{ points = '[[0,0,0.01]]'; move_type = 'rotate' }) 'MISSING_PARAMETER' 'rotate without an axis' | Out-Null
    Fail (Invoke-Tool 'move_face' @{ points = '[[0.5,0.5,0.5]]'; distance = 0.003 }) 'FACE_NOT_FOUND' 'a point that is on no face' | Out-Null
    Assert ((StateVer) -eq $v0) 'a refused move leaves the state version alone'; Assert (@(FeatureNames).Count -eq $n0) "no feature was left behind: $n0 -> $(@(FeatureNames).Count)"
}

Test 'M05 in a SHEET-METAL part: the four walls of the 25 mm tray are lowered by 2 mm at their tops in ONE Move Face (reverse), the part rebuilds without error and the flat pattern is still made' {
    Fixture 4; $tops = @((Get-Faces).faces | Where-Object { $_.planar -and $_.normal[2] -gt 0.999999 -and [math]::Abs($_.point[2] - 0.025) -lt 1e-6 } | ForEach-Object { , @($_.point[0], $_.point[1], $_.point[2]) })
    Assert ($tops.Count -eq 4) "the wall tops found: $($tops.Count)"; $pts = '[' + (($tops | ForEach-Object { '[' + (($_ | ForEach-Object { Num $_ }) -join ',') + ']' }) -join ',') + ']'
    $r = Rg (Ok (Invoke-Tool 'move_face' @{ points = $pts; distance = 0.002; reverse = $true }) 'move the wall tops'); Assert ($r.verified -eq $true -and $r.definition.faces -eq 4) "problems: $($r.problems -join ' | ')"
    $z = Extent 2; Near $z[1] 0.023 1e-6 'the walls are 23 mm high'; $fl = Flat; Assert ($fl.flat.size[0] -gt 0.1) "the flat pattern is still made: $($fl.flat.size -join ' x ')"
}

# ---- SWEPT FLANGE (sheet_metal_swept_flange): a path on the Top Plane from the origin along +x, an open profile on the Right Plane at the origin -------------------------------------------------------
# Two sketches in a row on STANDARD planes fail while the automatic snapshot is on: a SaveAs copy taken while a sketch is open closes it (fixed in Snapshots.cs, 2026-10-27 - it takes a restart of SolidWorks to
# load); the helper switches the snapshots off while the sketches are made.
function NoSnap([scriptblock]$body) {
    $m = [regex]::Match((Invoke-Bridge 'snapshots' 5000), '"intervalSeconds":(\d+)'); $prev = if ($m.Success) { [int]$m.Groups[1].Value } else { 120 }; [void](Invoke-Bridge 'snapshot_policy off' 5000)
    try { & $body } finally { if ($prev -gt 0) { [void](Invoke-Bridge "snapshot_policy $prev" 5000) } }
}
function Seg([double]$x1, [double]$y1, [double]$x2, [double]$y2) { Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = $x1; y1 = $y1; x2 = $x2; y2 = $y2 }) 'sketch line' | Out-Null }
$SweptProfiles = @{
    L    = { Seg 0.015 0 0 0; Seg 0 0 0 0.02 }
    U    = { Seg 0.015 0.02 0.015 0; Seg 0.015 0 0 0; Seg 0 0 0 0.02 }
    Z    = { Seg 0.015 0 0 0; Seg 0 0 0 0.02; Seg 0 0.02 -0.01 0.02 }
    line = { Seg 0 0 0 0.02 }
}
$SweptPaths = @{
    line   = { Seg 0 0 0.1 0 }
    corner = { Seg 0 0 0.1 0; Seg 0.1 0 0.1 -0.06 }
    arc    = { Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'arc_center'; cx = 0.0; cy = -0.05; x1 = 0.0; y1 = 0.0; x2 = 0.05; y2 = -0.05; direction = 1 }) 'path arc' | Out-Null }
}
function SweptSketches([string]$profile = 'L', [string]$path = 'line') {
    Fresh-Part
    $names = NoSnap {
        $p = Ok (Invoke-Tool 'create_sketch' @{ plane = 'Top Plane' }) 'path sketch'; $pn = $p.cadState.activeSketch; & $SweptPaths[$path]
        $q = Ok (Invoke-Tool 'create_sketch' @{ plane = 'Right Plane' }) 'profile sketch'; $qn = $q.cadState.activeSketch; & $SweptProfiles[$profile]
        @($qn, $pn)
    }
    , @($names)
}
function Swept($names, [hashtable]$more = @{}) { Invoke-Tool 'sheet_metal_swept_flange' (Merge @{ profile_sketch = $names[0]; path_sketch = $names[1]; thickness = 0.0015 } $more) }
function SweptBuild([string]$profile = 'L', [string]$path = 'line', [hashtable]$more = @{}) { $n = SweptSketches $profile $path; $r = Rg (Ok (Swept $n $more) "swept flange $profile / $path"); Assert ($r.verified -eq $true -and -not $r.problems) "problems: $($r.problems -join ' | ')"; $r }

Test 'S00 SWEPT FLANGE of an L profile (15 + 20 mm) along a 100 mm line: the first feature of an empty part, thickness 1.5 mm, 1 bend, volume 4880.144 mm3 and a (29 + pi/2 (r + K t)) x 100 mm blank - the closed forms of the sheet-metal maths' {
    $r = SweptBuild 'L' 'line'
    Assert ($r.feature -like 'Swept Flange*' -and @($r.new_features | Where-Object { $_ -like 'Sheet-Metal*' }).Count -eq 1) "features: $($r.new_features -join ', ')"; Near $r.sheet_thickness 0.0015 1e-9 'sheet thickness'; Assert ($r.bends -eq 1 -and $r.bodies -eq 1) "bends $($r.bends), bodies $($r.bodies)"
    Near ($r.volume_added * 1e9) 4880.144 0.01 'volume (mm3) = 1.5 x [(15 - 3) + (20 - 3) + pi/2 x 2.25] x 100'; Near $r.blank[0] 0.1 2e-5 'blank length'; Near $r.blank[1] (0.029 + [math]::PI / 2 * (0.0015 + $r.k_factor * 0.0015)) 2e-5 "blank width = 12 + 17 + pi/2 (r + K t), K = $($r.k_factor)"
    Near $r.body_after.size[0] 0.1 1e-6 'box x'; Near $r.body_after.size[1] 0.02 1e-6 'box y'; Near $r.body_after.size[2] 0.015 1e-6 'box z'
}

Test 'S01 reverse: the material goes to the other side of the profile (the profile becomes the inner face): 53.30 mm2 instead of 48.80 mm2 of cross-section, 100 x 21.5 x 16.5 mm; both are verified against their closed forms' {
    $a = SweptBuild 'L' 'line'; $b = SweptBuild 'L' 'line' @{ reverse = $true }
    Near ($b.volume_added * 1e9) 5330.144 0.01 'reversed volume (mm3)'; Near $b.body_after.size[1] 0.0215 1e-6 'reversed box y'; Near $b.body_after.size[2] 0.0165 1e-6 'reversed box z'; Assert ($a.material_side -ne $b.material_side) "the material side flips: $($a.material_side) / $($b.material_side)"
}

Test 'S02 other profiles: a U (2 bends, 22 faces), a Z (2 bends) and a single line (a flat plate 20 x 1.5 x 100, no bend) - each verified against the closed forms of its own profile' {
    $u = SweptBuild 'U' 'line'; Assert ($u.bends -eq 2 -and $u.body_after.faces -eq 22) "U: bends $($u.bends), faces $($u.body_after.faces)"
    $z = SweptBuild 'Z' 'line'; Assert ($z.bends -eq 2) "Z: bends $($z.bends)"
    $l = SweptBuild 'line' 'line'; Assert ($l.bends -eq 0 -and $l.body_after.faces -eq 6) "line: bends $($l.bends), faces $($l.body_after.faces)"; Near ($l.volume_added * 1e9) 3000 0.01 'a plate 20 x 1.5 x 100 mm = 3000 mm3'
}

Test 'S03 start and end offsets shorten the flange at the ends of the path: 10 mm + 5 mm off a 100 mm path = a 85 mm flange (volume and blank checked with the shortened path)' {
    $r = SweptBuild 'L' 'line' @{ start_offset = 0.01; end_offset = 0.005 }; Near $r.body_after.size[0] 0.085 1e-6 'length 85 mm'; Near ($r.volume_added * 1e9) (48.80144 * 85) 0.02 'volume'; Near $r.blank[0] 0.085 2e-5 'blank length'
}

Test 'S04 other paths: a path with a CORNER (the flange is mitred round it: 115 x 20 x 75 mm) and an ARC path (130 x 21.5 x 130 mm) build; their closed forms are listed as skipped' {
    $c = SweptBuild 'L' 'corner'; Assert (@($c.checks_skipped).Count -eq 1) "skipped: $($c.checks_skipped -join ' | ')"; Near $c.body_after.size[0] 0.115 1e-5 'corner: x'; Near $c.body_after.size[2] 0.075 1e-5 'corner: z'
    $a = SweptBuild 'L' 'arc'; Near $a.body_after.size[0] 0.130 1e-5 'arc: x'; Near $a.body_after.size[1] 0.0215 1e-5 'arc: y'; Near $a.body_after.size[2] 0.130 1e-5 'arc: z'; Assert ($a.bodies -eq 1) 'one body'
}

Test 'S05 refusals: no sketches, the same sketch twice, thickness 0, a sketch that does not exist, a radius too big for the profile (taken out again) - nothing is left behind' {
    $n = SweptSketches 'L' 'line'; $v0 = StateVer; $n0 = @(FeatureNames).Count
    Fail (Invoke-Tool 'sheet_metal_swept_flange' @{ thickness = 0.0015 }) 'MISSING_PARAMETER' 'no sketches' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_swept_flange' @{ profile_sketch = $n[0]; path_sketch = $n[0]; thickness = 0.0015 }) 'INVALID_PARAMETER' 'the same sketch twice' | Out-Null
    Fail (Swept $n @{ thickness = 0 }) 'MISSING_PARAMETER' 'thickness 0' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_swept_flange' @{ profile_sketch = $n[0]; path_sketch = 'Sketch99'; thickness = 0.0015 }) 'SKETCH_NOT_FOUND' 'a path that does not exist' | Out-Null
    Fail (Swept $n @{ bend_radius = 0.5 }) 'INVALID_PARAMETER' 'a radius of 500 mm' | Out-Null
    $e = Fail (Swept $n @{ bend_radius = 0.02 }) 'SWEPT_FLANGE_FAILED' 'a 20 mm radius on a 15 + 20 mm profile'; Assert ($e.error.message -like '*Check*' -or $e.error.message -like '*rebuild error*') $e.error.message
    Assert ((StateVer) -eq $v0) 'refusals leave the state version alone'; Assert (@(FeatureNames | Where-Object { $_ -match '\[(SweptFlange|SheetMetal)\]' }).Count -eq 0) "no swept flange or sheet-metal feature was left behind: $(@(FeatureNames) -join '; ')"
}

# ---- SHEET-METAL GAUGE TABLE in the base flange (sheet_metal_feature base_flange gauge_table= gauge=): thickness, radius and K from the table row, the cut list's "Sheet Metal Gauge" ---------------------------
# The tables are the samples of the SolidWorks install (the file location for gauge tables is user preference 61 - READ here, never written).
function GaugeFolder { $r = Invoke-Probe 'probe84_pref.cs' 'pref=61'; $m = [regex]::Match($r, "plain='([^']*)'"); if ($m.Success) { $m.Groups[1].Value } else { '' } }
function GaugePlate([hashtable]$more) {
    Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.1; height = 0.06 }) 'plate sketch' | Out-Null
    Invoke-Tool 'sheet_metal_feature' (Merge @{ feature_type = 'base_flange' } $more)
}
$GaugeSteel = 'sample table - steel - english units.xlsx'

Test 'Q00 GAUGE TABLE (steel, inches): 16 Gauge gives the thickness of the TABLE ROW (0.0598 in = 1.51892 mm), its first radius (0.075 in), its K-factor (0.5), and the cut list says 16GA.; 18 Gauge gives 0.0478 in; the plate is 100 x 60' {
    $dir = GaugeFolder; if (-not $dir -or -not (Test-Path (Join-Path $dir $GaugeSteel))) { throw "the gauge table folder (user preference 61) is '$dir': the sample table is not there" }
    $tbl = Join-Path $dir $GaugeSteel
    $a = Ok (GaugePlate @{ gauge_table = $tbl; gauge = '16 Gauge' }) 'base flange with a gauge'; $g = Rg $a; Assert ($g.gauge -eq '16 Gauge') "gauge $($g.gauge)"; Near $g.table_thickness 0.00151892 1e-8 'table thickness'; Near $g.table_radius 0.001905 1e-8 'table radius'; Near $g.table_k_factor 0.5 1e-9 'table K'
    $p = Params; Near $p.global.thickness 0.00151892 1e-8 'the part''s thickness is the table row''s'; Near $p.global.bend_radius 0.001905 1e-8 'bend radius'; Near $p.global.bend_allowance.k_factor 0.5 1e-9 'K-factor'
    Assert ($p.cut_list[0].properties.'Sheet Metal Gauge' -eq '16GA.') "cut list gauge: $($p.cut_list[0].properties.'Sheet Metal Gauge')"; Near ([double]$p.cut_list[0].properties.'Bounding Box Length') 100 0.01 'blank length (mm)'
    $b = Ok (GaugePlate @{ gauge_table = $tbl; gauge = '18 Gauge' }) 'base flange with 18 Gauge'; $q = Params; Near $q.global.thickness 0.00121412 1e-8 '18 gauge: 0.0478 in'; Assert ($q.cut_list[0].properties.'Sheet Metal Gauge' -eq '18GA.') "cut list gauge: $($q.cut_list[0].properties.'Sheet Metal Gauge')"
}

Test 'Q01 the gauge radius: one of the table row''s radii (0.10 in = 2.54 mm) instead of the first; a part WITHOUT a table is as before (thickness / radius / K from the call, no gauge in the cut list)' {
    $dir = GaugeFolder; $tbl = Join-Path $dir $GaugeSteel
    Ok (GaugePlate @{ gauge_table = $tbl; gauge = '16 Gauge'; gauge_radius = 0.00254 }) 'gauge with a radius' | Out-Null; $p = Params; Near $p.global.bend_radius 0.00254 1e-8 'the chosen table radius'; Near $p.global.thickness 0.00151892 1e-8 'thickness still from the table'
    Ok (GaugePlate @{ thickness = 0.002; bend_radius = 0.003; k_factor = 0.45 }) 'no table' | Out-Null; $q = Params; Near $q.global.thickness 0.002 1e-9 'plain call: thickness'; Near $q.global.bend_radius 0.003 1e-9 'plain call: radius'; Assert ($q.cut_list[0].properties.'Sheet Metal Gauge' -notlike '*GA.*') "no gauge in the cut list: $($q.cut_list[0].properties.'Sheet Metal Gauge')"
}

Test 'Q02 refusals: a gauge the table does not have lists the table''s gauges, a table that is not there, a gauge without a table, a table without a gauge - nothing is built' {
    $dir = GaugeFolder; $tbl = Join-Path $dir $GaugeSteel; Fresh-Part; Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = 0.1; height = 0.06 }) 'plate sketch' | Out-Null; $v0 = StateVer
    $e = Fail (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = 'Sketch1'; gauge_table = $tbl; gauge = '99 Gauge' }) 'GAUGE_NOT_FOUND' 'a gauge the table does not have'; Assert ($e.error.message -like '*16 Gauge*' -and $e.error.message -like '*3 Gauge*') $e.error.message
    Fail (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = 'Sketch1'; gauge_table = (Join-Path $dir 'no such table.xlsx'); gauge = '16 Gauge' }) 'FILE_NOT_FOUND' 'a table that is not there' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = 'Sketch1'; gauge = '16 Gauge' }) 'MISSING_PARAMETER' 'a gauge without a table' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; sketch_name = 'Sketch1'; gauge_table = $tbl }) 'MISSING_PARAMETER' 'a table without a gauge' | Out-Null
    Assert ((StateVer) -eq $v0) 'refusals leave the state version alone'; Assert (@(FeatureNames | Where-Object { $_ -match '\[(SheetMetal|SMBaseFlange)\]' }).Count -eq 0) 'no sheet-metal feature was created'
}

# ---- FORMING TOOLS (sheet_metal_forming_tool): the louvers, lances, embosses, ribs and extruded flanges of the design library placed on a face of the plate (100 x 60 x 1.5 mm, z -1.5 .. 0) ---------------------
# Measured with probes 86 / 87: the form stands on the far side of the face (against its normal), the pick point is the placement, the rotation turns the tool about it (counter-clockwise looking at the face
# from outside), flip changes nothing, SolidWorks accepts a tool that overhangs the edge (it extends the sheet) and a tool on the edge face - the tool refuses both and takes a failed feature out again.
function Form([hashtable]$more) { Invoke-Tool 'sheet_metal_forming_tool' $more }
function FormOk([hashtable]$more, [string]$what) { $r = Rg (Ok (Form $more) $what); Assert ($r.verified -eq $true -and -not $r.problems) "$what : problems: $($r.problems -join ' | ')"; $r }
function PartState { @{ volume = (Volume); features = (@(FeatureNames) -join '; '); version = (StateVer) } }
function SameState($a, $b, [string]$what) { Near $a.volume $b.volume 1e-13 "$what : the volume"; Assert ($a.features -eq $b.features) "$what : the features changed: $($a.features) -> $($b.features)"; Assert ($a.version -eq $b.version) "$what : the state version moved" }
# the depth of every tool of the library (the part's z extent in mm after a placement at the middle of the plate, measured 2026-10-27); the single rib is 103 mm long and does not fit the 60 mm plate
$FormDepth = [ordered]@{ 'circular emboss' = 6.5; 'counter sink emboss' = 5.513; 'counter sink emboss2' = 6.5; 'dimple' = 6.5; 'drafted rectangular emboss' = 7.504; 'extruded hole' = 2.0; 'rectangular flange' = 4.0; 'round flange' = 4.0;
    '90 degree lance' = 12.0; 'angled lance' = 13.061; 'arc lance' = 4.5; 'bridge lance' = 7.5; 'lance and form shovel' = 7.12; 'lance and form with bend' = 9.263; 'lance and form' = 7.5; 'louver' = 7.5 }

Test 'F00 THE LIBRARY: action list shows the 17 forming tools of the design library (embosses, extruded flanges, lances, louvers, ribs), each with a file that exists; filter_text narrows the list' {
    Fixture 0; $r = Rg (Ok (Form @{ action = 'list' }) 'list')
    Assert ($r.count -eq 17 -and @($r.tools).Count -eq 17) "tools: $($r.count)"; Assert (@($r.folders).Count -ge 1) 'a forming tools folder'
    $cats = @($r.tools | ForEach-Object { $_.category } | Sort-Object -Unique) -join ', '; Assert ($cats -eq 'embosses, extruded flanges, lances, louvers, ribs') "categories: $cats"
    foreach ($t in $r.tools) { Assert (Test-Path $t.path) "the file of '$($t.name)' is not there: $($t.path)" }
    Assert (@($r.tools | Where-Object { $_.name -eq 'louver' -and $_.relative_path -eq 'louvers/louver.sldftp' }).Count -eq 1) 'louvers/louver.sldftp is listed'
    $l = Rg (Ok (Form @{ action = 'list'; filter_text = 'lance' }) 'list lance'); Assert ($l.count -eq 7) "lances: $($l.count)"
    $n = Rg (Ok (Form @{ action = 'list'; filter_text = 'no such tool' }) 'list nothing'); Assert ($n.count -eq 0) "nothing: $($n.count)"
}

Test 'F01 A LOUVER at (10, 0) on the top face: built, read back (path, configuration, pick point, face normal), +158.25 mm3, 6 -> 20 faces, the plate becomes 100 x 60 x 7.5 mm and the form stands BELOW the face (against its normal)' {
    Fixture 0; $v0 = Volume
    $r = FormOk @{ tool = 'louver'; point = '[0.01,0,0]' } 'louver'
    Assert ($r.feature -eq 'louver1' -and $r.feature_type -eq 'FormToolInstance') "feature $($r.feature) [$($r.feature_type)]"; Assert ($r.tool -eq 'louver' -and $r.tool_path -like '*forming tools\louvers\louver.sldftp') "tool $($r.tool_path)"
    Assert ($r.configuration -eq 'Default') "configuration $($r.configuration)"; Near $r.rotation 0 1e-9 'rotation'
    Near $r.pick_point[0] 0.01 1e-9 'pick point x'; Near $r.pick_point[1] 0 1e-9 'pick point y'; Near $r.pick_point[2] 0 1e-9 'pick point z'
    Near $r.face_normal[2] 1 1e-9 'the placement face is the top face (normal +z)'; Near $r.pushes_toward[2] -1 1e-9 'the form is pushed against the normal'
    Near ($r.volume_change * 1e9) 158.248 0.01 'volume added (mm3)'; Near $r.volume_before $v0 1e-13 'volume before'
    Assert ($r.body_before.faces -eq 6 -and $r.body_after.faces -eq 20) "faces $($r.body_before.faces) -> $($r.body_after.faces)"; Assert ($r.feature_faces -eq 14) "the feature owns $($r.feature_faces) faces"
    Near $r.body_after.size[0] 0.1 2e-6 'plate x'; Near $r.body_after.size[1] 0.06 2e-6 'plate y'; Near $r.body_after.size[2] 0.0075 2e-6 'height 7.5 mm'
    $z = Extent 2; Near $z[1] 0 1e-9 'the top face stays at z = 0 (the louver itself has no flat face along z)'; Near ($z[1] - $r.body_after.size[2]) -0.0075 2e-6 'so the louver reaches z = -7.5 mm, below the plate'; Assert ($r.overhang_checked -eq $true) 'overhang was checked (the face is along an axis)'
    $names = FeatureNames; Assert ((IndexOfType $names 'FormToolInstance') -lt (IndexOfType $names 'FlatPattern')) "the form goes in BEFORE the flat pattern: $($names -join '; ')"
}

Test 'F02 NAME FORMS: a bare name, a path relative to the library and a full path all find the same tool - three louvers side by side on one plate (x = -30, 0, 30), each +158.25 mm3' {
    Fixture 0; $full = (Rg (Ok (Form @{ action = 'list'; filter_text = 'louvers/louver' }) 'list')).tools[0].path
    $a = FormOk @{ tool = 'louver'; point = '[-0.03,0,0]' } 'bare name'; $b = FormOk @{ tool = 'louvers/louver.sldftp'; point = '[0,0,0]' } 'relative path'; $c = FormOk @{ tool = $full; point = '[0.03,0,0]' } 'full path'
    Assert ($a.feature -eq 'louver1' -and $b.feature -eq 'louver2' -and $c.feature -eq 'louver3') "features: $($a.feature), $($b.feature), $($c.feature)"
    foreach ($r in $a, $b, $c) { Near ($r.volume_change * 1e9) 158.248 0.01 "$($r.feature): volume added (mm3)"; Assert ($r.tool_path -eq $a.tool_path) 'the same file' }
    Assert ($c.body_after.faces -eq 6 + 3 * 14) "faces after three louvers: $($c.body_after.faces)"
}

Test 'F03 THE PICK POINT IS THE PLACEMENT: a louver at (-20, 10) instead of (10, 0) moves the part''s centre of mass by exactly (dx, dy) x (volume added / volume) = (-0.5184, +0.1728) mm' {
    Fixture 0; $a = FormOk @{ tool = 'louver'; point = '[0.01,0,0]' } 'louver at (10, 0)'; Fixture 0; $b = FormOk @{ tool = 'louver'; point = '[-0.02,0.01,0]' } 'louver at (-20, 10)'
    $k = ($a.volume_after - $a.volume_before) / $a.volume_after
    Near ($b.centre_of_mass_after[0] - $a.centre_of_mass_after[0]) (-0.03 * $k) 2e-6 'centre of mass: x'; Near ($b.centre_of_mass_after[1] - $a.centre_of_mass_after[1]) (0.01 * $k) 2e-6 'centre of mass: y'; Near $b.centre_of_mass_after[2] $a.centre_of_mass_after[2] 2e-6 'centre of mass: z'
    Near $b.pick_point[0] -0.02 1e-9 'pick point x'; Near $b.pick_point[1] 0.01 1e-9 'pick point y'
}

Test 'F04 ROTATION is counter-clockwise looking at the face from outside: the angled lance (its mass sits 0.196 mm from the pick point on -x) turned by 90 / -90 / 180 degrees swings round the pick point; on the BOTTOM face the same rotation runs the other way round (seen from +z)' {
    $off = @{}
    foreach ($rot in 0, 90, -90, 180) { Fixture 0; $r = FormOk @{ tool = 'angled lance'; point = '[0,0,0]'; rotation = $rot } "angled lance, rotation $rot"; $off["top$rot"] = @($r.centre_of_mass_after[0], $r.centre_of_mass_after[1]); Near $r.rotation (($rot + 360) % 360) 1e-6 "rotation read back for $rot" }
    $o = $off['top0']; Assert ([math]::Abs($o[0]) -gt 1.5e-4) "the tool is off-centre by $($o[0] * 1000) mm: the rotation test needs that"
    Near $off['top90'][0] (-$o[1]) 2e-6 'top +90: x = -y0'; Near $off['top90'][1] $o[0] 2e-6 'top +90: y = x0'; Near $off['top-90'][0] $o[1] 2e-6 'top -90: x = y0'; Near $off['top-90'][1] (-$o[0]) 2e-6 'top -90: y = -x0'; Near $off['top180'][0] (-$o[0]) 2e-6 'top 180: x = -x0'; Near $off['top180'][1] (-$o[1]) 2e-6 'top 180: y = -y0'
    foreach ($rot in 0, 90) { Fixture 0; $r = FormOk @{ tool = 'angled lance'; point = '[0,0,-0.0015]'; rotation = $rot } "angled lance on the bottom face, rotation $rot"; $off["bottom$rot"] = @($r.centre_of_mass_after[0], $r.centre_of_mass_after[1]) }
    $b = $off['bottom0']; Near $b[0] (-$o[0]) 2e-6 'the bottom face mirrors the tool: x = -x0'; Near $off['bottom90'][0] $b[1] 2e-6 'bottom +90: x = y0'; Near $off['bottom90'][1] (-$b[0]) 2e-6 'bottom +90: y = -x0 (clockwise seen from +z = counter-clockwise seen from outside)'
}

Test 'F05 THE OTHER FACE forms the other way: the same louver on the BOTTOM face (z = -1.5) stands on +z - the part reaches z = +6 mm and the bottom face stays at -1.5; flip = true is refused (SolidWorks ignores it)' {
    Fixture 0; $r = FormOk @{ tool = 'louver'; point = '[0.01,0,-0.0015]' } 'louver on the bottom face'
    Near $r.face_normal[2] -1 1e-9 'the placement face is the bottom face (normal -z)'; Near $r.pushes_toward[2] 1 1e-9 'pushed toward +z'; Near ($r.volume_change * 1e9) 158.248 0.01 'same volume'
    $z = Extent 2; Near $z[0] -0.0015 1e-9 'the bottom face stays at z = -1.5'; Near ($z[0] + $r.body_after.size[2]) 0.006 2e-6 'so the louver reaches z = +6 mm, above the plate'
    $s = PartState; $e = Fail (Form @{ tool = 'dimple'; point = '[-0.03,0,0]'; flip = $true }) 'INVALID_PARAMETER' 'flip'; Assert ($e.error.message -like '*other face*') $e.error.message; SameState $s (PartState) 'flip refused'
}

Test 'F06 EVERY TOOL OF THE LIBRARY places at the middle of the plate, verified, with its own depth (embosses 2 - 7.5 mm, flanges 4 mm, lances 4.5 - 13 mm, louver 7.5 mm); the 103 mm rib does not fit the 60 mm plate and is refused (it would EXTEND the sheet), the part untouched' {
    $tools = (Rg (Ok (Form @{ action = 'list' }) 'list')).tools; $done = 0
    foreach ($t in $tools) {
        Fixture 0; $s = PartState
        if ($t.name -eq 'single rib') { $e = Fail (Form @{ tool = $t.relative_path; point = '[0,0,0]' }) 'FORMING_TOOL_OVERHANG' 'single rib'; Assert ($e.error.message -like '*EXTENDED the sheet*') $e.error.message; SameState $s (PartState) 'single rib refused'; continue }
        Assert ($FormDepth.Contains($t.name)) "no depth for '$($t.name)'"
        $r = FormOk @{ tool = $t.relative_path; point = '[0,0,0]' } $t.name; Near $r.body_after.size[2] ($FormDepth[$t.name] / 1000) 1e-6 "$($t.name): height (mm)"; Near $r.body_after.size[0] 0.1 2e-6 "$($t.name): plate x"; Near $r.body_after.size[1] 0.06 2e-6 "$($t.name): plate y"
        Assert ([math]::Abs($r.volume_change) -gt 1e-12) "$($t.name): volume changed by $($r.volume_change)"; $done++
    }
    Assert ($done -eq 16) "$done tools placed"
}

Test 'F07 REFUSALS before anything is placed: no tool, no point, an unknown tool (with the similar ones), a point off the plate, a point not on the face given, the EDGE face of the sheet, a bad rotation - the part is untouched' {
    Fixture 0; $s = PartState
    Fail (Form @{ point = '[0,0,0]' }) 'MISSING_PARAMETER' 'no tool' | Out-Null; Fail (Form @{ tool = 'louver' }) 'MISSING_PARAMETER' 'no point or face' | Out-Null
    $e = Fail (Form @{ tool = 'louv'; point = '[0,0,0]' }) 'FORMING_TOOL_NOT_FOUND' 'an unknown tool'; Assert ($e.error.message -like '*louvers/louver.sldftp*') "similar tools are named: $($e.error.message)"
    Fail (Form @{ tool = 'C:\no\such\tool.sldftp'; point = '[0,0,0]' }) 'FORMING_TOOL_NOT_FOUND' 'a full path that is not there' | Out-Null; Fail (Form @{ tool = 'lou*'; point = '[0,0,0]' }) 'INVALID_PARAMETER' 'a wildcard' | Out-Null
    Fail (Form @{ tool = 'louver'; point = '[0.08,0,0]' }) 'FACE_NOT_FOUND' 'a point off the plate' | Out-Null; Fail (Form @{ tool = 'louver'; point = '[0,0]' }) 'INVALID_PARAMETER' 'a point with two numbers' | Out-Null
    $e = Fail (Form @{ tool = 'louver'; point = '[0.05,0,-0.00075]' }) 'INVALID_PARAMETER' 'the edge face of the sheet'; Assert ($e.error.message -like '*THICKNESS face*') $e.error.message
    Fail (Form @{ tool = 'louver'; face = 99 }) 'FACE_NOT_FOUND' 'a face index out of range' | Out-Null; Fail (Form @{ tool = 'louver'; point = '[0,0,0]'; rotation = 100000 }) 'INVALID_PARAMETER' 'a rotation of 100000 degrees' | Out-Null
    $faces = (Get-Faces).faces; $bottom = 0; for ($i = 0; $i -lt @($faces).Count; $i++) { if ($faces[$i].planar -and $faces[$i].normal[2] -lt -0.999) { $bottom = [int]$faces[$i].i } }
    $e = Fail (Form @{ tool = 'louver'; face = $bottom; point = '[0,0,0]' }) 'INVALID_PARAMETER' 'a point on the top face with the bottom face given'; Assert ($e.error.message -like '*not on the face*') $e.error.message
    SameState $s (PartState) 'refusals'
    $f = FormOk @{ tool = 'dimple'; face = $bottom } 'a face and no point'; Near $f.face_normal[2] -1 1e-9 'the tool went on the bottom face given by its index'; Near $f.pick_point[2] -0.0015 1e-9 'at a point of that face (z = -1.5)'
}

Test 'F08 WHAT SOLIDWORKS WOULD ACCEPT AND THE TOOL TAKES OUT AGAIN: a louver at the corner (it would extend the plate to 102 x 76.7 mm), a configuration the tool does not have (SolidWorks would silently use Default), a tool that overlaps another one (rebuild error) - the part is exactly as it was' {
    Fixture 0; $s = PartState
    $e = Fail (Form @{ tool = 'louver'; point = '[0.049,0.029,0]' }) 'FORMING_TOOL_OVERHANG' 'a louver at the corner'; Assert ($e.error.message -like '*EXTENDED the sheet*' -and $e.error.message -like '*taken out again*') $e.error.message; SameState $s (PartState) 'overhang'
    $e = Fail (Form @{ tool = 'louver'; point = '[0,0,0]'; configuration = 'NoSuchConfig' }) 'CONFIGURATION_NOT_FOUND' 'a configuration that does not exist'; Assert ($e.error.message -like "*'Default'*") $e.error.message; SameState $s (PartState) 'configuration'
    $ok = FormOk @{ tool = 'louver'; point = '[0,0,0]'; configuration = 'Default' } 'the configuration that exists'; Assert ($ok.configuration -eq 'Default') "configuration $($ok.configuration)"
    $s2 = PartState; $e = Fail (Form @{ tool = 'circular emboss'; point = '[-0.012,0,0]' }) 'FORMING_TOOL_FAILED' 'an emboss that overlaps the louver'; Assert ($e.error.message -like '*rebuild error*') $e.error.message; SameState $s2 (PartState) 'overlap'
    Assert (@(FeatureNames | Where-Object { $_ -match 'FormToolInstance' }).Count -eq 1) "only the louver is left: $(@(FeatureNames) -join '; ')"
}

Test 'F09 NOT A FLAT SHEET FACE, NOT A SHEET-METAL PART: a point on the BEND (a cylinder) is refused, so is a part with no Sheet-Metal feature; a tool on the flange wall (a flat face of a bent part) builds and forms through the wall' {
    Bent-Part; $s = PartState; $h = [math]::Sqrt(0.5) * 0.0015
    $e = Fail (Form @{ tool = 'dimple'; point = "[0,$(Num (0.027 + $h)),$(Num (0.0015 - $h))]" }) 'INVALID_PARAMETER' 'a point on the bend'; Assert ($e.error.message -like '*not planar*') $e.error.message; SameState $s (PartState) 'bend refused'
    $e = Fail (Form @{ tool = 'dimple'; point = '[0,0.0285,0.012]' }) 'FORMING_TOOL_OVERHANG' 'the 39 mm dimple on a 25 mm wall'; Assert ($e.error.message -like '*along z*') $e.error.message; SameState $s (PartState) 'dimple on the wall refused'
    $w = FormOk @{ tool = 'counter sink emboss'; point = '[0,0.0285,0.012]' } 'a counter sink emboss on the flange''s inner face'; Near $w.face_normal[1] -1 1e-9 'the flange''s inner face looks toward -y'; Near $w.pushes_toward[1] 1 1e-9 'the form goes through the wall toward +y'; Assert ($w.overhang_checked -eq $true) 'overhang checked'
    Near $w.body_after.size[1] 0.064013 1.5e-6 'the emboss stands outside the wall: y runs from -30 to 28.5 + 5.513 mm'; Near $w.body_after.size[2] 0.0265 2e-6 'the wall does not grow in z'
    Fresh-Part; Fail (Form @{ tool = 'dimple'; point = '[0,0,0]' }) 'NOT_SHEET_METAL' 'an empty part' | Out-Null
}

Test 'F10 A LOUVER ARRAY with create_pattern: a linear pattern of the placed louver (4 instances, 20 mm apart) repeats the form - volume = plate + 4 x 158.248 mm3, 62 faces = 6 + 4 x 14, and the pattern goes in before the flat pattern' {
    Fixture 0; $r = FormOk @{ tool = 'louver'; point = '[-0.035,0,0]' } 'the seed louver'
    Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'linear'; feature_name = $r.feature; spacing = 0.02; count = 4; direction = 'X' }) 'linear pattern' | Out-Null
    Near ((Volume) * 1e9) (9000 + 4 * 158.248) 0.01 'volume (mm3)'; $n = (Get-Faces).face_count; Assert ($n -eq 62) "faces $n"
    $names = FeatureNames; $lp = IndexOfType $names 'LPattern'; Assert ($lp -gt (IndexOfType $names 'FormToolInstance') -and $lp -lt (IndexOfType $names 'FlatPattern')) "order: $($names -join '; ')"
}

# ---- CORNER RELIEF (sheet_metal_corner_relief): the four-wall tray (100 x 60 mm, 25 mm walls, 1.5 mm sheet, 20103.843 mm3, 42 faces, FOUR two-bend corners) ----------------------------------------------------------
# Measured by probe 88 (SetBodyScope is required; SolidWorks died twice while the relief types were tried - the tool offers only the five that were built). Volumes are the part's AFTER the relief, in mm3.
function Relief([hashtable]$more) { Invoke-Tool 'sheet_metal_corner_relief' $more }
function ReliefBuild([hashtable]$more, [string]$what) { Fixture 4; $r = Rg (Ok (Relief $more) $what); Assert ($r.verified -eq $true -and -not $r.problems) "$what : problems: $($r.problems -join ' | ')"; $r }

Test 'R00 CORNER RELIEF, circular 3 mm: ONE feature for the four two-bend corners (SetBodyScope, Initialize, CollectAllCorners, CreateFeature), 42 -> 46 faces, 20103.843 -> 19974.975 mm3, every corner read back as circular with the 3 mm width' {
    $r = ReliefBuild @{ relief = 'circular'; width = 0.003 } 'circular 3 mm'
    Assert ($r.feature -like 'Corner Relief*' -and $r.feature_type -eq 'CornerRelief' -and $r.corners_collected -eq 4) "feature $($r.feature) [$($r.feature_type)], corners $($r.corners_collected)"
    Assert ($r.body_before.faces -eq 42 -and $r.body_after.faces -eq 46) "faces $($r.body_before.faces) -> $($r.body_after.faces)"; Near ($r.volume_before * 1e9) 20103.843 0.01 'volume before (mm3)'; Near ($r.volume_after * 1e9) 19974.975 0.01 'volume after (mm3)'
    Assert ($r.definition.corner_count -eq 4 -and @($r.definition.corners).Count -eq 4) "definition: $($r.definition | ConvertTo-Json -Compress -Depth 4)"
    foreach ($k in $r.definition.corners) { Assert ($k.relief_type -eq 0) "corner $($k.index) relief type $($k.relief_type)"; Near $k.slot_width 0.003 1e-9 "corner $($k.index) width" }
    $names = FeatureNames; Assert ((IndexOfType $names 'CornerRelief') -ge 0 -and (IndexOfType $names 'CornerRelief') -lt (IndexOfType $names 'FlatPattern')) "the relief goes in before the flat pattern: $($names -join '; ')"
}

Test 'R01 the circular relief follows its diameter: 3 / 4 / 6 mm remove 128.87 / 226.68 / 510.01 mm3 from the four corners (the length makes no difference)' {
    foreach ($case in @(@(0.003, 19974.975, 46), @(0.004, 19877.163, 50), @(0.006, 19593.831, 50))) {
        $r = ReliefBuild @{ relief = 'circular'; width = $case[0] } "circular $($case[0])"; Near ($r.volume_after * 1e9) $case[1] 0.01 "volume with a $($case[0] * 1000) mm circle"; Assert ($r.body_after.faces -eq $case[2]) "faces $($r.body_after.faces)" }
    $r = ReliefBuild @{ relief = 'circular'; width = 0.003; length = 0.0015 } 'circular, another length'; Near ($r.volume_after * 1e9) 19974.975 0.01 'the length of a circular relief changes nothing'
}

Test 'R02 the other types: square and rectangular follow their LENGTH (3 / 4 / 5 / 6 mm), tear is fixed by the geometry, obround needs a slot about twice as long as wide (6 x 3, 8 x 4) - each read back, verified, the volume down' {
    foreach ($case in @(@('square', 0.003, 0.003, 20043.438, 58), @('square', 0.004, 0.003, 20028.616, 70), @('square', 0.005, 0.003, 19990.158, 70), @('rectangular', 0.004, 0.003, 20028.616, 70), @('rectangular', 0.006, 0.003, 19941.200, 59),
                        @('tear', 0.004, 0.003, 20070.879, 38), @('obround', 0.006, 0.003, 20011.248, 70), @('obround', 0.008, 0.004, 19967.719, 66))) {
        $r = ReliefBuild @{ relief = $case[0]; length = $case[1]; width = $case[2] } "$($case[0]) $($case[1])"; Near ($r.volume_after * 1e9) $case[3] 0.01 "$($case[0]) volume"; Assert ($r.body_after.faces -eq $case[4]) "$($case[0]) $($case[1]) : faces $($r.body_after.faces)"
        # a SQUARE relief is stored by SolidWorks as the rectangular type (9): the same length gives the same cut (square 4 and rectangular 4 both leave 20028.616 mm3, 70 faces)
        Assert (@($r.definition.corners | Where-Object { $_.relief_type -ne @{ circular = 0; square = 9; tear = 3; obround = 5; rectangular = 9 }[$case[0]] }).Count -eq 0) "$($case[0]) : the corners kept another type: $(@($r.definition.corners | ForEach-Object { $_.relief_type }) -join ',')" }
    $sq = ReliefBuild @{ relief = 'square'; length = 0.004; width = 0.003 } 'the square, for its note'; Assert ($sq.note -like '*rectangular type (9)*') 'a square relief says it is stored as the rectangular type'
}

Test 'R03 options: center_on_bend_lines changes the cut (50 faces, 19970.883 mm3) and reads back; tangent_to_bend reads back; add_filleted_corners is taken by a square relief (78 faces) and reported as a PROBLEM for a circular one that ignores it' {
    $c = ReliefBuild @{ relief = 'circular'; width = 0.003; center_on_bend_lines = $true } 'centre on the bend lines'; Near ($c.volume_after * 1e9) 19970.883 0.01 'centred on the bend lines'; Assert ($c.body_after.faces -eq 50 -and $c.definition.corners[0].center_on_bend_lines -eq $true) "faces $($c.body_after.faces)"
    $t = ReliefBuild @{ relief = 'circular'; width = 0.003; tangent_to_bend = $true } 'tangent'; Assert ($t.definition.corners[0].tangent_to_bend -eq $true) 'tangent_to_bend must read back'
    $f = ReliefBuild @{ relief = 'square'; length = 0.004; width = 0.004; add_filleted_corners = $true; fillet_diameter = 0.002 } 'filleted square'; Assert ($f.body_after.faces -eq 78 -and $f.definition.corners[0].add_filleted_corners -eq $true) "filleted square: faces $($f.body_after.faces)"; Near ($f.volume_after * 1e9) 20032.460 0.01 'filleted square volume'
    Fixture 4; $g = Rg (Ok (Relief @{ relief = 'circular'; width = 0.003; add_filleted_corners = $true; fillet_diameter = 0.002 }) 'filleted circle'); Assert ($g.verified -eq $false -and ($g.problems -join ' ') -like '*add_filleted_corners*') "a circular relief ignores filleted corners: $($g.problems -join ' | ')"
}

Test 'R04 refusals: a three-bend relief (it killed SolidWorks on two-bend corners), three_bend corners, ratio_to_thickness (builds nothing), a missing length / width, a circle under 2 x the thickness (SolidWorks builds nothing: taken as a failure, the part untouched), an obround that is too short' {
    Fixture 4; $v0 = StateVer; $vol0 = Volume; $n0 = @(FeatureNames).Count
    $e = Fail (Relief @{ relief = 'bend_waist'; width = 0.003 }) 'INVALID_PARAMETER' 'a three-bend relief'; Assert ($e.error.message -like '*three-bend*') $e.error.message
    Fail (Relief @{ relief = 'circular'; width = 0.003; corner_type = 'three_bend' }) 'INVALID_PARAMETER' 'three_bend corners' | Out-Null
    Fail (Relief @{ relief = 'circular'; width = 0.003; ratio_to_thickness = $true }) 'INVALID_PARAMETER' 'ratio_to_thickness' | Out-Null
    Fail (Relief @{ relief = 'square' }) 'MISSING_PARAMETER' 'square without a length' | Out-Null
    Fail (Relief @{ relief = 'obround'; length = 0.006 }) 'MISSING_PARAMETER' 'obround without a width' | Out-Null
    Fail (Relief @{ relief = 'circular'; width = 0.003; add_filleted_corners = $true }) 'MISSING_PARAMETER' 'filleted corners without a diameter' | Out-Null
    $e = Fail (Relief @{ relief = 'circular'; width = 0.0029 }) 'CORNER_RELIEF_FAILED' 'a circle under 2 x the thickness'; Assert ($e.error.message -like '*at least 2 x the thickness*') $e.error.message
    Fail (Relief @{ relief = 'obround'; length = 0.003; width = 0.004 }) 'CORNER_RELIEF_FAILED' 'an obround that is too short' | Out-Null
    Assert ((StateVer) -eq $v0) 'refusals leave the state version alone'; Near ((Volume) * 1e9) ($vol0 * 1e9) 1e-6 'the part is untouched'; Assert (@(FeatureNames).Count -eq $n0) "no feature was left behind: $n0 -> $(@(FeatureNames).Count)"
}

Test 'R05 a part without two-bend corners: a plain plate says NO_CORNERS (nothing touched); a part that is not sheet metal says NOT_SHEET_METAL' {
    Fixture 0; $v0 = StateVer; $e = Fail (Relief @{ relief = 'circular'; width = 0.003 }) 'NO_CORNERS' 'a plain plate'; Assert ($e.error.message -like '*two-bend corners*') $e.error.message; Assert ((StateVer) -eq $v0) 'no state change'
    Fresh-Part; Fail (Relief @{ relief = 'circular'; width = 0.003 }) 'NOT_SHEET_METAL' 'an empty part' | Out-Null
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
