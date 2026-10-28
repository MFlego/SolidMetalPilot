# Regression tests for the FLANGE RECIPE of fold-from-flat (sheet_metal_from_flat strategy 'miter_flange' | 'auto'): a flat whose corners are CLOSED cannot be folded with sketched bends, but the flat of a box
# made by ONE mitre flange (a base flange + a mitre flange round the plate with a wall / rim / lip profile) is recognisable - its bend lines are NESTED RECTANGLES - and is rebuilt from flanges.
# What they prove: the recipe reads the plate, the profile points, the directions and the rip gap out of a flat that SolidWorks itself wrote (round trips: part -> flat DXF -> part), the rebuilt part has the
# folded size of the original and its own flat pattern is the DXF (blank, 12 / 8 / 4 bends, outer cutting length, and the exported DXF line for line), the default strategy still refuses the flat and now says
# which recipe fits, and every flat that is NOT of this family is refused with the reason before anything is created. The optional -PathB runs the same on the user's Path B cabinet flat (read only).
# Needs SolidWorks running with the bridge (shell >= 0.9), the in-process tools loaded and NOTHING else open (every document is closed at the end).
# Usage: fromflat-recipe-tests.ps1 [-Force] [-Json out.json] [-Only 'FR01'] [-PathB '<..._pathb_api_flat.dxf>']
param([switch]$Force, [string]$Json = '', [string]$Only = '', [string]$PathB = '')
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
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'solidpilot-fromflat-recipe-tests'; New-Item -ItemType Directory -Force $tmp | Out-Null
$t = 0.0015; $r = 0.003; $K = 0.45; $BA = [math]::PI / 2 * ($r + $K * $t)      # the sheet of every test (meters): 1.5 mm, inside radius 3 mm, K 0.45
$sheet = @{ thickness = $t; bend_radius = $r; k_factor = $K }
function Plan([string]$Path, $More = @{}) { Invoke-Tool 'sheet_metal_from_flat' (Merge @{ dxf_path = $Path; plan_only = $true } $More) }
function From-Flat([string]$Path, $More = @{}) { Invoke-Tool 'sheet_metal_from_flat' (Merge (Merge @{ dxf_path = $Path } $sheet) $More) }

