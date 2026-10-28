# Regression tests for FOLD FROM FLAT (sheet_metal_from_flat): a sheet-metal part built from the flat pattern in an ASCII DXF - a base flange from the outline, then sketched bends, the outermost first.
# What they prove: the plan (outline / bend lines / regions / tree of bends / build order) is read correctly from the DXF - lines, arcs, circles, hidden and continuous bend lines, units, chords that end
# on outline corners - and every flat that cannot be folded is refused BEFORE anything is created, with the reason; the built parts have the sizes of the CLOSED-FORM sheet-metal maths (bend allowance
# BA = pi/2 (r + K t), outside setback r + t); the part's OWN flat pattern is the DXF outline (bounding box, exact volume = area x t, and its DXF export = the input DXF); the direction of every bend follows the
# linetype (or `directions`); the lip that turns back is a lip that turns back.
# The flats are WRITTEN BY THE TESTS (FlatTray / FlatChannel in sheetmetal-fixtures.ps1): nothing of the user's is needed. Needs SolidWorks running with the bridge (shell >= 0.9), the in-process tools loaded
# and NOTHING else open (every document is closed at the end).
# Usage: fromflat-tests.ps1 [-Force] [-Json out.json] [-Only 'FF02']
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
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'solidpilot-fromflat-tests'; New-Item -ItemType Directory -Force $tmp | Out-Null
$t = 0.0015; $r = 0.003; $K = 0.45; $BA = [math]::PI / 2 * ($r + $K * $t)      # the sheet of every build test (meters): 1.5 mm, inside radius 3 mm, K 0.45
function Flat-File([string]$Name, $Entities, [int]$Units = 4) { $p = Join-Path $tmp $Name; Write-Dxf $p $Entities $Units; $p }
function Plan([string]$Path, $More = @{}) { Invoke-Tool 'sheet_metal_from_flat' (Merge @{ dxf_path = $Path; plan_only = $true } $More) }
function From-Flat([string]$Path, $More = @{}) { Invoke-Tool 'sheet_metal_from_flat' (Merge @{ dxf_path = $Path; thickness = $t; bend_radius = $r; k_factor = $K } $More) }
# A flat with CLOSED corners: the y flaps are 2 mm wider than the plate, so they reach 2 mm beyond the line of the x bends - what Convert and closed-corner flanges draw
function ClosedCornerFlat {
    $o = Dxf-Polygon @(@(50, -25), @(75, -25), @(75, 25), @(50, 25), @(50, 30), @(52, 30), @(52, 55), @(-52, 55), @(-52, 30), @(-50, 30), @(-50, 25), @(-75, 25), @(-75, -25), @(-50, -25), @(-50, -30), @(-52, -30), @(-52, -55), @(52, -55), @(52, -30), @(50, -30))
    @($o) + @(Dxf-Line 50 -25 50 25) + @(Dxf-Line -50 -25 -50 25) + @(Dxf-Line -50 30 50 30) + @(Dxf-Line -50 -30 50 -30)
}

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== Fold from flat  ($script:ToolBase)   sheet: t 1.5 mm, r 3 mm, K 0.45, BA $([math]::Round($BA * 1000, 4)) mm"

