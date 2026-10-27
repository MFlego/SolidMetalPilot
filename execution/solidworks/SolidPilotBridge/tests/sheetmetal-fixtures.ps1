# Shared helpers and fixtures of the live sheet-metal suites (convert-tests.ps1 and the v4 build tests). Dot-source it AFTER probes\_common.ps1 and probes\_tools.ps1:
#   . "$PSScriptRoot\sheetmetal-fixtures.ps1"
# Helpers : Assert / Near / Rg / Ok / Fail / Merge / Num / Mm / Sw / StateVer / Fresh-Part / Params / Flat / Converts / PmWindows
# Fixtures: Tray (an open box), RimFixture + RimSteps (the base shape and the ordered Convert recipe of the user's v4 enclosure), $V4 (its Convert settings), TrayBlank (closed-form flat blank of a tray)
function Assert($cond, [string]$msg) { if (-not $cond) { throw $msg } }
function Near([double]$a, [double]$b, [double]$tol, [string]$what) { if ([math]::Abs($a - $b) -gt $tol) { throw "$what : $a vs $b (tolerance $tol)" } }
function Rg($reply) { $reply.result_geometry }
function Ok($reply, [string]$what) { Assert ($reply.status -eq 'COMPLETED') "$what : $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Fail($reply, [string]$code, [string]$what) { Assert ($reply.status -eq 'FAILED' -and $reply.error.code -eq $code) "$what : expected FAILED/$code, got $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Merge($a, $b) { $r = @{}; foreach ($k in $a.Keys) { $r[$k] = $a[$k] }; foreach ($k in $b.Keys) { $r[$k] = $b[$k] }; $r }      # `+` on hashtables throws on a duplicate key; this lets $b win
function Num($v) { [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.######}', $v) }
function Mm($m) { [math]::Round($m * 1000, 3) }
function Sw { Get-Process SLDWORKS | Select-Object -First 1 }
function StateVer { [int](Invoke-RestMethod "$script:ToolBase/api/tool/state" -TimeoutSec 30).stateVersion }
function Fresh-Part { Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null; Ok (Invoke-Tool 'open_new_part' @{}) 'new part' | Out-Null }
function Params { Rg (Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'get' }) 'sheet_metal_params get') }
function Flat { Rg (Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flat_pattern_control') }
function Converts { (Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'feature list')).features | Where-Object { $_.type -eq 'SolidToSheetMetal' } }
function PmWindows { [int](Rg (Invoke-Tool 'ui_inspect' @{ action = 'windows' })).count }

# Run a typed probe INSIDE SolidWorks (probes\run-probe.ps1). A pipe reply now and then comes back EMPTY (the connection closes without a line; seen 2026-10-27, never twice in a row): ask again, and say so.
$script:ProbesDir = Join-Path $PSScriptRoot '..\probes'
function Invoke-Probe([string]$File, [string]$Arg = '') {
    for ($try = 1; $try -le 3; $try++) {
        $r = (& "$script:ProbesDir\run-probe.ps1" $File $Arg) -join "`n"
        if ($r.Trim().Length -gt 0) { return $r }
        Write-Host "      (empty pipe reply for $File, attempt $try)"; Start-Sleep -Milliseconds 400
    }
    return ''
}
# EXACT volume (m3) of the part (probe55_mass.cs; analyze_model(mass_properties) has six significant digits only); the edges that run through the thickness of a flat blank (parallel to z, length t) = the outline vertices
function Volume { $r = Invoke-Probe 'probe55_mass.cs'; if ($r -notmatch 'volume=([-+0-9.eE]+)') { throw "mass probe answered: $r" }; [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) }
function ThicknessEdges([double]$t) {
    (Get-Edges).edges | Where-Object { [math]::Abs($_.start[0] - $_.end[0]) -lt 1e-7 -and [math]::Abs($_.start[1] - $_.end[1]) -lt 1e-7 -and [math]::Abs([math]::Abs($_.start[2] - $_.end[2]) - $t) -lt 1e-7 }
}
function FeatureNames { (Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'feature list')).features | ForEach-Object { "$($_.name) [$($_.type)]" } }
function IndexOfType($names, [string]$type) { $n = @($names); for ($i = 0; $i -lt $n.Count; $i++) { if ($n[$i].EndsWith("[$type]")) { return $i } }; return -1 }      # FeatureNames lines read "Unfold1 [UnFold]"
function CountOfType($names, [string]$type) { @($names | Where-Object { $_.EndsWith("[$type]") }).Count }

# An open box: a solid whose top face was deleted -> ONE surface body, 5 faces. Default 100 x 60 x 30 mm, centred on the origin in X / Y, standing on the Front Plane.
$TrayEdges = '[[0.05,0,0],[0,0.03,0],[-0.05,0,0],[0,-0.03,0]]'      # the four edges of the back plate (z = 0) of the default tray, by midpoint
function Tray([double]$w = 0.1, [double]$h = 0.06, [double]$d = 0.03) {
    Fresh-Part
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = $w; height = $h }) 'tray rectangle' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = $d; feature_type = 'boss' }) 'tray extrude' | Out-Null
    Ok (Invoke-Tool 'delete_face' @{ faces = "[[0,0,$(Num $d)]]" }) 'tray open face' | Out-Null
}
# The base shape of a rim-and-lip enclosure at the user's v4 proportions (defaults: 12 x 18 in, rim 1.00 in on the 12 in sides and 0.75 in top / bottom, lip 0.5 in, 3.25 in deep): the box, a lip block on the
# rim plane, the front cap deleted, and four rip sketches (outer corner -> rim-inner corner) on the rim plane. Returns the rip sketch names.
function RimFixture([double]$W = 0.3048, [double]$H = 0.4572, [double]$D = 0.08255, [double]$rx = 0.0254, [double]$ry = 0.01905, [double]$lip = 0.0127) {
    Fresh-Part
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = $W; height = $H }) 'rim: box rectangle' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = $D; feature_type = 'boss' }) 'rim: box extrude' | Out-Null
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = $D }) 'rim: rim plane' | Out-Null
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Plane1'; width = $W - 2 * $rx; height = $H - 2 * $ry }) 'rim: lip rectangle' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = $lip; feature_type = 'boss' }) 'rim: lip extrude' | Out-Null
    Ok (Invoke-Tool 'delete_face' @{ faces = "[[0,0,$(Num ($D + $lip))]]" }) 'rim: open front' | Out-Null
    $names = @()
    foreach ($c in @(@(-1, 1), @(1, 1), @(-1, -1), @(1, -1))) {
        $r = Ok (Invoke-Tool 'create_sketch' @{ plane = 'Plane1' }) 'rim: rip sketch'
        $names += (($r | ConvertTo-Json -Depth 6 -Compress) -replace '.*?"(Sketch\d+)".*', '$1')
        Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = $c[0] * $W / 2; y1 = $c[1] * $H / 2; x2 = $c[0] * ($W / 2 - $rx); y2 = $c[1] * ($H / 2 - $ry) }) 'rim: rip line' | Out-Null
    }
    $names
}
# The ordered recipe of the user's v4: back-plate perimeter -> the four rip sketches -> rim outer edges -> lip root edges. `rot` rotates the order INSIDE each bend series (the order of the series stays).
function RimSteps([string[]]$names, [int]$rot = 0, [double]$W = 0.3048, [double]$H = 0.4572, [double]$D = 0.08255, [double]$rx = 0.0254, [double]$ry = 0.01905) {
    $ox = $W / 2; $oy = $H / 2; $ix = $ox - $rx; $iy = $oy - $ry
    $series = {
        param($hx, $hy, $z)
        $e = @(@($hx, 0, $z), @(0, $hy, $z), @(-$hx, 0, $z), @(0, -$hy, $z)); $o = @(); for ($i = 0; $i -lt 4; $i++) { $o += , $e[($i + $rot) % 4] }
        '[' + (($o | ForEach-Object { '[' + (($_ | ForEach-Object { Num $_ }) -join ',') + ']' }) -join ',') + ']'
    }
    '[{"list":"bend","edges":' + (& $series $ox $oy 0) + '},{"list":"rip","sketches":["' + ($names -join '","') + '"]},{"list":"bend","edges":' + (& $series $ox $oy $D) + '},{"list":"bend","edges":' + (& $series $ix $iy $D) + '}]'
}
# the Convert settings of the user's v4 (1.524 mm = 16 ga, R 4.572 mm = 3 t, material OUTSIDE the surface, overlapped corners with a 0.03 mm gap, tear relief, K 0.45)
$V4 = @{ thickness = 0.001524; bend_radius = 0.004572; reverse_thickness = 'on'; corner = 'overlap'; rip_gap = 0.00003; overlap_ratio = 1; relief = 'tear'; k_factor = 0.45 }
# The EXACT outer size of a part: the extreme coordinate (m) of the planar faces whose normal runs along `axis` (0 x, 1 y, 2 z) -> @(min, max). A body's bounding box is conservative by a few microns
# wherever bend surfaces reach the outside, so sizes are asserted on planar faces.
function Extent([int]$axis) {
    $v = @((Get-Faces).faces | Where-Object { $_.planar -and [math]::Abs($_.normal[$axis]) -gt 0.999999 } | ForEach-Object { $_.point[$axis] })
    @(($v | Measure-Object -Minimum).Minimum, ($v | Measure-Object -Maximum).Maximum)
}
# Closed form of the flat blank of the RimFixture converted with the material OUTSIDE the surface (reverse thickness on, the sheet runs from the surface outwards): along x the sheet path is
# back plate / bend / wall (D) / bend / rim (rx) / bend / lip, the surface being the INNER skin at the first two bends and the OUTER skin at the lip root, so
#   half = W/2 + D + rx + lip - 6 r - 2 t + 3 BA          (BA = pi/2 (r + K t));   the same along y with ry and H.   Returns @(X, Y) in the unit of the arguments.
function RimBlank([double]$W, [double]$H, [double]$D, [double]$rx, [double]$ry, [double]$lip, [double]$t, [double]$r, [double]$k) {
    $ba = [math]::PI / 2 * ($r + $k * $t)
    @((2 * ($W / 2 + $D + $rx + $lip - 6 * $r - 2 * $t + 3 * $ba)), (2 * ($H / 2 + $D + $ry + $lip - 6 * $r - 2 * $t + 3 * $ba)))
}
# ---- the user's v4 (enclosure_nema_cabinet_dfm_cr_steel_16ga_copy_v4.SLDPRT), rebuilt with tools ----------------------------------------------------------------------------------------
# Base shape = the OUTER skin: 12 x 18 in box, 4 in deep, a lip block on the rim plane (rim 1.00 in on x, 0.75 in on y, lip 0.5 in), the cap deleted; then offset INWARD by one thickness; four rip sketches on the
# offset rim plane (z = D - t) from the outer corner to the rim-inner corner; Convert with the material OUTSIDE the offset surface. The reference was measured read-only (2026-10-27): t 1.6 mm, r 3 mm, K 0.5,
# overlap corners, ratio 1, gap 0.03 mm, tear relief; flat blank 571.214 x 710.914 mm, 12 bends; BendEdges stored in the user's pick order (see V4Steps).
$V4Ref = @{ W = 0.3048; H = 0.4572; D = 0.1016; rx = 0.0254; ry = 0.01905; lip = 0.0127; t = 0.0016; r = 0.003 }
function V4Build([hashtable]$g = $V4Ref, [hashtable]$convert = $null, [switch]$StopBeforeConvert) {
    $W = $g.W; $H = $g.H; $D = $g.D; $rx = $g.rx; $ry = $g.ry; $lip = $g.lip; $t = $g.t; $r = $g.r
    if ($null -eq $convert) { $convert = @{ reverse_thickness = 'on'; corner = 'overlap'; rip_gap = 0.00003; overlap_ratio = 1; relief = 'tear' } }
    Fresh-Part
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ width = $W; height = $H }) 'v4: box rectangle' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = $D; feature_type = 'boss' }) 'v4: box extrude' | Out-Null
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = $D }) 'v4: lip plane' | Out-Null
    Ok (Invoke-Tool 'sketch_center_rectangle' @{ plane = 'Plane1'; width = $W - 2 * $rx; height = $H - 2 * $ry }) 'v4: lip rectangle' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = $lip; feature_type = 'boss' }) 'v4: lip extrude' | Out-Null
    Ok (Invoke-Tool 'delete_face' @{ faces = "[[0,0,$(Num ($D + $lip))]]" }) 'v4: delete the cap' | Out-Null
    $o = Rg (Ok (Invoke-Tool 'offset_surface' @{ body_point = '[0,0,0]'; distance = $t; direction = 'inward' }) 'v4: offset the skin inward by t'); Assert ($o.verified -eq $true) 'v4: the offset must verify'
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = $D - $t }) 'v4: offset rim plane' | Out-Null
    $ox = $W / 2 - $t; $oy = $H / 2 - $t; $ix = $W / 2 - $rx - $t; $iy = $H / 2 - $ry - $t           # offset surface: outer corner (ox, oy), rim-inner corner (ix, iy)
    $names = @{}
    foreach ($c in @(@('NW', -1, 1, 1), @('NE', 1, 1, 1), @('SW', -1, -1, -1), @('SE', 1, -1, 1))) {          # the reference's Sketch11..14: NW, NE and SE run outer -> inner, only SW inner -> outer
        $sx = $c[1]; $sy = $c[2]; $out = $c[3] -gt 0
        $s = Ok (Invoke-Tool 'create_sketch' @{ plane = 'Plane2' }) "v4: rip sketch $($c[0])"; $nm = (($s | ConvertTo-Json -Depth 6 -Compress) -replace '.*?"(Sketch\d+)".*', '$1'); $names[$c[0]] = $nm
        $a = @(($sx * $ox), ($sy * $oy)); $b = @(($sx * $ix), ($sy * $iy)); if (-not $out) { $tmp = $a; $a = $b; $b = $tmp }
        Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = $a[0]; y1 = $a[1]; x2 = $b[0]; y2 = $b[1] }) "v4: rip line $($c[0])" | Out-Null
    }
    if ($StopBeforeConvert) { return $names }
    $steps = V4Steps $names $g
    $call = Merge @{ fixed_face = "[0,0,$(Num $t)]"; steps = $steps; thickness = $t; bend_radius = $r } $convert
    [pscustomobject]@{ names = $names; steps = $steps; convert = (Rg (Ok (Invoke-Tool 'sheet_metal_convert' $call) 'v4: convert')) }
}
# the user's pick order, as stored in the reference's Convert-Solid1.BendEdges: plate edges +x -y -x +y, then (rip sketches Sketch11..14), then the wall-top edges -x +y +x -y, then the lip-root edges -x +y +x -y
function V4Steps($names, [hashtable]$g = $V4Ref) {
    $ox = $g.W / 2 - $g.t; $oy = $g.H / 2 - $g.t; $ix = $g.W / 2 - $g.rx - $g.t; $iy = $g.H / 2 - $g.ry - $g.t; $zp = $g.t; $zr = $g.D - $g.t
    $pt = { param($x, $y, $z) '[' + ((Num $x), (Num $y), (Num $z) -join ',') + ']' }
    $s1 = @((& $pt $ox 0 $zp), (& $pt 0 (-$oy) $zp), (& $pt (-$ox) 0 $zp), (& $pt 0 $oy $zp)) -join ','
    $s2 = @((& $pt (-$ox) 0 $zr), (& $pt 0 $oy $zr), (& $pt $ox 0 $zr), (& $pt 0 (-$oy) $zr)) -join ','
    $s3 = @((& $pt (-$ix) 0 $zr), (& $pt 0 $iy $zr), (& $pt $ix 0 $zr), (& $pt 0 (-$iy) $zr)) -join ','
    $rips = @($names.NW, $names.NE, $names.SW, $names.SE) -join '","'
    '[{"list":"bend","edges":[' + $s1 + ']},{"list":"rip","sketches":["' + $rips + '"]},{"list":"bend","edges":[' + $s2 + ']},{"list":"bend","edges":[' + $s3 + ']}]'
}
# The twelve outline corners of v4's UNFOLDED blank that the user rounds with ONE multi-radius fillet, from the closed-form unrolled geometry (x, y in mm, radius in mm; the corner's edge runs through the thickness):
#   y_A = (H/2 - t - r) + BA                         plate bend ends        radius 10      x = W/2 - t  (minus the 0.02 mm the overlap corner takes)
#   y_B = y_A + (D - 2 t - 2 r)                      wall straight ends     radius 15
#   y_C = y_B + BA + (ry - r - (r + t)) + BA         lip-root bend ends     radius 5       x = W/2 - rx - t
# (measured on the reference: (+-150.78, +-229.97) r 10, (+-150.78, +-322.37) r 15, (+-125.38, +-345.76) r 5)
function V4FilletTargets([hashtable]$g, [double]$k) {
    $ba = [math]::PI / 2 * ($g.r + $k * $g.t); $t = $g.t; $r = $g.r
    $yA = ($g.H / 2 - $t - $r) + $ba; $yB = $yA + ($g.D - 2 * $t - 2 * $r); $yC = $yB + $ba + ($g.ry - $r - ($r + $t)) + $ba
    $xo = ($g.W / 2 - $t) - 0.00002; $xi = ($g.W / 2 - $g.rx - $t) - 0.00002; $out = @()
    foreach ($sx in 1, -1) { foreach ($sy in 1, -1) {
        $out += , @(($sx * $xo * 1000), ($sy * $yA * 1000), 10); $out += , @(($sx * $xo * 1000), ($sy * $yB * 1000), 15); $out += , @(($sx * $xi * 1000), ($sy * $yC * 1000), 5) } }
    $out
}
# Find the thickness-direction edges of the unfolded blank nearest to each target (x mm, y mm, radius mm) and fillet them in ONE multi-radius feature. Returns the tool's result.
function V4Fillet([hashtable]$g, [double]$k, [double]$tolMm = 0.5) {
    $te = @(ThicknessEdges $g.t); $found = @()
    foreach ($w in (V4FilletTargets $g $k)) {
        $best = $te | Sort-Object { [math]::Abs($_.mid[0] * 1000 - $w[0]) + [math]::Abs($_.mid[1] * 1000 - $w[1]) } | Select-Object -First 1
        $d = [math]::Abs($best.mid[0] * 1000 - $w[0]) + [math]::Abs($best.mid[1] * 1000 - $w[1]); if ($d -gt $tolMm) { throw "no thickness edge within $tolMm mm of ($($w[0]), $($w[1])) (nearest off by $([math]::Round($d, 3)))" }
        $found += , @($best, $w[2])
    }
    $pts = '[' + (($found | ForEach-Object { '[' + ((Num $_[0].mid[0]), (Num $_[0].mid[1]), (Num $_[0].mid[2]) -join ',') + ']' }) -join ',') + ']'
    $rad = '[' + (($found | ForEach-Object { Num ($_[1] / 1000.0) }) -join ',') + ']'
    Rg (Ok (Invoke-Tool 'add_edge_feature' @{ feature_type = 'fillet'; radius_or_distance = 0.01; points = $pts; radii = $rad }) 'v4: one multi-radius fillet on the 12 corners')
}
# The user's corner trim (v4 Sketch19): a cubic B-spline from (269.938, 211.381) to (275.907, 207.92) mm - tangent to the straight rim mitre at the start (direction 0.8, -0.6) and to the lip edge at the end -
# sampled at 17 points (the sketch's fit points are recreated safely; the user's B-spline has 5 control points, the cut face matches to 1 micron) + the 9 micron offset-edge line at its end. The curve belongs to the
# corner geometry of THIS part (t 1.6, r 3, K 0.5, rim 1.00 / 0.75 in); another sheet / radius needs another curve.
$V4TrimSpline = @(0.269938052, 0.21138125, 0.270235172, 0.21113743, 0.270537797, 0.210854762, 0.270846929, 0.210543353, 0.271163572, 0.210213311, 0.27148873, 0.209874742, 0.271823407, 0.209537753, 0.272168605, 0.209212453, 0.272525328, 0.208908946,
    0.27289458, 0.208637342, 0.273277364, 0.208407747, 0.273674665, 0.208228858, 0.274087326, 0.20809855, 0.274516121, 0.20800968, 0.274961828, 0.207955078, 0.275425222, 0.207927575, 0.275907078, 0.20792)
# Trim the four corners of the UNFOLDED v4 blank: a plane one thickness above the Front Plane (`planeName`: Plane3 after V4Build's Plane1 and Plane2), the sketch (spline + the 9 micron line), a MID-PLANE surface
# extrude of 5 mm, the surface cut (flip false for a mid-plane ribbon; the tool takes the feature out again if it removes more than 100 mm3) and two mirrors (Right Plane, then Top Plane: cut + first mirror).
function V4Trim([hashtable]$g, [string]$planeName = 'Plane3', [double[]]$spline = $null) {
    if ($null -eq $spline) { $spline = $script:V4TrimSpline }
    Ok (Invoke-Tool 'add_reference_geometry' @{ type = 'plane'; ref_plane_name = 'Front Plane'; offset = $g.t }) 'v4: trim plane' | Out-Null
    $sk = Ok (Invoke-Tool 'create_sketch' @{ plane = $planeName }) 'v4: trim sketch'; $name = (($sk | ConvertTo-Json -Depth 6 -Compress) -replace '.*?"(Sketch\d+)".*', '$1')
    Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'spline'; points = $spline }) 'v4: trim spline' | Out-Null
    $n = $spline.Count; Ok (Invoke-Tool 'add_sketch_entity' @{ entity_type = 'line'; x1 = $spline[$n - 2]; y1 = $spline[$n - 1]; x2 = $spline[$n - 2]; y2 = $spline[$n - 1] + 0.0000087868 }) 'v4: offset-edge line' | Out-Null
    $e = Rg (Ok (Invoke-Tool 'surface_extrude' @{ sketch = $name; depth = 0.005; mid_plane = $true }) 'v4: trim ribbon')
    $c = Rg (Ok (Invoke-Tool 'surface_cut' @{ surface = $e.feature; flip = $false; max_removed_volume = 1e-7 }) 'v4: trim cut')
    $m1 = Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'mirror'; features_json = "[`"$($c.feature)`"]"; plane = 'Right Plane' }) 'v4: mirror about the Right Plane'
    $mn = ((FeatureNames) | Where-Object { $_.EndsWith('[MirrorPattern]') } | Select-Object -Last 1) -split ' ' | Select-Object -First 1
    Ok (Invoke-Tool 'create_pattern' @{ pattern_type = 'mirror'; features_json = "[`"$($c.feature)`",`"$mn`"]"; plane = 'Top Plane' }) 'v4: mirror cut + mirror about the Top Plane' | Out-Null
    [pscustomobject]@{ sketch = $name; ribbon = $e; cut = $c }
}
# The exact chord of the lip corner (+x +y) of the UNFOLDED, filleted blank (meters): A = the end of the straight rim mitre, B = the start of the lip's side edge. Between them runs the exact unrolled mitre (a B-curve);
# a 9 micron line leads on from B' to B. The closed form puts B at (half - (lip - r), H/2 - ry - t - 0.03 mm) with half = W/2 + D + rx + lip - 4t - 6r + 3 BA; probe 70 reads the exact end points in double precision
# (analyze_model rounds to a micron, and a trim spline that misses the vertex by a micron makes Fold return nothing). Returns @{ A; B; Bp; chord = @(dx, dy) }.
function V4CornerChord([hashtable]$g, [double]$k) {
    $ba = [math]::PI / 2 * ($g.r + $k * $g.t); $half = $g.W / 2 + $g.D + $g.rx + $g.lip - 4 * $g.t - 6 * $g.r + 3 * $ba
    $bx = ($half - ($g.lip - $g.r)) * 1000; $by = ($g.H / 2 - $g.ry - $g.t - 0.00003) * 1000
    $o = Invoke-Probe 'probe70_vertices_exact.cs' ([string]::Format([Globalization.CultureInfo]::InvariantCulture, 'x={0:0.####};y={1:0.####};r=12', $bx, $by))
    $ci = [Globalization.CultureInfo]::InvariantCulture; $num = '([-0-9.]+)'
    $curve = [regex]::Match($o, "edge bcurve length [0-9.]+\s+\($num, $num\) -> \($num, $num\)"); $short = [regex]::Match($o, "edge line length 0\.0000[0-9]+\s+\($num, $num\) -> \($num, $num\)")
    if (-not $curve.Success -or -not $short.Success) { throw "the corner curve was not found near ($bx, $by) mm: $o" }
    $c = 1..4 | ForEach-Object { [double]::Parse($curve.Groups[$_].Value, $ci) }; $sh = 1..4 | ForEach-Object { [double]::Parse($short.Groups[$_].Value, $ci) }
    $p1 = @($c[0], $c[1]); $p2 = @($c[2], $c[3]); if ($p1[0] -lt $p2[0]) { $A = $p1; $Bp = $p2 } else { $A = $p2; $Bp = $p1 }
    $q1 = @($sh[0], $sh[1]); $q2 = @($sh[2], $sh[3]); $B = if ($q1[1] -lt $q2[1]) { $q1 } else { $q2 }
    @{ A = $A; Bp = $Bp; B = $B; chord = @(($B[0] - $A[0]), ($A[1] - $B[1])) }
}
# The user's trim curve (v4 Sketch19, sampled: $V4TrimSpline) NORMALISED on its chord and mapped onto another corner chord; `lambda` scales its depth below the straight chord (1 = the user's shape). Returns the flat
# [x1, y1, x2, y2, ...] array (meters) that V4Trim takes. Measured on the intended 16 ga set: lambda 1 keeps every seam distance >= 0.0278 mm (the design gap is 0.03), lambda 0.5 still pinches to 0.0204 mm.
function V4TrimSplineFor($chord, [double]$lambda = 1.0) {
    $A4 = @(0.269938052, 0.21138125); $B4 = @(0.275907078, 0.20792); $sp = $script:V4TrimSpline; $n = $sp.Count / 2; $out = New-Object 'System.Collections.Generic.List[double]'
    for ($i = 0; $i -lt $n; $i++) {
        $u = ($sp[2 * $i] - $A4[0]) / ($B4[0] - $A4[0]); $v = ($sp[2 * $i + 1] - $B4[1]) / ($A4[1] - $B4[1])      # u: 0 at A .. 1 at B; v: 1 at A .. 0 at B
        $chordV = 1 - $u; $v2 = $chordV - $lambda * ($chordV - $v)
        $out.Add($chord.A[0] + $u * ($chord.B[0] - $chord.A[0])); $out.Add($chord.B[1] + $v2 * ($chord.A[1] - $chord.B[1]))
    }
    , $out.ToArray()
}
# The seam at the lip corner of the FOLDED part (probe 67, IMeasure between the small faces): the smallest distance between two faces (the design gap is the overlap gap, 0.03 mm; unmeasurable pairs are skipped)
# and the smallest face area (a sliver face of 0.013 mm2 is the mark of the untrimmed corner).
function V4Seam([hashtable]$g) {
    $cx = ($g.W / 2 - $g.rx - $g.t) * 1000; $cy = ($g.H / 2 - $g.ry - $g.t) * 1000; $cz = ($g.D + $g.lip - $g.t) * 1000 - 6
    $o = Invoke-Probe 'probe67_faces_near.cs' ([string]::Format([Globalization.CultureInfo]::InvariantCulture, 'x={0:0.####};y={1:0.####};z={2:0.####};r=16;maxarea=80;pairs=1', $cx, $cy, $cz))
    $areas = @([regex]::Matches($o, '#\d+ \w+(?: n\([^)]*\))? area ([0-9.]+) mm2') | ForEach-Object { [double]$_.Groups[1].Value })
    $dists = @([regex]::Matches($o, 'distance #\d+ <-> #\d+ = ([0-9.\-]+) mm') | ForEach-Object { [double]$_.Groups[1].Value } | Where-Object { $_ -ge 0 })
    [pscustomobject]@{ raw = $o; faces = $areas.Count; min_area = ($areas | Measure-Object -Minimum).Minimum; min_gap = ($dists | Measure-Object -Minimum).Minimum }
}
# ---- DXF (flat pattern) reading, no dependencies -------------------------------------------------------------------------------------------------------------------------------------------------------
function Read-Dxf([string]$path) {
    $lines = [IO.File]::ReadAllLines($path); $ents = New-Object System.Collections.ArrayList; $in = $false; $cur = $null
    for ($i = 0; $i + 1 -lt $lines.Count; $i += 2) {
        $code = [int]$lines[$i].Trim(); $val = $lines[$i + 1].Trim()
        if ($code -eq 0 -and $val -eq 'SECTION' -and $lines[$i + 3].Trim() -eq 'ENTITIES') { $in = $true; continue }
        if ($in -and $code -eq 0 -and $val -eq 'ENDSEC') { break }
        if (-not $in) { continue }
        if ($code -eq 0) { $cur = [pscustomobject]@{ type = $val; d = @{} }; [void]$ents.Add($cur) } elseif ($null -ne $cur) { if (-not $cur.d.ContainsKey($code)) { $cur.d[$code] = New-Object System.Collections.ArrayList }; [void]$cur.d[$code].Add($val) }
    }
    ,$ents
}
function Dxf-Length($e) {
    $f = { param($c, $k = 0) [double]::Parse($e.d[$c][$k], [Globalization.CultureInfo]::InvariantCulture) }
    switch ($e.type) { 'LINE' { [math]::Sqrt([math]::Pow((& $f 11) - (& $f 10), 2) + [math]::Pow((& $f 21) - (& $f 20), 2)) } 'ARC' { $d = (((& $f 51) - (& $f 50)) % 360 + 360) % 360; [math]::PI * $d / 180 * (& $f 40) } 'CIRCLE' { 2 * [math]::PI * (& $f 40) } default { 0 } }
}
function Dxf-Summary([string]$path) {
    $e = Read-Dxf $path; $by = @{}; foreach ($x in $e) { $by[$x.type] = 1 + [int]$by[$x.type] }
    $len = 0.0; foreach ($x in $e) { $len += [double](Dxf-Length $x) }
    [pscustomobject]@{ count = $e.Count; by = $by; length = $len; entities = $e }
}
# closed form of v4's flat blank WITH the inward offset: half = W/2 + D + rx + lip - 4 t - 6 r + 3 BA (and ry, H/2 along y), BA = pi/2 (r + K t). Returns @(X, Y) in the unit of the arguments.
function V4Blank([hashtable]$g, [double]$k) {
    $ba = [math]::PI / 2 * ($g.r + $k * $g.t)
    @((2 * ($g.W / 2 + $g.D + $g.rx + $g.lip - 4 * $g.t - 6 * $g.r + 3 * $ba)), (2 * ($g.H / 2 + $g.D + $g.ry + $g.lip - 4 * $g.t - 6 * $g.r + 3 * $ba)))
}
# Closed form of the flat blank of an open tray (inner / outer skin w x h, depth d) converted with ripped corners: X = w + 2 (BA + d - 2 s), BA = pi/2 (r + K t), s = r when the surface is the INNER skin
# (reverse thickness on) and r + t when it is the OUTER skin. Returns @(X, Y) in the unit of the arguments.
function TrayBlank([double]$w, [double]$h, [double]$d, [double]$t, [double]$r, [double]$k, [bool]$surfaceIsInner) {
    $ba = [math]::PI / 2 * ($r + $k * $t); $s = if ($surfaceIsInner) { $r } else { $r + $t }
    @(($w + 2 * ($ba + $d - 2 * $s)), ($h + 2 * ($ba + $d - 2 * $s)))
}

