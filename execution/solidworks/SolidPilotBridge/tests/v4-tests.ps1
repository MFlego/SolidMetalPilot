# The user's v4 enclosure (enclosure_nema_cabinet_dfm_cr_steel_16ga_copy_v4.SLDPRT: 12 x 18 in, 4 in deep, rim 1.00 in on x / 0.75 in on y, lip 0.5 in, t 1.6 mm, r 3 mm, K 0.5) REBUILT WITH TOOLS ONLY, in the
# user's recipe and order: base shape -> delete the cap -> surface offset inward by t -> four rip sketches -> Convert to Sheet Metal (in-process; bends picked in the order stored in the reference) -> Unfold ->
# ONE multi-radius fillet on the twelve outline corners (10 / 15 / 5 mm) -> corner trim (sketch with the user's spline, mid-plane surface extrude, surface cut, mirrors) -> Fold -> flat pattern + DXF.
# Every number was measured READ-ONLY on the reference (SHEETMETAL-FINDINGS.txt section 8): V00-V02 assert them as constants; V03 (needs -Reference <the user's v4 .SLDPRT>) exports the reference's flat DXF and
# compares it entity by entity - the reference file itself is never part of the repository and never saved or modified (opened read-only, closed without saving).
# V00 -> V01 -> V02 share ONE part (each continues where the previous one stopped): run them together. Needs SolidWorks running with the bridge (shell >= 0.9), tools loaded, NOTHING else open.
# Usage: v4-tests.ps1 [-Force] [-Json out.json] [-Only 'V0[012]'] [-Reference 'C:\...\..._copy_v4.SLDPRT']
param([switch]$Force, [string]$Json = '', [string]$Only = '', [string]$Reference = '')
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
$g = $V4Ref; $K = 0.5; $face = "[0,0,$(Num $g.t)]"
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'solidpilot-v4-tests'; New-Item -ItemType Directory -Force $tmp | Out-Null
function CutList { (Params).cut_list[0].properties }

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== the user's v4 rebuilt with tools  ($script:ToolBase)"

Test 'V00 base shape -> offset -> rips -> Convert in the USER''S pick order: 12 bends, folded 12 x 18 x 4.5 in, flat blank 571.214 x 710.914 mm (the reference''s, and the closed form)' {
    $v = V4Build $g; $c = $v.convert
    Assert ($c.verified -eq $true -and -not $c.problems) "convert: $($c.problems -join ' | ')"
    Assert ($c.lists.bend_edges -eq 12 -and $c.lists.rip_sketches -eq 4) "lists: $($c.lists | ConvertTo-Json -Compress)"
    $d = $c.definition; Assert ($d.CornerDefaults -eq 1 -and $d.ReliefType -eq 2 -and $d.ReverseThickness -eq $true -and $d.KeepBody -eq $false) 'overlap corners, tear relief, reverse thickness on'
    Near $d.SheetThickness 0.0016 1e-9 't'; Near $d.BendRadius 0.003 1e-9 'r'; Near $d.RipGap 0.00003 1e-9 'gap 0.03 mm'; Near $d.RipOverlapRatio 1 1e-9 'ratio 1'
    # the order of the picks IS the user's: plate edges +x -y -x +y, then the rips, then the wall tops -x +y +x -y, then the lip roots -x +y +x -y
    $ox = [math]::Round(($g.W / 2 - $g.t), 6); $oy = [math]::Round(($g.H / 2 - $g.t), 6); $ix = [math]::Round(($g.W / 2 - $g.rx - $g.t), 6); $iy = [math]::Round(($g.H / 2 - $g.ry - $g.t), 6)
    $want = @(@($ox, 0), @(0, -$oy), @(-$ox, 0), @(0, $oy), 'rips', @(-$ox, 0), @(0, $oy), @($ox, 0), @(0, -$oy), @(-$ix, 0), @(0, $iy), @($ix, 0), @(0, -$iy))
    $picks = @($c.picks); Assert ($picks.Count -eq 16) "picks: $($picks.Count)"
    $bi = 0; $wi = 0; foreach ($pk in $picks) { if ($pk.list -eq 'RipSketches') { continue }; $w = $want[$wi]; if ($w -eq 'rips') { $wi++; $w = $want[$wi] }; $wi++; Near $pk.mid[0] $w[0] 1e-5 "bend $($bi + 1) x"; Near $pk.mid[1] $w[1] 1e-5 "bend $($bi + 1) y"; $bi++ }
    $ripAt = ($picks | ForEach-Object { $_.list }) -join ','; Assert ($ripAt -eq ((1..4 | ForEach-Object { 'BendList' }) + (1..4 | ForEach-Object { 'RipSketches' }) + (1..8 | ForEach-Object { 'BendList' }) -join ',')) "rips come after the first series: $ripAt"
    $p = Params; Assert ([int]$p.cut_list[0].properties.Bends -eq 12) 'bends'; Near $p.global.bend_allowance.k_factor 0.5 1e-9 'K 0.5'
    $x = Extent 0; $y = Extent 1; $z = Extent 2; Near (Mm $x[0]) -152.4 1e-3 'x min'; Near (Mm $x[1]) 152.4 1e-3 'x max'; Near (Mm $y[0]) -228.6 1e-3 'y min'; Near (Mm $y[1]) 228.6 1e-3 'y max'; Near (Mm $z[0]) 0 1e-3 'z min'; Near (Mm $z[1]) 114.3 1e-3 'z max = 4 in + 0.5 in'
    $f = Flat; $cf = V4Blank $g $K; Near (Mm $f.flat.size[0]) 571.214 2e-3 'flat X = the reference'; Near (Mm $f.flat.size[1]) 710.914 2e-3 'flat Y = the reference'; Near (Mm $cf[0]) (Mm $f.flat.size[0]) 2e-3 'closed form X'; Near (Mm $cf[1]) (Mm $f.flat.size[1]) 2e-3 'closed form Y'
    $cl = CutList; Near ([double]$cl.'Bounding Box Area') 406084.23 0.05 'bounding box area = the reference''s'; Near ([double]$cl.'Bounding Box Area-Blank') 333502.63 0.05 'blank area before fillets and trims'
}