# A box made by ONE mitre flange, from tools: the plate W x L (outer sharp, top face z = 0), the profile ([[across, up], ...] in meters from the plate's top face), the rip gap. Returns the folded extents (planar
# faces) and the flat DXF the part writes (SolidWorks' own API export: HIDDEN = bends that fold up, continuous = down).
function MitreBox([double]$W, [double]$L, [string]$profile, [double]$gap, [string]$name) {
    Fresh-Part
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Front Plane'; width = $W; height = $L }) 'plate sketch' | Out-Null
    Ok (Invoke-Tool 'sheet_metal_feature' (Merge @{ feature_type = 'base_flange' } $sheet)) 'base flange' | Out-Null
    $e = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2]) -lt 1e-6 } | Sort-Object { [math]::Atan2($_.mid[1], $_.mid[0]) }; $loop = "[$((@($e | ForEach-Object { $_.i })) -join ',')]"
    Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = $loop; profile_points = $profile; rip_gap = $gap; trim_side_bends = $false; dimension_profile = $false }) 'mitre flange' | Out-Null
    $x = Extent 0; $y = Extent 1; $z = Extent 2
    $path = Join-Path $tmp "$name.SLDPRT"; Remove-Item $path -ErrorAction SilentlyContinue; Ok (Invoke-Tool 'save_document' @{ file_path = $path }) 'save the part' | Out-Null
    $dxf = Join-Path $tmp "$name.dxf"; Remove-Item $dxf -ErrorAction SilentlyContinue
    $fl = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = $dxf; dxf_flags = 5 }) 'export the flat'); Assert ($fl.dxf.written -eq $true) 'the flat DXF was written'
    $p = Params; $cut = [double]($p.cut_list[0].properties.'Cutting Length-Outer') * $(if ($p.units.length -eq 'in') { 0.0254 } else { 0.001 })
    [pscustomobject]@{ x = $x; y = $y; z = $z; dxf = $dxf; blank = $fl.flat.size; cut = $cut; bends = [int]$p.cut_list[0].properties.Bends }
}
# the rebuilt part must be the original: folded extents (planar faces), flat blank, bends, cutting length, and the flat DXF it writes line for line
function Same-As($orig, [string]$name) {
    $x = Extent 0; $y = Extent 1; $z = Extent 2
    foreach ($pair in @(@('x', $x, $orig.x), @('y', $y, $orig.y), @('z', $z, $orig.z))) { Near $pair[1][0] $pair[2][0] 2e-5 "folded $($pair[0]) min"; Near $pair[1][1] $pair[2][1] 2e-5 "folded $($pair[0]) max" }
    $p = Params; $cut = [double]($p.cut_list[0].properties.'Cutting Length-Outer') * $(if ($p.units.length -eq 'in') { 0.0254 } else { 0.001 }); Near $cut $orig.cut 2e-5 'outer cutting length'
    Assert ([int]$p.cut_list[0].properties.Bends -eq $orig.bends) "bends $($p.cut_list[0].properties.Bends) vs $($orig.bends)"
    $path = Join-Path $tmp "$name.SLDPRT"; Remove-Item $path -ErrorAction SilentlyContinue; Ok (Invoke-Tool 'save_document' @{ file_path = $path }) 'save the rebuilt part' | Out-Null
    $out = Join-Path $tmp "$name.dxf"; Remove-Item $out -ErrorAction SilentlyContinue
    Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = $out; dxf_flags = 5 }) 'export the rebuilt flat' | Out-Null
    $a = Dxf-Keys $orig.dxf; $b = Dxf-Keys $out
    $onlyIn = @($a.outline | Where-Object { $b.outline -notcontains $_ }); $onlyOut = @($b.outline | Where-Object { $a.outline -notcontains $_ })
    Assert ($onlyIn.Count -eq 0 -and $onlyOut.Count -eq 0) "outline only in the original: $($onlyIn -join ' | ') ;; only in the rebuilt part: $($onlyOut -join ' | ')"
    $bi = @($a.bends | Where-Object { $b.bends -notcontains $_ }); $bo = @($b.bends | Where-Object { $a.bends -notcontains $_ })
    Assert ($bi.Count -eq 0 -and $bo.Count -eq 0 -and $a.bends.Count -eq $b.bends.Count) "bend lines only in the original: $($bi -join ' | ') ;; only in the rebuilt part: $($bo -join ' | ')"
}

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== Fold from flat - the flange recipe  ($script:ToolBase)   sheet: t 1.5 mm, r 3 mm, K 0.45, BA $([math]::Round($BA * 1000, 4)) mm"

$narrow = 0.00005      # a rip gap of 0.05 mm: the wall edges stand r - BA/2 - g/sqrt(2) = 0.078 mm BEYOND the first bend lines (closed corners); above g = 0.09 mm the corners are open enough for sketched bends
$wallRimLip = '[[0,0.025],[0.01,0.025],[0.01,0.031]]'      # wall 25 mm up from the plate's top face, rim 10 mm INWARD, lip 6 mm up (the NEMA cabinet's shape: bends up, up, down)
Test 'FR00 the plan of a mitre-flange box (wall + rim + lip, made by tools): 12 bend lines in 3 nested rings, plate 100 x 70, the profile points and the rip gap READ BACK from the DXF; the default strategy refuses it and says which recipe fits' {
    $orig = MitreBox 0.1 0.07 $wallRimLip $narrow 'mbox'
    Assert ($orig.bends -eq 12) "the original has $($orig.bends) bends"
    $none = Rg (Ok (Plan $orig.dxf @{ strategy = 'miter_flange' }) 'plan without a sheet'); Assert ($none.route -eq 'miter_flange' -and $none.recipe.rings -eq 3 -and $none.recipe.kind -eq 'miter_flange') "route $($none.route) recipe: $($none.recipe | ConvertTo-Json -Compress -Depth 4)"
    Assert ($null -eq $none.recipe.profile_points -and $none.recipe.note -like '*thickness*') 'without the sheet the recipe reports its structure and asks for thickness / radius'
    Assert (($none.recipe.directions -join ',') -eq 'up,up,down') "directions from the two linetypes: $($none.recipe.directions -join ',')"
    $g = Rg (Ok (Plan $orig.dxf (Merge $sheet @{ strategy = 'miter_flange' })) 'plan with the sheet'); $rc = $g.recipe
    Near $rc.plate[0] 0.1 1e-7 'plate width (m)'; Near $rc.plate[1] 0.07 1e-7 'plate length (m)'
    $pp = @($rc.profile_points); Assert ($pp.Count -eq 3) "profile points: $($pp.Count)"
    $want = @(@(0, 0.025), @(0.01, 0.025), @(0.01, 0.031)); for ($i = 0; $i -lt 3; $i++) { Near $pp[$i][0] $want[$i][0] 1e-7 "profile point $i across"; Near $pp[$i][1] $want[$i][1] 1e-7 "profile point $i up" }
    Near $rc.rip_gap $narrow 1e-7 'the rip gap, read from where the wall edges stand: g = sqrt(2) (X - t - x_edge)'
    $e = Fail (Plan $orig.dxf) 'BEND_LINE_NOT_ON_OUTLINE' 'the default strategy refuses the closed flat'; Assert ($e.error.message -like '*mitre flange*' -and $e.error.message -like "*strategy = 'miter_flange'*") "the refusal names the recipe: $($e.error.message)"
    $a = Rg (Ok (Plan $orig.dxf (Merge $sheet @{ strategy = 'auto' })) 'auto plans the recipe'); Assert ($a.route -eq 'miter_flange') "auto: route $($a.route)"
}