# ---- flat patterns written as ASCII DXF, for the fold-from-flat tests (sheet_metal_from_flat) -----------------------------------------------------------------------------------------------------
# entities: @{ t = 'LINE'; p = @(x1, y1, x2, y2); lt = 'HIDDEN' }  |  @{ t = 'ARC'; c = @(cx, cy); r = r; a0 = deg; a1 = deg }  (counter-clockwise from a0 to a1)  |  @{ t = 'CIRCLE'; c = @(cx, cy); r = r }.  Units: 4 = mm, 1 = in, 0 = unsaid.
function Write-Dxf([string]$Path, $Entities, [int]$Units = 4) {
    $ci = [Globalization.CultureInfo]::InvariantCulture; $sb = New-Object Text.StringBuilder
    $g = { param($c, $v) [void]$sb.Append(('{0,3}' -f $c)).Append("`r`n").Append([string]$v).Append("`r`n") }
    $n = { param($v) ([double]$v).ToString('0.######', $ci) }
    & $g 0 'SECTION'; & $g 2 'HEADER'; & $g 9 '$ACADVER'; & $g 1 'AC1015'; if ($Units -ne 0) { & $g 9 '$INSUNITS'; & $g 70 $Units }; & $g 0 'ENDSEC'
    & $g 0 'SECTION'; & $g 2 'ENTITIES'
    foreach ($e in $Entities) {
        $lt = if ($e.lt) { $e.lt } else { 'Continuous' }
        switch ($e.t) {
            'LINE' { & $g 0 'LINE'; & $g 8 '0'; & $g 6 $lt; & $g 10 (& $n $e.p[0]); & $g 20 (& $n $e.p[1]); & $g 30 '0.0'; & $g 11 (& $n $e.p[2]); & $g 21 (& $n $e.p[3]); & $g 31 '0.0' }
            'ARC' { & $g 0 'ARC'; & $g 8 '0'; & $g 6 $lt; & $g 10 (& $n $e.c[0]); & $g 20 (& $n $e.c[1]); & $g 30 '0.0'; & $g 40 (& $n $e.r); & $g 50 (& $n $e.a0); & $g 51 (& $n $e.a1) }
            'CIRCLE' { & $g 0 'CIRCLE'; & $g 8 '0'; & $g 6 $lt; & $g 10 (& $n $e.c[0]); & $g 20 (& $n $e.c[1]); & $g 30 '0.0'; & $g 40 (& $n $e.r) }
            'MTEXT' { & $g 0 'MTEXT'; & $g 8 '0'; & $g 10 '0.0'; & $g 20 '0.0'; & $g 30 '0.0'; & $g 1 'note' }
        }
    }
    & $g 0 'ENDSEC'; & $g 0 'EOF'
    [IO.File]::WriteAllText($Path, $sb.ToString(), [Text.Encoding]::ASCII)
}
# a closed polygon of points as LINE entities; one line
function Dxf-Polygon($pts, [string]$lt = 'Continuous') { for ($i = 0; $i -lt $pts.Count; $i++) { $a = $pts[$i]; $b = $pts[($i + 1) % $pts.Count]; @{ t = 'LINE'; p = @($a[0], $a[1], $b[0], $b[1]); lt = $lt } } }
function Dxf-Line($x1, $y1, $x2, $y2, [string]$lt = 'HIDDEN') { @{ t = 'LINE'; p = @($x1, $y1, $x2, $y2); lt = $lt } }
# A flat TRAY with corner reliefs (mm): the plate [-Wp/2, Wp/2] x [-Hp/2, Hp/2] with its four corner squares (g x g) cut away, four wall flaps of flat length L beyond the bend lines, each g short of the plate
# corners, the free corners of the flaps rounded with radius rc (0 = square). The four bend lines are HIDDEN; every other entity is the outline. With a circular hole of radius `hole` at hx, hy when hole > 0.
# Returns the entities as ONE array: assign it (`$ents = FlatTray ...`) before piping or adding to it.
function FlatTray([double]$Wp = 100, [double]$Hp = 60, [double]$L = 25, [double]$g = 5, [double]$rc = 0, [double]$hole = 0, [double]$hx = 10, [double]$hy = 5) {
    $a = $Wp / 2; $b = $Hp / 2; $ents = New-Object System.Collections.ArrayList
    $flaps = @(@{ o = @($a, 0); th = 0; hw = $b - $g }, @{ o = @(0, $b); th = 90; hw = $a - $g }, @{ o = @(-$a, 0); th = 180; hw = $b - $g }, @{ o = @(0, -$b); th = 270; hw = $a - $g })
    $corners = @(@($a, $b), @(-$a, $b), @(-$a, -$b), @($a, -$b))
    # a point of a flap's own frame (x along the flap, y along its bend line) in the sheet's frame
    $loc = { param($f, $x, $y) $c = [math]::Cos($f.th * [math]::PI / 180); $s = [math]::Sin($f.th * [math]::PI / 180); , @(($f.o[0] + $c * $x - $s * $y), ($f.o[1] + $s * $x + $c * $y)) }
    $line = { param($p, $q) [void]$ents.Add((Dxf-Line $p[0] $p[1] $q[0] $q[1] 'Continuous')) }
    for ($i = 0; $i -lt 4; $i++) {
        $f = $flaps[$i]; $hw = $f.hw; $next = $flaps[($i + 1) % 4]
        $s0 = & $loc $f 0 (-$hw); $s1 = & $loc $f 0 $hw
        if ($rc -gt 0) {
            & $line $s0 (& $loc $f ($L - $rc) (-$hw))
            [void]$ents.Add(@{ t = 'ARC'; c = (& $loc $f ($L - $rc) (-$hw + $rc)); r = $rc; a0 = (270 + $f.th) % 360; a1 = (360 + $f.th) % 360 })
            & $line (& $loc $f $L (-$hw + $rc)) (& $loc $f $L ($hw - $rc))
            [void]$ents.Add(@{ t = 'ARC'; c = (& $loc $f ($L - $rc) ($hw - $rc)); r = $rc; a0 = (0 + $f.th) % 360; a1 = (90 + $f.th) % 360 })
            & $line (& $loc $f ($L - $rc) $hw) $s1
        } else {
            & $line $s0 (& $loc $f $L (-$hw)); & $line (& $loc $f $L (-$hw)) (& $loc $f $L $hw); & $line (& $loc $f $L $hw) $s1
        }
        # the RELIEF at the corner between this flap and the next: the plate's corner square (g x g) is cut away, so that the bend zones of the two walls never meet in the same material
        $cx = $corners[$i][0]; $cy = $corners[$i][1]; $n = @(($cx - [math]::Sign($cx) * $g), ($cy - [math]::Sign($cy) * $g))
        & $line $s1 $n; & $line $n (& $loc $next 0 (-$next.hw))
        [void]$ents.Add((Dxf-Line $s0[0] $s0[1] $s1[0] $s1[1] 'HIDDEN'))
    }
    if ($hole -gt 0) { [void]$ents.Add(@{ t = 'CIRCLE'; c = @($hx, $hy); r = $hole }) }
    ,@($ents)
}
# the area (mm2) of the outline of FlatTray (the hole taken out)
function FlatTrayArea([double]$Wp = 100, [double]$Hp = 60, [double]$L = 25, [double]$g = 5, [double]$rc = 0, [double]$hole = 0) {
    $a = $Wp / 2; $b = $Hp / 2
    $Wp * $Hp - 4 * $g * $g + 2 * $L * 2 * ($b - $g) + 2 * $L * 2 * ($a - $g) - 8 * (1 - [math]::PI / 4) * $rc * $rc - [math]::PI * $hole * $hole
}
# A flat CHANNEL (mm): the plate [-Wp/2, Wp/2] x [-Hp/2, Hp/2] with a wall flap of length L1 on each x side and a lip of length L2 beyond it - a plain rectangle, bend lines across its full height.
# lipLine = 'HIDDEN' draws the lip bend lines like the wall bends (same direction); 'Continuous' draws them the other way (the lip turns back out).
function FlatChannel([double]$Wp = 80, [double]$Hp = 50, [double]$L1 = 30, [double]$L2 = 12, [string]$lipLine = 'HIDDEN') {
    $a = $Wp / 2; $b = $Hp / 2; $x = $a + $L1 + $L2
    $ents = New-Object System.Collections.ArrayList
    foreach ($e in (Dxf-Polygon @(@(-$x, -$b), @($x, -$b), @($x, $b), @(-$x, $b)))) { [void]$ents.Add($e) }
    foreach ($sx in 1, -1) { [void]$ents.Add((Dxf-Line ($sx * $a) (-$b) ($sx * $a) $b 'HIDDEN')); [void]$ents.Add((Dxf-Line ($sx * ($a + $L1)) (-$b) ($sx * ($a + $L1)) $b $lipLine)) }
    ,@($ents)
}
