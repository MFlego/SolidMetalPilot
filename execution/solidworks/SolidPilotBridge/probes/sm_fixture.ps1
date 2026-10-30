# Builds a small sheet-metal fixture through the SolidPilot tools (the same ones an agent uses): a 100 x 60 mm base flange (1.5 mm, R1.5, K 0.5 = the template default) with four 25 mm edge flanges on its perimeter,
# i.e. an open-corner tray. Prints each step. The fixture stays OPEN and ACTIVE in SolidWorks for the typed probes (run them with the bridge: run_cs).
# Usage: powershell -NoProfile -File sm_fixture.ps1 [-Flanges 4]
param([int]$Flanges = 4)
. "$PSScriptRoot\_tools.ps1"
function Step($name, $o) { "{0,-26} {1}{2}" -f $name, $o.status, $(if ($o.error) { "  $($o.error.code): $($o.error.message)" } else { '' }) }
Step 'open_new_part' (Invoke-Tool open_new_part)
Step 'create_sketch' (Invoke-Tool create_sketch @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 })
$w = 0.05; $h = 0.03
$segs = @(@{ entity_type = 'line'; x1 = -$w; y1 = -$h; x2 = $w; y2 = -$h }, @{ entity_type = 'line'; x1 = $w; y1 = -$h; x2 = $w; y2 = $h }, @{ entity_type = 'line'; x1 = $w; y1 = $h; x2 = -$w; y2 = $h }, @{ entity_type = 'line'; x1 = -$w; y1 = $h; x2 = -$w; y2 = -$h })
Step 'add_sketch_entities' (Invoke-Tool add_sketch_entities @{ segments = $segs })
$bf = Invoke-Tool sheet_metal_feature @{ feature_type = 'base_flange'; thickness = 0.0015; bend_radius = 0.0015; k_factor = 0.5 }; Step 'base_flange' $bf
"edges after the base flange: $((Get-Edges).edge_count)"
# the four top-face (z = 0) perimeter edges, found by position each time (every flange renumbers the edges)
$targets = @(@(0, $h, 0), @($w, 0, 0), @(0, -$h, 0), @(-$w, 0, 0))
foreach ($t in $targets | Select-Object -First $Flanges) {
    # the perimeter edge on the z = 0 face nearest to the target; neighbouring flanges shorten it a little (corner relief), so its midpoint drifts by a few mm
    $edge = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2] - $t[2]) -lt 1e-4 } | Sort-Object { [math]::Abs($_.mid[0] - $t[0]) + [math]::Abs($_.mid[1] - $t[1]) } | Select-Object -First 1
    if ($edge -and ([math]::Abs($edge.mid[0] - $t[0]) + [math]::Abs($edge.mid[1] - $t[1])) -gt 0.006) { $edge = $null }
    if (-not $edge) { "no edge found at ($($t -join ', '))"; continue }
    $r = Invoke-Tool sheet_metal_feature @{ feature_type = 'edge_flange'; edge_index = [int]$edge.i; flange_length = 0.025; dimension_type = 'inner'; angle = 90; bend_radius = 0.0015; thickness = 0.0015 }
    Step ("edge_flange @(" + ($t -join ',') + ")") $r
}
$g = Get-Analysis 'geometry'; "geometry: $($g -join ' ')"
$f = Invoke-Tool analyze_model @{ analysis_type = 'features'; name = ''; from_feature = ''; to_feature = ''; near = ''; k = 0; axis = '' }
$tree = ($f.cadState.features[0] | ConvertFrom-Json); "feature tree: " + (($tree.features | ForEach-Object { "$($_.name)[$($_.type)]" }) -join '  ')