Test 'V01 unfold -> ONE multi-radius fillet on the 12 outline corners: all twelve exist at the reference''s positions (4 microns), radii 10 / 15 / 5 mm, the unfolded body is flat' {
    $u = Rg (Ok (Invoke-Tool 'sheet_metal_fold' @{ action = 'unfold'; fixed_face = $face }) 'unfold'); Assert ($u.verified -eq $true -and $u.flat -eq $true -and $u.bends_in_feature -eq 12) "unfold: $($u | ConvertTo-Json -Compress -Depth 3)"
    Near (Mm $u.size_after[0]) 571.214 2e-3 'unfolded X'; Near (Mm $u.size_after[1]) 710.914 2e-3 'unfolded Y'
    $r = V4Fillet $g $K; Assert ($r.verified -eq $true -and $r.multi_radius -eq $true -and $r.edges_in_feature -eq 12) "fillet: $($r | ConvertTo-Json -Compress -Depth 4)"
    $ref = @(@(150.78, 229.97, 10), @(150.78, 322.37, 15), @(125.38, 345.76, 5))      # the reference's Fillet1 items (mm), all four sign combinations
    foreach ($sx in 1, -1) { foreach ($sy in 1, -1) { foreach ($q in $ref) {
        $hit = @($r.radii_read_back | Where-Object { [math]::Abs($_.mid[0] * 1000 - $sx * $q[0]) -lt 0.01 -and [math]::Abs($_.mid[1] * 1000 - $sy * $q[1]) -lt 0.01 })
        Assert ($hit.Count -eq 1 -and [math]::Abs($hit[0].radius * 1000 - $q[2]) -lt 1e-6) "the corner ($($sx * $q[0]), $($sy * $q[1])) must carry $($q[2]) mm: $($hit | ConvertTo-Json -Compress)" } } }
    $names = FeatureNames; Assert ((IndexOfType $names 'UnFold') -lt (IndexOfType $names 'Fillet')) "Unfold before Fillet: $($names -join ' | ')"
}

Test 'V02 trim the four corners (the user''s spline, mid-plane ribbon, surface cut, two mirrors) -> Fold -> the cut list EQUALS THE REFERENCE''S: cutting length 2497.89, blank area 333518.6, mass 533.71; flat DXF of 90 entities' {
    $t = V4Trim $g; Near ($t.cut.removed_volume * 1e9) 1.948 0.05 'the lens the first cut removes (mm3)'
    $m = (FeatureNames) | Where-Object { $_.EndsWith('[MirrorPattern]') }; Assert (@($m).Count -eq 2) "two mirrors expected: $((FeatureNames) -join ' | ')"
    $f = Rg (Ok (Invoke-Tool 'sheet_metal_fold' @{ action = 'fold'; fixed_face = $face }) 'fold'); Assert ($f.verified -eq $true -and $f.bends_in_feature -eq 12) "fold: $($f | ConvertTo-Json -Compress -Depth 3)"
    $x = Extent 0; $y = Extent 1; $z = Extent 2; Near (Mm $x[1]) 152.4 1e-3 'folded x'; Near (Mm $y[1]) 228.6 1e-3 'folded y'; Near (Mm $z[1]) 114.3 1e-3 'folded z'
    $cl = CutList; Near ([double]$cl.'Cutting Length-Outer') 2497.89 0.02 'cutting length = the reference''s'; Near ([double]$cl.'Bounding Box Area-Blank') 333518.65 0.05 'blank area = the reference''s'; Near ([double]$cl.Mass) 533.71 0.02 'mass = the reference''s'; Assert ([int]$cl.Bends -eq 12) 'bends'
    $names = FeatureNames; $order = 'UnFold', 'Fillet', 'ExtruRefSurface', 'SurfCut', 'MirrorPattern', 'Fold' | ForEach-Object { IndexOfType $names $_ }
    Assert (($order -join ',') -eq (($order | Sort-Object) -join ',') -and ($order | Where-Object { $_ -lt 0 }).Count -eq 0) "feature order Unfold < Fillet < Surface-Extrude < SurfaceCut < Mirror < Fold: $($names -join ' | ')"
    $path = Join-Path $tmp 'v4_tools.SLDPRT'; Remove-Item "$tmp\v4_tools.*" -ErrorAction SilentlyContinue
    Ok (Invoke-Tool 'save_document' @{ file_path = $path }) 'save the part' | Out-Null
    $fl = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = (Join-Path $tmp 'v4_tools.dxf'); dxf_flags = 5 }) 'flat DXF'); Assert ($fl.dxf.written -eq $true) 'the DXF was written'
    $s = Dxf-Summary (Join-Path $tmp 'v4_tools.dxf'); Assert ($s.count -eq 90 -and $s.by['ARC'] -eq 12 -and $s.by['LINE'] -eq 64 -and $s.by['SPLINE'] -eq 12 -and $s.by['MTEXT'] -eq 2) "DXF entities: $($s.count) $($s.by | ConvertTo-Json -Compress)"
    Near $s.length 6790.142 0.05 'total length of the lines and arcs = the reference''s'
}

Test 'V03 CLOSURE: the flat DXF of the tool-built part against the DXF of the user''s v4 (read-only): every line and arc identical, the 12 splines at the same places (needs -Reference)' {
    if (-not $Reference) { '      (skipped: pass -Reference <the user''s ..._copy_v4.SLDPRT>)'; return }
    Assert (Test-Path $Reference) "reference not found: $Reference"
    Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close the tool-built part' | Out-Null
    $o = Invoke-Probe 'probe56_open_readonly.cs' $Reference; Assert ($o -like '*readOnly True*') "the reference must open READ-ONLY: $o"
    try {
        $refDxf = Join-Path $tmp 'v4_reference_flat.dxf'; Remove-Item $refDxf -ErrorAction SilentlyContinue
        $fl = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = $refDxf; dxf_flags = 5 }) 'flat DXF of the reference'); Assert ($fl.dxf.written -eq $true) 'the reference DXF was written'
    } finally { Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null }
    $a = Dxf-Summary $refDxf; $b = Dxf-Summary (Join-Path $tmp 'v4_tools.dxf')
    Assert ($a.count -eq $b.count) "entity counts: reference $($a.count), tools $($b.count)"
    foreach ($ty in 'ARC', 'LINE', 'SPLINE', 'MTEXT') { Assert ($a.by[$ty] -eq $b.by[$ty]) "$ty count: reference $($a.by[$ty]), tools $($b.by[$ty])" }
    Near $a.length $b.length 0.05 'total length of the lines and arcs'
    $key = { param($e) $f = { param($c, $k = 0) [math]::Round([double]::Parse($e.d[$c][$k], [Globalization.CultureInfo]::InvariantCulture), 2) }
        if ($e.type -eq 'LINE') { $p1 = "$(& $f 10),$(& $f 20)"; $p2 = "$(& $f 11),$(& $f 21)"; 'L ' + ((@($p1, $p2) | Sort-Object) -join ' - ') } elseif ($e.type -eq 'ARC') { "A $(& $f 10),$(& $f 20) r$(& $f 40) $([math]::Round([double]::Parse($e.d[50][0], [Globalization.CultureInfo]::InvariantCulture), 1))-$([math]::Round([double]::Parse($e.d[51][0], [Globalization.CultureInfo]::InvariantCulture), 1))" } else { $null } }
    $ka = @($a.entities | ForEach-Object { & $key $_ } | Where-Object { $_ }); $kb = @($b.entities | ForEach-Object { & $key $_ } | Where-Object { $_ })
    $onlyA = @(Compare-Object $ka $kb | Where-Object { $_.SideIndicator -eq '<=' }); $onlyB = @(Compare-Object $kb $ka | Where-Object { $_.SideIndicator -eq '<=' })
    Assert ($onlyA.Count -eq 0 -and $onlyB.Count -eq 0) "lines / arcs only in the reference: $(($onlyA | ForEach-Object { $_.InputObject }) -join ' | ') ;; only in the tool-built part: $(($onlyB | ForEach-Object { $_.InputObject }) -join ' | ')"
}