Test 'FR01 ROUND TRIP: the mitre box is rebuilt from its flat - verified, the folded extents / blank / 12 bends / outer cutting length are the original, and the flat DXF it writes IS the original DXF' {
    $orig = MitreBox 0.1 0.07 $wallRimLip $narrow 'mbox'
    Fresh-Part
    $res = Rg (Ok (From-Flat $orig.dxf @{ strategy = 'miter_flange' }) 'from flat'); Assert ($res.verified -eq $true -and -not $res.problems) "problems: $($res.problems -join ' | ')"
    Assert ($res.route -eq 'miter_flange' -and $res.bends -eq 12 -and $res.bends_expected -eq 12) "route $($res.route), bends $($res.bends) / $($res.bends_expected)"
    Assert ($res.flat_deviation -lt 1e-5) "flat deviation $($res.flat_deviation)"; Assert ([math]::Abs($res.cutting_length_deviation) -lt 2e-5) "cutting length deviation $($res.cutting_length_deviation)"
    Assert (@($res.steps).Count -eq 2) "steps: $($res.steps -join ' | ')"; Near $res.rip_gap $narrow 1e-7 'the rip gap used'
    Same-As $orig 'mbox_rebuilt'
}

Test 'FR02 other profiles: a plain WALL (one ring, 4 bends) and a wall with an OUTWARD rim (bends up, DOWN) - each rebuilt from its flat to the original part, each with its own rip gap (0.02 / 0.03 mm) read back' {
    foreach ($case in @(@{ name = 'wall'; w = 0.09; l = 0.06; prof = '[[0,0.02]]'; gap = 0.00002; bends = 4; dirs = 'up' }, @{ name = 'flare'; w = 0.09; l = 0.06; prof = '[[0,0.02],[-0.012,0.02]]'; gap = 0.00003; bends = 8; dirs = 'up,down' })) {
        $orig = MitreBox $case.w $case.l $case.prof $case.gap "m_$($case.name)"; Assert ($orig.bends -eq $case.bends) "$($case.name): the original has $($orig.bends) bends"
        $g = Rg (Ok (Plan $orig.dxf (Merge $sheet @{ strategy = 'miter_flange' })) "plan $($case.name)"); Assert (($g.recipe.directions -join ',') -eq $case.dirs) "$($case.name): directions $($g.recipe.directions -join ',')"
        Near $g.recipe.rip_gap $case.gap 1e-7 "$($case.name): rip gap"
        Fresh-Part
        $res = Rg (Ok (From-Flat $orig.dxf @{ strategy = 'miter_flange' }) "from flat $($case.name)"); Assert ($res.verified -eq $true -and -not $res.problems) "$($case.name): problems: $($res.problems -join ' | ')"
        Same-As $orig "m_$($case.name)_rebuilt"
    }
}