Test 'FF00 the plan of a tray flat: 20 outline pieces, 4 bend lines, 5 regions (the plate is the fixed one), one build group, every bend at depth 1, units from $INSUNITS' {
    $ents = FlatTray; $p = Flat-File 'tray.dxf' $ents; $reply = Plan $p; $g = Rg (Ok $reply 'plan')
    Assert ($g.plan_only -eq $true -and $g.verified -eq $true) 'plan_only reply'
    Assert ($g.units -eq 'mm' -and [math]::Abs($g.scale_to_meters - 0.001) -lt 1e-12) "units $($g.units) scale $($g.scale_to_meters)"
    Assert ($g.outline.pieces -eq 20 -and $g.outline.holes -eq 0) "outline: $($g.outline | ConvertTo-Json -Compress -Depth 4)"
    Near $g.outline.size[0] 0.150 1e-9 'outline width (m)'; Near $g.outline.size[1] 0.110 1e-9 'outline height (m)'
    Assert ($g.bends.Count -eq 4 -and $g.regions.Count -eq 5) "bends $($g.bends.Count), regions $($g.regions.Count)"
    $fixed = @($g.regions | Where-Object { $_.fixed }); Assert ($fixed.Count -eq 1) 'one fixed region'; Near $fixed[0].area 0.0059 1e-9 'the fixed region is the plate less its four 5 x 5 mm corner squares: 100 x 60 - 100 mm2'
    foreach ($b in $g.bends) { Assert ($b.depth -eq 1 -and $b.linetype -eq 'hidden' -and $b.direction -eq 'up' -and $b.near_region -eq $fixed[0].index) "bend $($b.index): $($b | ConvertTo-Json -Compress)" }
    Assert (@($g.build_order).Count -eq 1 -and @($g.build_order[0].bends).Count -eq 4 -and $g.build_order[0].depth -eq 1) "build order: $($g.build_order | ConvertTo-Json -Compress -Depth 4)"
    Assert (($g.warnings -join ' ') -like '*one linetype*') "the warning that the DXF says nothing about the bend direction: $($g.warnings -join ' | ')"
    $pt = $g.fixed_point; Assert ([math]::Abs($pt[0]) -lt 0.05 -and [math]::Abs($pt[1]) -lt 0.03) "the fixed point lies on the plate: $pt"
    # the same flat in INCHES (INSUNITS 1) reads as the same flat
    $inEnts = $ents | ForEach-Object { $e = $_; switch ($e.t) { 'LINE' { @{ t = 'LINE'; lt = $e.lt; p = @($e.p | ForEach-Object { $_ / 25.4 }) } } 'ARC' { @{ t = 'ARC'; c = @($e.c | ForEach-Object { $_ / 25.4 }); r = $e.r / 25.4; a0 = $e.a0; a1 = $e.a1 } } 'CIRCLE' { @{ t = 'CIRCLE'; c = @($e.c | ForEach-Object { $_ / 25.4 }); r = $e.r / 25.4 } } } }
    $inch = Flat-File 'tray_in.dxf' $inEnts 1
    $gi = Rg (Ok (Plan $inch) 'plan in inches'); Assert ($gi.units -eq 'in') "units $($gi.units)"; Near $gi.outline.size[0] 0.150 1e-6 'inch flat: width (m)'; Near $gi.outline.size[1] 0.110 1e-6 'inch flat: height (m)'
}