Test 'V04 THE INTENDED SET (16 ga 1.524 / r 4.572 / K 0.45, 12 x 18 in, box 3.25 in + lip 0.5 in) through the same pipeline: the user''s trim curve normalised on the corner chord folds and keeps every seam >= 0.027 mm' {
    $gi = @{ W = 0.3048; H = 0.4572; D = 0.08255; rx = 0.0254; ry = 0.01905; lip = 0.0127; t = 0.001524; r = 0.004572 }; $ki = 0.45; $fi = "[0,0,$(Num $gi.t)]"
    $v = V4Build $gi $V4; $c = $v.convert; Assert ($c.verified -eq $true -and -not $c.problems) "convert: $($c.problems -join ' | ')"; Assert ($c.lists.bend_edges -eq 12 -and $c.lists.rip_sketches -eq 4) "lists: $($c.lists | ConvertTo-Json -Compress)"
    $x = Extent 0; $y = Extent 1; $z = Extent 2; Near (Mm ($x[1] - $x[0])) 304.8 2e-3 'folded X 12 in'; Near (Mm ($y[1] - $y[0])) 457.2 2e-3 'folded Y 18 in'; Near (Mm ($z[1] - $z[0])) 95.25 2e-3 'folded Z 3.25 in + 0.5 in'
    $bl = V4Blank $gi $ki; $f = Flat; Near (Mm $f.flat.size[0]) (Mm $bl[0]) 3e-3 'flat blank X = the closed form'; Near (Mm $f.flat.size[1]) (Mm $bl[1]) 3e-3 'flat blank Y = the closed form'
    Ok (Invoke-Tool 'sheet_metal_fold' @{ action = 'unfold'; fixed_face = $fi }) 'unfold' | Out-Null
    $fil = V4Fillet $gi $ki; Assert ($fil.verified -eq $true) 'the twelve fillets'
    $ch = V4CornerChord $gi $ki; Near $ch.chord[0] ([math]::PI / 2 * ($gi.r + $ki * $gi.t)) 2e-6 'the chord is one bend allowance wide'; Near ($ch.A[1] - $ch.Bp[1]) (0.75 * ($gi.r + $gi.t)) 1e-5 'the chord drops tan(phi) (r + t) = 0.75 (r + t)'
    $tr = V4Trim $gi 'Plane3' (V4TrimSplineFor $ch 1.0); $mm3 = $tr.cut.removed_volume * 1e9; Assert ($mm3 -gt 1.5 -and $mm3 -lt 5) "the trim removes a lens of a few mm3 per corner: $mm3"
    $fo = Rg (Ok (Invoke-Tool 'sheet_metal_fold' @{ action = 'fold'; fixed_face = $fi }) 'fold with the trim'); Assert ($fo.verified -eq $true) "fold: $($fo.problems -join ' | ')"
    $cl = (Params).cut_list[0].properties; Assert ([int]$cl.Bends -eq 12) 'twelve bends'
    $seam = V4Seam $gi
    Assert ($seam.min_gap -ge 0.027) "every seam distance must stay >= 0.027 mm (the design gap is 0.03): the smallest is $($seam.min_gap) mm`n$($seam.raw)"
    Assert ($seam.min_area -ge 0.05) "no sliver face at the corner (the untrimmed corner has a 0.013 mm2 one): the smallest face is $($seam.min_area) mm2"
    $out = Join-Path $tmp 'intended_cabinet.SLDPRT'; Remove-Item $out -ErrorAction SilentlyContinue; Ok (Invoke-Tool 'save_document' @{ file_path = $out }) 'save' | Out-Null
    $fl = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = (Join-Path $tmp 'intended_cabinet.dxf'); dxf_flags = 5 }) 'flat DXF'); Assert ($fl.dxf.written -eq $true) 'the DXF was written'
    $d = Dxf-Summary (Join-Path $tmp 'intended_cabinet.dxf'); Assert ($d.by['ARC'] -eq 12 -and $d.by['SPLINE'] -ge 12) "the flat DXF has the 12 fillet arcs and the trim splines (16 here: 12 corner curves and the 4 of the trim): $($d.by | ConvertTo-Json -Compress)"
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