Test 'FR03 strategy auto: a relieved tray still folds with SKETCHED BENDS (route sketched_bends), the mitre box takes the recipe; the default strategy builds the tray exactly as before' {
    $tray = Join-Path $tmp 'tray.dxf'; Write-Dxf $tray (FlatTray) 4
    $g = Rg (Ok (Plan $tray (Merge $sheet @{ strategy = 'auto' })) 'plan a tray with auto'); Assert ($g.route -eq 'sketched_bends' -and $g.bends.Count -eq 4) "route $($g.route)"
    Fresh-Part; $res = Rg (Ok (From-Flat $tray @{ strategy = 'auto' }) 'build a tray with auto'); Assert ($res.verified -eq $true -and $res.bends -eq 4 -and $res.route -eq 'sketched_bends') "the tray: verified $($res.verified), bends $($res.bends), route $($res.route)"
    $orig = MitreBox 0.1 0.07 '[[0,0.02]]' $narrow 'auto_box'
    Fresh-Part; $m = Rg (Ok (From-Flat $orig.dxf @{ strategy = 'auto' }) 'build the box with auto'); Assert ($m.route -eq 'miter_flange' -and $m.verified -eq $true) "the box: route $($m.route) verified $($m.verified) $($m.problems -join ' | ')"
    Assert ($m.sketched_bends_refusal.code -eq 'BEND_LINE_NOT_ON_OUTLINE') "the refusal that sent it to the recipe is reported: $($m.sketched_bends_refusal | ConvertTo-Json -Compress)"
    # a WIDE rip gap (0.5 mm; above g = sqrt(2) (r - BA/2) = 0.16 mm the wall edges stand INSIDE the bend lines) passes the plan of the sketched bends (nothing reaches across a line) but SolidWorks refuses the sketched bend:
    # auto takes the failed attempt back and builds the recipe, and says so
    $wide = MitreBox 0.1 0.07 '[[0,0.02]]' 0.0005 'wide_box'
    $gw = Rg (Ok (Plan $wide.dxf (Merge $sheet @{ strategy = 'auto' })) 'plan the wide-gap box'); Assert ($gw.route -eq 'sketched_bends' -and $gw.bends.Count -eq 4) "wide gap: the plan says $($gw.route)"
    Fresh-Part; $w = Rg (Ok (From-Flat $wide.dxf @{ strategy = 'auto' }) 'build the wide-gap box'); Assert ($w.route -eq 'miter_flange' -and $w.verified -eq $true) "wide gap: route $($w.route), problems: $($w.problems -join ' | ')"
    Assert ($w.sketched_bends_failure.code -eq 'BUILD_STEP_FAILED' -and $w.sketched_bends_failure.message -like '*rolled back*') "the failed first try is reported: $($w.sketched_bends_failure | ConvertTo-Json -Compress)"
    Near $w.rip_gap 0.0005 1e-7 'the wide rip gap read back'
    Same-As $wide 'wide_box_rebuilt'
}