Test 'FF01 flats that cannot be read or folded are refused with the reason: missing / unreadable file, unsaid units, open outline, a bend line off the outline, two parts, crossing bend lines, a fixed point outside, a bad direction, CLOSED corners' {
    Fail (Invoke-Tool 'sheet_metal_from_flat' @{ plan_only = $true }) 'MISSING_PARAMETER' 'no dxf_path' | Out-Null
    Fail (Plan (Join-Path $tmp 'no-such-file.dxf')) 'FILE_NOT_FOUND' 'missing file' | Out-Null
    $tray = FlatTray; $noUnits = Flat-File 'no_units.dxf' $tray 0
    Fail (Plan $noUnits) 'UNITS_UNKNOWN' 'a DXF without $INSUNITS' | Out-Null
    $g = Rg (Ok (Plan $noUnits @{ units = 'mm' }) 'units said by the caller'); Assert ($g.units -eq 'mm' -and $g.bends.Count -eq 4) 'units = mm reads it'
    Fail (Plan $noUnits @{ units = 'furlong' }) 'INVALID_PARAMETER' 'a unit that does not exist' | Out-Null
    $junk = Join-Path $tmp 'junk.dxf'; Set-Content $junk "this is not`na DXF file"; Fail (Plan $junk) 'DXF_INVALID' 'junk' | Out-Null
    # an outline that does not close
    $open = $tray | Where-Object { -not ($_.t -eq 'LINE' -and $_.lt -eq 'Continuous' -and [math]::Abs($_.p[0] - 50) -lt 1e-9 -and [math]::Abs($_.p[1] + 25) -lt 1e-9) }
    Fail (Plan (Flat-File 'open.dxf' $open)) 'NO_OUTLINE' 'open outline' | Out-Null
    # a bend line in the middle of the plate that touches nothing
    $lost = $tray + @(Dxf-Line -10 -10 10 10)
    $e = Fail (Plan (Flat-File 'lost.dxf' $lost)) 'BEND_LINE_NOT_ON_OUTLINE' 'a bend line off the outline'; Assert ($e.error.message -like '*outline to the outline*') $e.error.message
    # a second part
    $two = $tray + @(Dxf-Polygon @(@(200, 0), @(220, 0), @(220, 20), @(200, 20)))
    Fail (Plan (Flat-File 'two.dxf' $two)) 'MULTIPLE_PARTS' 'two parts' | Out-Null
    # crossing bend lines (a bend across all the others)
    $chan = FlatChannel; $cross = $chan + @(Dxf-Line -82 0 82 0)
    $e = Fail (Plan (Flat-File 'cross.dxf' $cross)) 'BEND_LINE_NOT_ON_OUTLINE' 'crossing bend lines'
    Fail (Plan $noUnits @{ units = 'mm'; fixed_point = '[1, 1]' }) 'FIXED_POINT_OUTSIDE' 'a fixed point outside the sheet' | Out-Null
    Fail (Plan (Flat-File 'tray2.dxf' $tray) @{ directions = '["sideways"]' }) 'INVALID_PARAMETER' 'a direction that does not exist' | Out-Null
    # CLOSED corners: the y flaps are 2 mm wider than the plate, so they reach 2 mm beyond the line of the x bends (what Convert and closed-corner flanges draw)
    $closedPath = Flat-File 'closed.dxf' (ClosedCornerFlat)
    $e = Fail (Plan $closedPath) 'BEND_NOT_FOLDABLE' 'a flat with closed corners'
    Assert ($e.error.message -like '*2 mm beyond the line*' -and $e.error.message -like '*CLOSED corners*') $e.error.message
    $g = Rg (Ok (Plan $closedPath @{ allow_unfoldable = $true }) 'allow_unfoldable plans it anyway'); Assert (($g.warnings -join ' ') -like '*not cleanly foldable*') 'allow_unfoldable keeps the finding as a warning'
}

Test 'FF02 PLATE + TAB: a continuous bend line that ends on two outline corners is a bend (not an outline); the L bracket it folds into has the closed-form size, its flat is the DXF outline' {
    $tab = @(Dxf-Polygon @(@(0, 0), @(60, 0), @(60, 10), @(90, 10), @(90, 30), @(60, 30), @(60, 40), @(0, 40))) + @(Dxf-Line 60 10 60 30 'Continuous')
    $p = Flat-File 'tab.dxf' $tab
    $g = Rg (Ok (Plan $p) 'plan'); Assert ($g.bends.Count -eq 1 -and $g.regions.Count -eq 2 -and $g.outline.pieces -eq 8) "bends $($g.bends.Count) regions $($g.regions.Count) outline $($g.outline.pieces)"
    $b = $g.bends[0]; Assert ($b.linetype -eq 'continuous' -and $b.depth -eq 1 -and $b.direction -eq 'up') "bend: $($b | ConvertTo-Json -Compress)"
    Fresh-Part
    $rep = From-Flat $p; $res = Rg (Ok $rep 'from flat')
    Assert ($res.verified -eq $true -and -not $res.problems) "problems: $($res.problems -join ' | ')"
    Assert ($res.bends -eq 1 -and $res.bends_expected -eq 1) "bends $($res.bends) / $($res.bends_expected)"
    Assert ($res.flat_deviation -lt 1e-4) "flat deviation $($res.flat_deviation)"
    # closed form: the plate 60 mm long ends at the bend line; its straight part is 60 - BA/2 long, the tab (30 mm beyond the line) stands 30 - BA/2 + r above the plate
    $bam = $BA * 1000; $x = Extent 0; $y = Extent 1; $z = Extent 2
    Near (Mm $x[0]) 0 0.01 'x min = the free end of the plate'; Near (Mm $x[1]) (60 - $bam / 2 + 3 + 1.5) 0.01 'x max = the outside of the tab'
    Near (Mm $y[0]) 0 0.01 'y min'; Near (Mm $y[1]) 40 0.01 'y max'
    Near (Mm $z[0]) -1.5 0.01 'z min = the bottom of the plate'; Near (Mm $z[1]) (3 + 30 - $bam / 2) 0.01 'z max = the free end of the tab: r + L - BA/2'
}

