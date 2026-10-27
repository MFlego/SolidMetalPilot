# Regression tests for the typed-control tools (feature_data, sheet_metal_params, sheet_metal_corners, flat_pattern_control) against the IN-PROCESS add-in.
# Needs SolidWorks running with the bridge, NOTHING else open in it (the suite builds a tray with the SolidPilot tools and closes every document at the end), and the tools loaded (deploy-bridge.ps1 -ToolsOnly).
# Beyond "the value reads back" the tests check the GEOMETRY: a K-factor change must move the flat blank by exactly (pi/2) * dK * t per bend, a thicker sheet must give a thicker blank, a longer flange a taller part.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File typed-control-tests.ps1 [-Soak 15] [-Force] [-Json out.json]
param([int]$Soak = 15, [switch]$Force, [string]$Json = '')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\probes\_common.ps1"
. "$PSScriptRoot\..\probes\_tools.ps1"
$script:results = New-Object System.Collections.ArrayList
$script:tmpDirs = @()
function Test([string]$Name, [scriptblock]$Body) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $true; $why = ''
    try { & $Body } catch { $ok = $false; $why = $_.Exception.Message }
    "{0}  {1,-78} {2,6} ms  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.ElapsedMilliseconds, $why
    [void]$script:results.Add([pscustomobject]@{ name = $Name; pass = $ok; ms = $sw.ElapsedMilliseconds; why = $why })
}
function Assert($cond, [string]$msg) { if (-not $cond) { throw $msg } }
function Near([double]$a, [double]$b, [double]$tol, [string]$what) { if ([math]::Abs($a - $b) -gt $tol) { throw "$what : $a vs $b (tolerance $tol)" } }
function StateVer { [int](Invoke-RestMethod "$script:ToolBase/api/tool/state" -TimeoutSec 30).stateVersion }
function Rg($reply) { $reply.result_geometry }
function Ok($reply, [string]$what) { Assert ($reply.status -eq 'COMPLETED') "$what : $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Fail($reply, [string]$code, [string]$what) { Assert ($reply.status -eq 'FAILED' -and $reply.error.code -eq $code) "$what : expected FAILED/$code, got $($reply.status) $($reply.error.code) $($reply.error.message)"; $reply }
function Mm([double]$m) { [math]::Round($m * 1000, 4) }
function Flat { $o = Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flat_pattern_control'; Rg $o }           # folded + flat blank (the flat pattern is suppressed again afterwards)
function Params { Rg (Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'get' }) 'sheet_metal_params get') }
function Sw { Get-Process SLDWORKS | Select-Object -First 1 }

$ad = Invoke-Bridge 'activeDoc' 5000
if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe: is SolidWorks running with the add-in loaded?' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open in SolidWorks (activeDoc = $ad). The suite closes every document at the end: close them first or pass -Force." }
$h0 = (Sw).HandleCount; $m0 = [int]((Sw).PrivateMemorySize64 / 1MB)
"== typed-control tools  ($script:ToolBase)"

# ---- fixture: the SolidPilot way (open part, sketch, base flange, 4 edge flanges)
$script:fixtureOk = $false
Test 'T00 build the tray (base flange + four edge flanges, open corners)' {
    $out = & "$PSScriptRoot\..\probes\sm_fixture.ps1" 2>&1 | Out-String
    Assert ($out -match 'Edge-Flange4\[EdgeFlange\]') "fixture did not finish: $out"
    $script:fixtureOk = $true
}
if (-not $script:fixtureOk) { "fixture failed - the remaining tests need it"; exit 1 }
$script:base = $null

Test 'T01 feature_data list: sheet-metal features with their definition interfaces; folders only with include_all' {
    $r = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')
    $names = @($r.features | ForEach-Object { $_.name })
    foreach ($n in 'Sheet-Metal1', 'Base-Flange1', 'Edge-Flange1', 'Edge-Flange4', 'Flat-Pattern1') { Assert ($names -contains $n) "missing $n in $($names -join ',')" }
    Assert (-not ($names -contains 'Comments')) 'folders must be hidden by default'
    $ef = $r.features | Where-Object { $_.name -eq 'Edge-Flange1' }; Assert ($ef.definition -eq 'IEdgeFlangeFeatureData') "definition $($ef.definition)"
    Assert (@($ef.sub_features) -contains 'EdgeBend1 [OneBend]') 'sub-feature EdgeBend1 not listed'
    $all = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list'; include_all = $true }) 'list all'); Assert ($all.feature_count -gt $r.feature_count) 'include_all did not add the folders'
}