Test 'FR04 flats that are NOT of this family are refused with the reason, before anything is created: strips that differ from side to side, bend lines not parallel to an axis, a first bend that folds DOWN, a bad strategy, no sheet' {
    # nested rings with unequal strips (10 mm across x, 15 mm across y) = differential flange sizes: a mitre flange has ONE profile
    $rings = @(Dxf-Polygon @(@(-70, -60), @(70, -60), @(70, 60), @(-70, 60)))
    foreach ($hs in @(@(40, 30), @(50, 45))) { $rings += @(Dxf-Polygon @(@(-$hs[0], -$hs[1]), @($hs[0], -$hs[1]), @($hs[0], $hs[1]), @(-$hs[0], $hs[1])) 'HIDDEN') }
    $p = Join-Path $tmp 'unequal.dxf'; Write-Dxf $p $rings 4
    $e = Fail (Plan $p (Merge $sheet @{ strategy = 'miter_flange' })) 'RECIPE_NOT_APPLICABLE' 'unequal strips'; Assert ($e.error.message -like '*ONE profile all round*' -and $e.error.message -like '*10*15*' -and $e.error.message -like '*sketched bends refuse*') "the reason: $($e.error.message)"
    $e = Fail (Plan $p (Merge $sheet @{ strategy = 'auto' })) 'BEND_LINE_NOT_ON_OUTLINE' 'auto: the refusal of the sketched bends stays the answer'; Assert ($e.error.message -like '*ONE profile all round*') "auto names why the recipe does not fit either: $($e.error.message)"
    # the same skeleton turned by 10 degrees: no bend line is parallel to an axis
    $c = [math]::Cos(10 * [math]::PI / 180); $s = [math]::Sin(10 * [math]::PI / 180); $rot = { param($x, $y) @(($x * $c - $y * $s), ($x * $s + $y * $c)) }
    $tilted = @(); foreach ($hs in @(@(40, 30), @(50, 40))) { $q = @(@(-$hs[0], -$hs[1]), @($hs[0], -$hs[1]), @($hs[0], $hs[1]), @(-$hs[0], $hs[1])) | ForEach-Object { , (& $rot $_[0] $_[1]) }; $tilted += @(Dxf-Polygon $q 'HIDDEN') }
    $q = @(@(-70, -60), @(70, -60), @(70, 60), @(-70, 60)) | ForEach-Object { , (& $rot $_[0] $_[1]) }; $tilted += @(Dxf-Polygon $q)
    $tp = Join-Path $tmp 'tilted.dxf'; Write-Dxf $tp $tilted 4
    $e = Fail (Plan $tp (Merge $sheet @{ strategy = 'miter_flange' })) 'RECIPE_NOT_APPLICABLE' 'tilted bend lines'; Assert ($e.error.message -like '*parallel to an axis*') "the reason: $($e.error.message)"
    # a first bend that folds down
    $orig = MitreBox 0.1 0.07 '[[0,0.02]]' $narrow 'down_box'
    $e = Fail (Plan $orig.dxf (Merge $sheet @{ strategy = 'miter_flange'; directions = '["down"]' })) 'RECIPE_NOT_APPLICABLE' 'a first bend that folds down'; Assert ($e.error.message -like '*DOWN*') "the reason: $($e.error.message)"
    Fail (Plan $orig.dxf @{ strategy = 'sideways' }) 'INVALID_PARAMETER' 'a strategy that does not exist' | Out-Null
    Fresh-Part; $v0 = StateVer
    Fail (Invoke-Tool 'sheet_metal_from_flat' @{ dxf_path = $orig.dxf; strategy = 'miter_flange' }) 'MISSING_PARAMETER' 'no sheet' | Out-Null
    Fail (From-Flat $p @{ strategy = 'miter_flange' }) 'RECIPE_NOT_APPLICABLE' 'building an unequal flat' | Out-Null
    Assert ((StateVer) -eq $v0) 'a refused build leaves the state version alone'
    $names = @(FeatureNames | Where-Object { $_ -match '\[(ProfileFeature|SheetMetal|BaseFlange|MiterFlange)\]' }); Assert ($names.Count -eq 0) "nothing may be created: $($names -join ', ')"
}

if ($PathB) {
    Test 'FR05 THE USER''S PATH B CABINET (read only): its flat DXF (12 x 18 in, wall 4 in, rim 0.75 in, lip 0.5 in) -> the plate 12 x 18 in, the profile [[0,4],[0.75,4],[0.75,4.5]] in and the 0.01 in rip gap are read back, and the cabinet is rebuilt from flanges' {
        $s16 = @{ thickness = 0.001524; bend_radius = 0.004572; k_factor = 0.45 }
        $g = Rg (Ok (Plan $PathB (Merge $s16 @{ strategy = 'miter_flange' })) 'plan Path B'); $rc = $g.recipe
        Near $rc.plate[0] 0.3048 1e-6 'plate width'; Near $rc.plate[1] 0.4572 1e-6 'plate length'
        $pp = @($rc.profile_points); $want = @(@(0, 0.1016), @(0.01905, 0.1016), @(0.01905, 0.1143)); for ($i = 0; $i -lt 3; $i++) { Near $pp[$i][0] $want[$i][0] 2e-6 "profile point $i across"; Near $pp[$i][1] $want[$i][1] 2e-6 "profile point $i up" }
        Near $rc.rip_gap 0.000254 2e-6 'rip gap 0.010 in'
        Fresh-Part; $res = Rg (Ok (Invoke-Tool 'sheet_metal_from_flat' (Merge @{ dxf_path = $PathB; strategy = 'miter_flange' } $s16)) 'Path B from flat')
        Assert ($res.verified -eq $true -and -not $res.problems) "problems: $($res.problems -join ' | ')"; Assert ($res.bends -eq 12) "bends $($res.bends)"
        Assert ($res.flat_deviation -lt 2e-5 -and [math]::Abs($res.cutting_length_deviation) -lt 1e-4) "flat deviation $($res.flat_deviation), cutting length deviation $($res.cutting_length_deviation)"
        $x = Extent 0; $y = Extent 1; Near ($x[1] - $x[0]) 0.3048 2e-5 'folded width 12 in'; Near ($y[1] - $y[0]) 0.4572 2e-5 'folded length 18 in'
    }
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