Test 'FF03 the RELIEVED TRAY: four walls fold up at once from one sketch; the outer width = Wp - BA + 2 (r + t), Z = r + L - BA/2; the flat pattern is the DXF outline and its DXF export IS the input DXF' {
    $ents = FlatTray; $p = Flat-File 'tray.dxf' $ents
    Fresh-Part
    $res = Rg (Ok (From-Flat $p) 'from flat'); Assert ($res.verified -eq $true -and -not $res.problems) "problems: $($res.problems -join ' | ')"
    Assert ($res.bends -eq 4) "bends $($res.bends)"; Assert (@($res.steps).Count -eq 2) "steps: $($res.steps -join ' | ')"
    Assert ($res.flat_deviation -lt 1e-4) "flat deviation $($res.flat_deviation)"
    $bam = $BA * 1000; $x = Extent 0; $y = Extent 1; $z = Extent 2
    Near (Mm $x[1]) ((100 - $bam) / 2 + 4.5) 0.01 'x outer / 2 = (Wp - BA) / 2 + r + t'; Near (Mm $x[0]) (-((100 - $bam) / 2 + 4.5)) 0.01 '-x outer / 2'
    Near (Mm $y[1]) ((60 - $bam) / 2 + 4.5) 0.01 'y outer / 2 = (Hp - BA) / 2 + r + t'; Near (Mm $y[0]) (-((60 - $bam) / 2 + 4.5)) 0.01 '-y outer / 2'
    Near (Mm $z[0]) -1.5 0.01 'z min'; Near (Mm $z[1]) (3 + 25 - $bam / 2) 0.01 'z max = r + L - BA/2'
    # closure: the flat DXF of the BUILT part is the DXF it was built from
    $path = Join-Path $tmp 'tray_built.SLDPRT'; Remove-Item $path -ErrorAction SilentlyContinue
    Ok (Invoke-Tool 'save_document' @{ file_path = $path }) 'save the part' | Out-Null
    $out = Join-Path $tmp 'tray_exported.dxf'; Remove-Item $out -ErrorAction SilentlyContinue
    $fl = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = $out; dxf_flags = 5 }) 'export the flat'); Assert ($fl.dxf.written -eq $true) 'the DXF was written'
    $a = Dxf-Keys (Join-Path $tmp 'tray.dxf'); $b = Dxf-Keys $out
    $onlyIn = @($a.outline | Where-Object { $b.outline -notcontains $_ }); $onlyOut = @($b.outline | Where-Object { $a.outline -notcontains $_ })
    Assert ($onlyIn.Count -eq 0 -and $onlyOut.Count -eq 0) "outline only in the input: $($onlyIn -join ' | ') ;; only in the export: $($onlyOut -join ' | ')"
    Assert ($b.bends.Count -eq 4) "the export has $($b.bends.Count) bend lines"
}