Test 'T02 sheet_metal_params get: the whole sheet-metal state in one call' {
    $p = Params; $script:base = $p
    Assert ($p.sheet_metal -eq $true) 'sheet_metal flag'; Near $p.global.thickness 0.0015 1e-9 'thickness'; Near $p.global.bend_radius 0.0015 1e-9 'radius'
    Assert (@($p.edge_flanges).Count -eq 4 -and @($p.bends).Count -eq 4) 'expected 4 flanges and 4 bends'; Near $p.edge_flanges[0].length 0.025 1e-9 'flange length'
    Assert ($p.global.overrides.bend_allowance -eq $false) 'no override expected on a fresh part'; Assert ($p.flat_pattern.suppressed -eq $true) 'flat pattern is suppressed by default'
    $script:flat0 = Flat; Near $script:flat0.folded.size[2] 0.0265 5e-5 'folded height (25 + 1.5 mm)'; Near $script:flat0.flat.size[2] 0.0015 1e-6 'flat blank thickness'
    Assert ($script:flat0.flat_pattern_left -eq 'suppressed') 'the flat pattern must be left suppressed'
}

Test 'T03 feature_data get: every property with its access, per feature type' {
    $e = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'get'; feature = 'Edge-Flange1' }) 'get flange')
    Assert ($e.definition -eq 'IEdgeFlangeFeatureData') "iface $($e.definition)"; Assert ($e.properties.OffsetDistance.access -eq 'rw') 'OffsetDistance must be rw'; Near $e.properties.OffsetDistance.value 0.025 1e-9 'OffsetDistance'
    $b = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'get'; feature = 'EdgeBend1' }) 'get bend'); Assert ($b.definition -eq 'IOneBendFeatureData') "bend iface $($b.definition)"; Assert ($null -ne $b.properties.KFactor) 'KFactor missing on the bend'
    $ro = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'get'; feature = 'Edge-Flange1'; include_readonly = $false }) 'get rw only'); Assert (-not ($ro.properties.PSObject.Properties | Where-Object { $_.Value.access -eq 'ro' })) 'include_readonly=false still returned read-only properties'
}

Test 'T04 feature_data set: a longer flange makes a taller part, and the write is verified (applied)' {
    $StateVer = StateVer; $r = Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = @{ OffsetDistance = 0.035 } }) 'set flange'
    $g = Rg $r; Assert ($g.applied -contains 'OffsetDistance' -and $g.verified -eq $true -and $g.feature_error -eq 0) "report: $($g | ConvertTo-Json -Compress -Depth 4)"; Assert ((StateVer) -eq $StateVer + 1) 'state_version must rise by 1 on a set'
    Near (Flat).folded.size[2] 0.0365 5e-5 'folded height with a 35 mm flange'
    Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = @{ OffsetDistance = 0.025 } }) 'restore' | Out-Null; Near (Flat).folded.size[2] 0.0265 5e-5 'folded height restored'
}

Test 'T05 write-verify catches a value SolidWorks silently ignores (global K without the override)' {
    $r = Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Sheet-Metal1'; properties = @{ KFactor = 0.33 } }) 'plain K write'
    $g = Rg $r; Assert ($g.verified -eq $false) 'the ignored write must NOT be reported as verified'; Assert (@($g.ignored | ForEach-Object { $_.property }) -contains 'KFactor') "KFactor not in ignored: $($g | ConvertTo-Json -Compress -Depth 4)"
    Near $g.ignored[0].actual 0.5 1e-9 'the value SolidWorks kept'; Assert ($g.note -like '*OVERRIDE*') 'the note must name the missing precondition'
}

Test 'T06 NO_VALID_CHANGES: unknown property, wrong JSON type, read-only; nothing is rebuilt and state_version stays' {
    $StateVer = StateVer
    Fail (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = @{ Bogus = 1 } }) 'NO_VALID_CHANGES' 'unknown' | Out-Null
    $r = Fail (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = @{ OffsetDistance = 'long' } }) 'NO_VALID_CHANGES' 'wrong type'; Assert ($r.error.message -like '*expects a number*') $r.error.message
    Fail (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = @{ OffsetDistance = $true } }) 'NO_VALID_CHANGES' 'bool for a number' | Out-Null
    Assert ((StateVer) -eq $StateVer) 'failed sets must not change state_version'
}

Test 'T07 feature_data errors: missing feature, unknown feature, no definition, bad action, empty set, malformed JSON string' {
    Fail (Invoke-Tool 'feature_data' @{ action = 'get' }) 'MISSING_PARAMETER' 'no feature' | Out-Null
    Fail (Invoke-Tool 'feature_data' @{ action = 'get'; feature = 'Nope1' }) 'FEATURE_NOT_FOUND' 'unknown' | Out-Null
    Fail (Invoke-Tool 'feature_data' @{ action = 'get'; feature = 'Sketch1' }) 'NO_DEFINITION' 'sketch' | Out-Null
    Fail (Invoke-Tool 'feature_data' @{ action = 'explode'; feature = 'Edge-Flange1' }) 'INVALID_PARAMETER' 'bad action' | Out-Null
    Fail (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1' }) 'MISSING_PARAMETER' 'empty set' | Out-Null
    Fail (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = '{not json' }) 'INVALID_PARAMETER' 'malformed JSON string' | Out-Null
    $s = Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = '{"OffsetDistance":0.025}' }) 'a JSON-encoded string is accepted too'; Assert ((Rg $s).verified -eq $true) 'string-encoded properties'
}

Test 'T08 sheet_metal_params k_factor: the override sequence takes effect and the BLANK MOVES by (pi/2)*dK*t per bend' {
    $r = Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; k_factor = 0.42 }) 'set K'; $g = Rg $r
    Assert ($g.verified -eq $true) "problems: $($g.problems -join ';')"; Near $g.global.bend_allowance.k_factor 0.42 1e-9 'K read back'
    Assert ($g.global.dimensions -like '*D2@Sheet-Metal1*=0.42*') "the K dimension did not appear: [$($g.global.dimensions)]"; Assert ($g.global.overrides.bend_allowance -eq $true) 'override flag'
    Assert (@($g.bends | Where-Object { $_.k_factor -ne 0.42 }).Count -eq 0) 'all four default bends must follow the global K'
    $f = Flat; $dx = $script:flat0.flat.size[0] - $f.flat.size[0]; $expect = [math]::PI / 2 * 0.08 * 0.0015 * 2      # two bends along x
    Near $dx $expect 2e-5 'flat blank length change (x) for K 0.50 -> 0.42'
}

Test 'T09 once the override is on, the plain KFactor property works too; reset_overrides returns to the defaults and the blank is restored' {
    $r = Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Sheet-Metal1'; properties = @{ KFactor = 0.33 } }) 'K 0.33'; Assert ((Rg $r).verified -eq $true) 'with the override on the plain write must take effect'
    $z = Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; reset_overrides = 'bend_allowance,auto_relief' }) 'reset'; $g = Rg $z
    Assert ($g.verified -eq $true -and $g.global.overrides.bend_allowance -eq $false) "reset: $($g.problems -join ';')"; Assert ($g.global.dimensions -eq '') 'D2@Sheet-Metal1 must disappear with the override'; Near $g.global.bend_allowance.k_factor 0.5 1e-9 'K back to 0.5'
    $f = Flat; Near $f.flat.size[0] $script:flat0.flat.size[0] 1e-7 'flat x restored'; Near $f.flat.size[1] $script:flat0.flat.size[1] 1e-7 'flat y restored'
}

Test 'T10 generic route: calls + custom_bend_allowance through feature_data reach the same global K' {
    $r = Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Sheet-Metal1'; calls = @(@{ method = 'SetOverrideDefaultParameter2'; args = @(1, $true) }); custom_bend_allowance = @{ type = 'k_factor'; k_factor = 0.41 } }) 'calls + cba'
    $p = Params; Near $p.global.bend_allowance.k_factor 0.41 1e-9 'K via the generic route'; Assert ($p.global.dimensions -like '*D2@Sheet-Metal1*') 'dimension D2 expected'
    Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; reset_overrides = @('bend_allowance') }) 'reset' | Out-Null
}

Test 'T11 per-bend K (OneBend definition): only that bend moves, the blank follows' {
    $r = Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'EdgeBend1'; properties = @{ UseDefaultBendAllowance = $false; BendAllowanceType = 2; KFactor = 0.42 } }) 'per-bend K'; $g = Rg $r
    Assert ($g.verified -eq $true) "report: $($g | ConvertTo-Json -Compress -Depth 4)"; $p = Params
    Near $p.bends[0].k_factor 0.42 1e-9 'EdgeBend1'; Assert (@($p.bends | Select-Object -Skip 1 | Where-Object { $_.k_factor -ne 0.5 }).Count -eq 0) 'the other bends must keep K 0.5'
    $f = Flat; Near ($script:flat0.flat.size[1] - $f.flat.size[1]) ([math]::PI / 2 * 0.08 * 0.0015) 2e-5 'flat blank change (y) for ONE bend at K 0.42'
    Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'EdgeBend1'; properties = @{ UseDefaultBendAllowance = $true } }) 'restore the bend' | Out-Null
    Near (Flat).flat.size[1] $script:flat0.flat.size[1] 1e-7 'flat y restored'
}

Test 'T12 sheet_metal_params thickness + bend_radius: a 2 mm sheet gives a 2 mm blank; restore' {
    $r = Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; thickness = 0.002; bend_radius = 0.002 }) 'set 2 / 2'; Assert ((Rg $r).verified -eq $true) "problems: $((Rg $r).problems -join ';')"
    $f = Flat; Near $f.flat.size[2] 0.002 1e-7 'flat blank thickness'; Assert ($f.flat.size[0] -lt $script:flat0.flat.size[0]) 'a bigger radius/thickness shortens the blank here (setback grows)'
    Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; thickness = 0.0015; bend_radius = 0.0015 }) 'restore' | Out-Null; $f2 = Flat; Near $f2.flat.size[2] 0.0015 1e-7 'thickness restored'; Near $f2.flat.size[0] $script:flat0.flat.size[0] 1e-6 'blank restored'
}

Test 'T13 sheet_metal_params auto relief: rectangular / tear keep the ratio, obround reports it as a note; reset' {
    foreach ($t in 'rectangular', 'tear') { $g = Rg (Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; auto_relief = $t; relief_ratio = 0.7 }) "relief $t"); Assert ($g.verified -eq $true) "$t : $($g.problems -join ';')"; Near $g.global.relief_ratio 0.7 1e-9 "$t ratio" }
    $o = Rg (Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; auto_relief = 'obround'; relief_ratio = 0.4 }) 'obround'); Assert ($o.global.auto_relief_type -eq 2 -and $o.verified -eq $true -and "$($o.notes)" -like '*obround*') "obround: $($o | ConvertTo-Json -Compress -Depth 4)"
    Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; reset_overrides = 'auto_relief' }) 'reset' | Out-Null; Assert ((Params).global.overrides.auto_relief -eq $false) 'auto_relief override still on'
}

Test 'T14 sheet_metal_params errors: nothing to set, bad relief, bad reset name' {
    Fail (Invoke-Tool 'sheet_metal_params' @{ action = 'set' }) 'MISSING_PARAMETER' 'empty' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; auto_relief = 'zigzag' }) 'INVALID_PARAMETER' 'relief' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; reset_overrides = 'colour' }) 'INVALID_PARAMETER' 'reset' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_params' @{ action = 'dance' }) 'INVALID_PARAMETER' 'action' | Out-Null
}

Test 'T15 sheet_metal_corners: four corners closed in ONE call from face indices, configured as asked' {
    $faces = @((Get-Faces).faces | Where-Object { $_.planar -and [math]::Abs($_.area - 3.5E-05) -lt 1E-06 })
    $pick = @($faces | Where-Object { $n = $_.normal; $p = $_.point; (([math]::Abs($n[0]) -gt 0.5 -and [math]::Abs($p[0]) -lt 0.049) -or ([math]::Abs($n[1]) -gt 0.5 -and [math]::Abs($p[1]) -lt 0.029)) })
    Assert ($pick.Count -eq 4) "expected 4 inner end faces, found $($pick.Count) of $($faces.Count) candidates"
    $StateVer = StateVer; $r = Ok (Invoke-Tool 'sheet_metal_corners' @{ faces = '[' + (($pick | ForEach-Object { $_.i }) -join ',') + ']'; corner_type = 'overlap'; gap = 0.0000762; overlap_ratio = 1.0 }) 'close corners'; $g = Rg $r
    Assert ($g.verified -eq $true -and @($g.created).Count -eq 4 -and @($g.failed).Count -eq 0) "result: $($g | ConvertTo-Json -Compress -Depth 5)"
    foreach ($c in $g.created) { Assert ($c.configured -eq $true -and $c.corner_type -eq 2 -and $c.feature_error -eq 0) "$($c.feature) not configured"; Near $c.gap 0.0000762 1e-9 'gap' }
    Assert ($g.closed_corners_before -eq 0 -and $g.closed_corners_after -eq 4) 'corner count'; Assert ((StateVer) -eq $StateVer + 1) 'state_version rises by one for the whole call'
    $p = Params; Assert (@($p.closed_corners).Count -eq 4) 'sheet_metal_params get must list the four corners'
}

Test 'T16 closed corners can be edited afterwards through feature_data (type butt and back to overlap)' {
    $g = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Closed Corner1'; properties = @{ CornerType = 1 } }) 'butt'); Assert ($g.verified -eq $true) 'butt not applied'
    Assert ((Params).closed_corners[0].corner_type -eq 1) 'read back butt'
    $g2 = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Closed Corner1'; properties = @{ CornerType = 2; GapDistance = 0.0000762 } }) 'overlap'); Assert ($g2.verified -eq $true) 'overlap not applied'
}

Test 'T17 sheet_metal_corners failures: bad index, a face that cannot close, a point off the part, no target, bad type; nothing created' {
    $StateVer = StateVer
    Fail (Invoke-Tool 'sheet_metal_corners' @{ faces = '[9999]' }) 'FACE_NOT_FOUND' 'index' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_corners' @{ faces = '[3]' }) 'NO_CORNER_CREATED' 'big face' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_corners' @{ points = '[[5,5,5]]' }) 'NO_CORNER_CREATED' 'point off the part' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_corners' @{}) 'MISSING_PARAMETER' 'no target' | Out-Null
    Fail (Invoke-Tool 'sheet_metal_corners' @{ faces = '[3]'; corner_type = 'weld' }) 'INVALID_PARAMETER' 'type' | Out-Null
    Assert (@((Params).closed_corners).Count -eq 4) 'the failed calls changed the corner count'; Assert ((StateVer) -eq $StateVer) 'failed calls must not change state_version'
}

Test 'T18 flat_pattern_control options: merge_faces=false splits the flat faces, true restores; the flat pattern is always re-suppressed' {
    $a = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ merge_faces = $false }) 'merge off'); Assert ($a.verified -eq $true -and $a.options.merge_faces -eq $false) 'merge off'; Assert ($a.flat.faces -gt 14) "flat faces $($a.flat.faces)"
    $b = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ merge_faces = $true; corner_treatment = $true }) 'merge on'); Assert ($b.options.merge_faces -eq $true -and $b.flat_pattern_left -eq 'suppressed') 'merge on'
    $c = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ leave_flat = $true }) 'leave flat'); Assert ($c.flat_pattern_left -eq 'active') 'leave_flat'; Assert ((Params).flat_pattern.suppressed -eq $false) 'the flat pattern should stay active'
    Ok (Invoke-Tool 'flat_pattern_control' @{}) 'suppress again' | Out-Null; Assert ((Params).flat_pattern.suppressed -eq $true) 'suppressed again'
}

Test 'T19 flat DXF: an unsaved part -> SAVE_REQUIRED (and the flat pattern is left suppressed); saved -> a real DXF, bend lines add content' {
    $dir = Join-Path $env:TEMP ('tc_' + [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory $dir | Out-Null; $script:tmpDirs += $dir     # removed at the end (the saved part is still open, hence locked)
    Fail (Invoke-Tool 'flat_pattern_control' @{ dxf_path = "$dir\x.dxf" }) 'SAVE_REQUIRED' 'unsaved' | Out-Null; Assert ((Params).flat_pattern.suppressed -eq $true) 'a failed export must not leave the flat pattern active'
    Ok (Invoke-Tool 'save_document' @{ file_path = "$dir\tray.SLDPRT" }) 'save' | Out-Null
    $o = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = "$dir\outline.dxf"; dxf_flags = 1 }) 'dxf outline'); $b = Rg (Ok (Invoke-Tool 'flat_pattern_control' @{ dxf_path = "$dir\bends.dxf"; dxf_flags = 5 }) 'dxf bends')
    Assert ($o.dxf.written -eq $true -and $b.dxf.written -eq $true) 'DXF not written'; Assert ($o.dxf.bytes -gt 2000) "outline DXF only $($o.dxf.bytes) bytes"; Assert ($b.dxf.bytes -gt $o.dxf.bytes) "bend lines must add content ($($o.dxf.bytes) vs $($b.dxf.bytes))"
    $head = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes("$dir\bends.dxf"), 0, 200); Assert ($head -match 'SECTION') 'not a DXF file'
    Assert ((Params).flat_pattern.suppressed -eq $true) 'flat pattern left active after the export'
}

Test 'T20 idempotency and state_version: a replayed set is DUPLICATE; reads ignore a stale version, writes refuse it' {
    $op = [guid]::NewGuid().ToString('N'); $StateVer = StateVer
    $body = { param($tool, $params, $version, $opid) @{ operationId = $opid; tool = $tool; stateVersion = $version; params = $params } | ConvertTo-Json -Depth 20 -Compress }
    $send = { param($json) (Invoke-RestMethod -Method Post -ContentType 'application/json' -Body $json "$script:ToolBase/api/tool/execute" -TimeoutSec 120) }
    $a = & $send (& $body 'feature_data' @{ action = 'set'; feature = 'Edge-Flange2'; properties = @{ OffsetDistance = 0.025 } } $StateVer $op); Assert ($a.status -eq 'COMPLETED') "first: $($a.status) $($a.error.message)"
    $b = & $send (& $body 'feature_data' @{ action = 'set'; feature = 'Edge-Flange2'; properties = @{ OffsetDistance = 0.025 } } (StateVer) $op); Assert ($b.status -eq 'DUPLICATE') "replay gave $($b.status)"
    $c = & $send (& $body 'feature_data' @{ action = 'get'; feature = 'Edge-Flange2' } 999999 ([guid]::NewGuid().ToString('N'))); Assert ($c.status -eq 'COMPLETED') "a read with a stale version gave $($c.status)"
    $d = & $send (& $body 'feature_data' @{ action = 'set'; feature = 'Edge-Flange2'; properties = @{ OffsetDistance = 0.025 } } 999999 ([guid]::NewGuid().ToString('N'))); Assert ($d.status -eq 'FAILED' -and $d.error.code -eq 'INVALID_STATE_VERSION') "a write with a stale version gave $($d.status)"
}

Test "T21 a plain (non sheet-metal) part: NOT_SHEET_METAL, and feature_data still reads and writes ANY feature (generic definition scan)" {
    Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null
    Fail (Invoke-Tool 'feature_data' @{ action = 'list' }) 'NO_ACTIVE_DOCUMENT' 'no document' | Out-Null
    Ok (Invoke-Tool 'open_new_part' @{}) 'new part' | Out-Null; Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch' | Out-Null
    $segs = @(@{ entity_type = 'line'; x1 = 0.0; y1 = 0.0; x2 = 0.1; y2 = 0.0 }, @{ entity_type = 'line'; x1 = 0.1; y1 = 0.0; x2 = 0.1; y2 = 0.06 }, @{ entity_type = 'line'; x1 = 0.1; y1 = 0.06; x2 = 0.0; y2 = 0.06 }, @{ entity_type = 'line'; x1 = 0.0; y1 = 0.06; x2 = 0.0; y2 = 0.0 })
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $segs }) 'rectangle' | Out-Null
    Ok (Invoke-Tool 'extrude_feature' @{ depth = 0.02; feature_type = 'boss'; angle = 360.0; axis_x1 = 0.0; axis_y1 = 0.0; axis_x2 = 0.0; axis_y2 = 0.001; path_sketch = ''; profiles = '[]'; reverse = $false; through = $false; up_to_face_index = -1; mid_plane = $false }) 'extrude' | Out-Null
    $ext = (Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'list' }) 'list')).features | Where-Object { $_.type -eq 'Extrusion' } | Select-Object -First 1; Assert ($null -ne $ext) 'no Extrusion in the list'; Assert ($null -eq $ext.definition) 'the static map must not know Extrusion (so the scan is what is tested)'
    $g = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'get'; feature = $ext.name }) 'get extrusion'); Assert ($g.definition -like 'IExtrudeFeatureData*') "scan found $($g.definition)"
    $vol0 = (Get-Analysis 'mass_properties'); $v0 = ($vol0 | ConvertTo-Json -Compress -Depth 4)
    $s = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = $ext.name; calls = @(@{ method = 'SetDepth'; args = @($true, 0.03) }) }) 'SetDepth 30 mm'); Assert ($s.calls[0].method -eq 'SetDepth') 'call not logged'
    $v1 = (Get-Analysis 'mass_properties' | ConvertTo-Json -Compress -Depth 4); Assert ($v0 -ne $v1) 'the extrusion depth change did not change the mass properties'
    Fail (Invoke-Tool 'sheet_metal_params' @{ action = 'get' }) 'NOT_SHEET_METAL' 'params' | Out-Null; Fail (Invoke-Tool 'flat_pattern_control' @{}) 'NOT_SHEET_METAL' 'flat' | Out-Null; Fail (Invoke-Tool 'sheet_metal_corners' @{ faces = '[0]' }) 'NOT_SHEET_METAL' 'corners' | Out-Null
}

Test 'T23 sheet_metal_feature base_flange honours k_factor (SolidWorks ignores IBaseFlangeFeatureData.KFactor): 0.42 -> the override and D2@Sheet-Metal1; the default 0.5 -> nothing to override' {
    foreach ($k in 0.42, 0.5) {
        Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null; Ok (Invoke-Tool 'open_new_part' @{}) 'new part' | Out-Null
        Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch' | Out-Null
        $segs = @(@{ entity_type = 'line'; x1 = -0.05; y1 = -0.03; x2 = 0.05; y2 = -0.03 }, @{ entity_type = 'line'; x1 = 0.05; y1 = -0.03; x2 = 0.05; y2 = 0.03 }, @{ entity_type = 'line'; x1 = 0.05; y1 = 0.03; x2 = -0.05; y2 = 0.03 }, @{ entity_type = 'line'; x1 = -0.05; y1 = 0.03; x2 = -0.05; y2 = -0.03 })
        Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $segs }) 'rectangle' | Out-Null
        Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.0015; bend_radius = 0.0015; k_factor = $k }) "base flange K $k" | Out-Null
        $g = (Params).global; Near $g.bend_allowance.k_factor $k 1e-9 "the part's K for k_factor=$k"
        if ($k -eq 0.42) { Assert ($g.overrides.bend_allowance -eq $true -and $g.dimensions -like '*D2@Sheet-Metal1*=0.42*') "override / dimension missing: $($g | ConvertTo-Json -Compress -Depth 4)" }
        else { Assert ($g.overrides.bend_allowance -eq $false) 'the template default must not create an override' }
    }
}

Test 'T24 sheet_metal_params get carries the cut list: the FLAT BLANK size without unsuppressing the flat pattern, laser cutting length, bends - and it follows the model' {
    Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null; & "$PSScriptRoot\..\probes\sm_fixture.ps1" | Out-Null
    $c = (Params).cut_list; Assert (@($c).Count -eq 1) "cut-list items: $(@($c).Count)"; $pp = $c[0].properties
    Near $pp.'Sheet Metal Thickness' 1.5 1e-6 'thickness (mm)'; Assert ($pp.Bends -eq 4) "Bends $($pp.Bends)"; Near $pp.'Bend Radius' 1.5 1e-6 'bend radius (mm)'; Assert ($pp.'Cutting Length-Outer' -gt 400 -and $pp.'Cutting Length-Inner' -eq 0) 'cutting lengths'
    $f = Flat; Near $pp.'Bounding Box Length' ($f.flat.size[0] * 1000) 0.02 'blank length vs the flat pattern (mm)'; Near $pp.'Bounding Box Width' ($f.flat.size[1] * 1000) 0.02 'blank width vs the flat pattern (mm)'
    Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange1'; properties = @{ OffsetDistance = 0.035 } }) 'longer flange' | Out-Null
    $p2 = (Params).cut_list[0].properties; Near $p2.'Bounding Box Width' ($pp.'Bounding Box Width' + 10) 0.02 'a 10 mm longer flange widens the blank by 10 mm'; Assert ($p2.'Cutting Length-Outer' -gt $pp.'Cutting Length-Outer' + 15) 'the cutting length must grow by about 2 x 10 mm'
    $f2 = Flat; Near $p2.'Bounding Box Width' ($f2.flat.size[1] * 1000) 0.02 'the cut list still equals the flat pattern after the edit'
    Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'set'; k_factor = 0.42 }) 'K 0.42' | Out-Null; $p3 = (Params).cut_list[0].properties; Near $p3.'Bend Allowance' 0.42 1e-9 'Bend Allowance property = K'; Assert ($p3.'Bounding Box Length' -lt $p2.'Bounding Box Length') 'a smaller K shortens the blank'
}

if ($Soak -gt 0) {
    Test "T22 soak: $Soak cycles of set / restore / get / flat on a fresh tray - no failure, no handle or memory growth" {
        Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null; & "$PSScriptRoot\..\probes\sm_fixture.ps1" | Out-Null
        $hA = (Sw).HandleCount; $mA = [int]((Sw).PrivateMemorySize64 / 1MB)
        1..$Soak | ForEach-Object {
            $i = $_; $len = if ($i % 2) { 0.03 } else { 0.025 }
            $g = Rg (Ok (Invoke-Tool 'feature_data' @{ action = 'set'; feature = 'Edge-Flange3'; properties = @{ OffsetDistance = $len } }) "cycle $i set"); Assert ($g.verified -eq $true) "cycle $i : $($g | ConvertTo-Json -Compress -Depth 3)"
            $p = Params; Near $p.edge_flanges[2].length $len 1e-9 "cycle $i read-back"; $f = Flat; Assert ($f.flat_pattern_left -eq 'suppressed') "cycle $i flat pattern state"
        }
        $hB = (Sw).HandleCount; $mB = [int]((Sw).PrivateMemorySize64 / 1MB); "      soak: handles $hA -> $hB, private MB $mA -> $mB"
        Assert (($hB - $hA) -lt 200) "handle growth $hA -> $hB"; Assert (($mB - $mA) -lt 250) "memory growth $mA -> $mB MB"
    }
}

Invoke-Tool 'close_document' @{ save = $false; close_all = $true } | Out-Null
foreach ($d in $script:tmpDirs) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
"" ; $fail = @($script:results | Where-Object { -not $_.pass }).Count
$h1 = (Sw).HandleCount; $m1 = [int]((Sw).PrivateMemorySize64 / 1MB)
"{0} tests, {1} passed, {2} failed.   SolidWorks handles {3} -> {4}, private MB {5} -> {6}" -f $script:results.Count, ($script:results.Count - $fail), $fail, $h0, $h1, $m0, $m1
if ($Json) { $script:results | ConvertTo-Json | Set-Content $Json }
exit $fail