Test 'FF04 ARCS and a HOLE: rounded flap corners (8 arcs) and a round hole survive the trip - the flat pattern has EXACTLY the area of the DXF outline (volume = area x t), the folded size is the closed form' {
    $ents = FlatTray -rc 3 -hole 8; $p = Flat-File 'tray_round.dxf' $ents
    $g = Rg (Ok (Plan $p) 'plan'); Assert ($g.outline.holes -eq 1 -and $g.outline.kinds.arc -eq 8) "outline: $($g.outline | ConvertTo-Json -Compress -Depth 4)"
    Fresh-Part
    $res = Rg (Ok (From-Flat $p) 'from flat'); Assert ($res.verified -eq $true -and -not $res.problems) "problems: $($res.problems -join ' | ')"
    $bam = $BA * 1000; $x = Extent 0; Near (Mm $x[1]) ((100 - $bam) / 2 + 4.5) 0.01 'x outer / 2'
    Ok (Invoke-Tool 'flat_pattern_control' @{ leave_flat = $true }) 'leave the part flat' | Out-Null
    $vol = (Volume) * 1e9; $want = (FlatTrayArea -rc 3 -hole 8) * 1.5
    Assert ([math]::Abs($vol - $want) / $want -lt 2e-4) "flat volume $([math]::Round($vol, 3)) mm3, area x t = $([math]::Round($want, 3)) mm3"
}

Test 'FF05 a C-CHANNEL: wall and lip bends (depth 1 and 2, both hidden = both up): the lips turn IN - the outermost bends are folded first, the sizes are the closed form' {
    $p = Flat-File 'channel.dxf' (FlatChannel -Wp 80 -Hp 50 -L1 30 -L2 12 -lipLine 'HIDDEN')
    $g = Rg (Ok (Plan $p) 'plan'); Assert ($g.bends.Count -eq 4 -and $g.regions.Count -eq 5) "bends $($g.bends.Count) regions $($g.regions.Count)"
    $depths = ($g.bends | ForEach-Object { $_.depth } | Sort-Object) -join ','; Assert ($depths -eq '1,1,2,2') "depths $depths"
    $order = @($g.build_order | ForEach-Object { $_.depth }); Assert (($order -join ',') -eq '2,1') "the outermost bends are folded first: build order $($order -join ',')"
    Fresh-Part
    $res = Rg (Ok (From-Flat $p) 'from flat'); Assert ($res.verified -eq $true -and -not $res.problems) "problems: $($res.problems -join ' | ')"
    Assert ($res.bends -eq 4) "bends $($res.bends)"
    $bam = $BA * 1000; $xend = 40 - $bam / 2; $x = Extent 0; $y = Extent 1; $z = Extent 2
    Near (Mm $x[1]) ($xend + 3 + 1.5) 0.01 'x max = the outside of the wall (the lip turns IN)'; Near (Mm $x[0]) (-($xend + 3 + 1.5)) 0.01 'x min'
    Near (Mm $y[1]) 25 0.01 'y max'; Near (Mm $y[0]) -25 0.01 'y min'
    Near (Mm $z[0]) -1.5 0.01 'z min'; Near (Mm $z[1]) (2 * 3 + 1.5 + 30 - $bam) 0.01 'z max = 2 r + t + L1 - BA'
}

Test 'FF06 a HAT: the lip bends are drawn CONTINUOUS (the other direction) - the lips turn OUT; the same hat from an all-hidden DXF plus directions = [up, down] is the same part' {
    $dat = FlatChannel -Wp 80 -Hp 50 -L1 30 -L2 12 -lipLine 'Continuous'; $p = Flat-File 'hat.dxf' $dat
    $g = Rg (Ok (Plan $p) 'plan'); $dirs = ($g.bends | Sort-Object depth | ForEach-Object { $_.direction }) -join ','; Assert ($dirs -eq 'up,up,down,down') "directions by linetype: $dirs"
    Assert (-not (($g.warnings -join ' ') -like '*one linetype*')) 'two linetypes say the directions: no warning'
    $bam = $BA * 1000; $hidden = FlatChannel -Wp 80 -Hp 50 -L1 30 -L2 12 -lipLine 'HIDDEN'
    foreach ($case in @(@{ file = $p; more = @{} }, @{ file = (Flat-File 'hat_hidden.dxf' $hidden); more = @{ directions = '["up","down"]' } })) {
        Fresh-Part
        $res = Rg (Ok (From-Flat $case.file $case.more) 'from flat'); Assert ($res.verified -eq $true -and -not $res.problems) "problems: $($res.problems -join ' | ')"
        $x = Extent 0; $z = Extent 2
        # the lip turns OUT: its tip is the outermost face, x = (Wp/2 - BA/2) + 2 r + t + L2 - BA/2
        Near (Mm $x[1]) (40 + 2 * 3 + 1.5 + 12 - $bam) 0.01 'x max = the tip of the outward lip'; Near (Mm $x[0]) (-(40 + 2 * 3 + 1.5 + 12 - $bam)) 0.01 'x min'
        Near (Mm $z[1]) (2 * 3 + 1.5 + 30 - $bam) 0.01 'z max = 2 r + t + L1 - BA (as the C channel: the lip sits on top of the wall either way)'
    }
}

Test 'FF07 refusals while building: a part that is not empty, no thickness / radius, a flat with closed corners - nothing is created' {
    $tray = FlatTray; $p = Flat-File 'tray.dxf' $tray
    Tray    # a part with a body in it
    Fail (From-Flat $p) 'NOT_EMPTY_PART' 'a part with a body' | Out-Null
    Fresh-Part; $v0 = StateVer
    Fail (Invoke-Tool 'sheet_metal_from_flat' @{ dxf_path = $p; bend_radius = $r }) 'MISSING_PARAMETER' 'no thickness' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_from_flat' @{ dxf_path = $p; thickness = $t }) 'MISSING_PARAMETER' 'no bend radius' | Out-Null
    $closed = Flat-File 'closed.dxf' (ClosedCornerFlat)
    Fail (From-Flat $closed) 'BEND_NOT_FOLDABLE' 'a closed-corner flat' | Out-Null
    Assert ((StateVer) -eq $v0) 'a refused build leaves the state version alone'
    $names = @(FeatureNames | Where-Object { $_ -match '\[(ProfileFeature|SheetMetal|BaseFlange|SM3dBend)\]' }); Assert ($names.Count -eq 0) "nothing may be created: $($names -join ', ')"
}

Test 'FF08 a build SolidWorks refuses half way is TAKEN BACK: a bend radius far too big for the tray fails a step, the part is empty again, and keep_on_failure keeps the half-built part for a look' {
    $tray = FlatTray; $p = Flat-File 'tray.dxf' $tray
    Fresh-Part; $before = @(FeatureNames).Count
    $e = Invoke-Tool 'sheet_metal_from_flat' @{ dxf_path = $p; thickness = $t; bend_radius = 0.2; k_factor = $K }
    Assert ($e.status -eq 'FAILED' -and $e.error.code -eq 'BUILD_STEP_FAILED') "expected BUILD_STEP_FAILED, got $($e.status) $($e.error.code) $($e.error.message)"
    Assert ($e.error.message -like '*rolled back*') "the message says the build was taken back: $($e.error.message)"
    $after = @(FeatureNames); $stray = @($after | Where-Object { $_ -match '\[(ProfileFeature|SheetMetal|BaseFlange|SM3dBend|FlatPattern)\]' }); Assert ($stray.Count -eq 0) "the part must be empty again: $($stray -join ', ')"
    Assert ($after.Count -eq $before) "the feature tree is as it was: $before features before, $($after.Count) after"
    $k = Invoke-Tool 'sheet_metal_from_flat' @{ dxf_path = $p; thickness = $t; bend_radius = 0.2; k_factor = $K; keep_on_failure = $true }
    Assert ($k.status -eq 'FAILED' -and $k.error.message -notlike '*rolled back:*') "keep_on_failure keeps the features: $($k.error.message)"
    $kept = @(FeatureNames | Where-Object { $_ -match '\[(ProfileFeature|SheetMetal|BaseFlange)\]' }); Assert ($kept.Count -gt 0) 'the half-built part is still there'
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
""; $fail =@($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail
