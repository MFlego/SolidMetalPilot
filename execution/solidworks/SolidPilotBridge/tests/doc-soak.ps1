# Document open / build / close soak: does a long agent session leak SolidWorks resources? Each cycle: new part, sketch, base flange, two edge flanges, a hem, flat pattern read, close without saving.
# Samples the SolidWorks process every few cycles: handles, private MB, GDI objects, USER objects (the ones that make a long-running SolidWorks fall over), and prints the growth PER CYCLE.
# Needs SolidWorks running with the bridge and NOTHING else open (it closes every document each cycle). Usage: doc-soak.ps1 [-Cycles 30] [-SampleEvery 5] [-Force] [-NoSnapshots]
# FINDING (A/B/A, 2026-10-27): with the automatic snapshots on, every build cycle of a NEW document leaves ~16 handles in SolidWorks (17.1 / 15.5 per cycle); with -NoSnapshots 0.9 / 0.5. The cost is the
# SaveAs3 copy itself. Memory, GDI and USER objects stay flat either way. Raw SolidWorks API cycles (probes 44 / 46) and HTTP / read-only tool calls leak nothing.
param([int]$Cycles = 30, [int]$SampleEvery = 5, [switch]$Force, [switch]$NoSnapshots)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\probes\_common.ps1"
. "$PSScriptRoot\..\probes\_tools.ps1"
Add-Type -Namespace Native -Name Gui -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetGuiResources(System.IntPtr hProcess, uint flags);'
function Sample { $p = Get-Process SLDWORKS | Select-Object -First 1; [pscustomobject]@{ handles = $p.HandleCount; mb = [int]($p.PrivateMemorySize64 / 1MB); gdi = [Native.Gui]::GetGuiResources($p.Handle, 0); user = [Native.Gui]::GetGuiResources($p.Handle, 1); threads = $p.Threads.Count } }
function Ok($r, $what) { if ($r.status -ne 'COMPLETED') { throw "$what : $($r.status) $($r.error.code) $($r.error.message)" }; $r }
$ad = Invoke-Bridge 'activeDoc' 5000; if ($null -eq $ad) { throw 'The SolidPilot Bridge does not answer on its pipe.' }
if (-not $Force -and $ad -ne 'none') { throw "A document is open (activeDoc = $ad): close it first or pass -Force." }
$segs = @(@{ entity_type = 'line'; x1 = -0.05; y1 = -0.03; x2 = 0.05; y2 = -0.03 }, @{ entity_type = 'line'; x1 = 0.05; y1 = -0.03; x2 = 0.05; y2 = 0.03 }, @{ entity_type = 'line'; x1 = 0.05; y1 = 0.03; x2 = -0.05; y2 = 0.03 }, @{ entity_type = 'line'; x1 = -0.05; y1 = 0.03; x2 = -0.05; y2 = -0.03 })
function Cycle {
    Ok (Invoke-Tool 'open_new_part' @{}) 'open' | Out-Null; Ok (Invoke-Tool 'create_sketch' @{ plane = 'Front Plane'; on_face = $false; face_x = 0; face_y = 0; face_z = 0 }) 'sketch' | Out-Null
    Ok (Invoke-Tool 'add_sketch_entities' @{ segments = $segs }) 'rect' | Out-Null; Ok (Invoke-Tool 'sheet_metal_feature' @{ feature_type = 'base_flange'; thickness = 0.0015; bend_radius = 0.0015; k_factor = 0.5 }) 'base' | Out-Null
    $e = (Get-Edges).edges | Where-Object { [math]::Abs($_.mid[2]) -lt 1e-6 } | Sort-Object { [math]::Atan2($_.mid[1], $_.mid[0]) }
    Ok (Invoke-Tool 'sheet_metal_miter_flange' @{ edges = "[$((@($e | ForEach-Object { $_.i })) -join ',')]"; height = 0.02 }) 'miter' | Out-Null
    $x = Ok (Invoke-Tool 'sheet_metal_params' @{ action = 'get' }) 'params'; Ok (Invoke-Tool 'flat_pattern_control' @{}) 'flat' | Out-Null
    Ok (Invoke-Tool 'close_document' @{ save = $false; close_all = $true }) 'close' | Out-Null
}
$prevPolicy = $null
if ($NoSnapshots) { $j = Invoke-Bridge 'snapshots' 5000; if ($j -match '"intervalSeconds":(\d+)') { $prevPolicy = [int]$Matches[1] }; [void](Invoke-Bridge 'snapshot_policy off' 5000); 'snapshots OFF for this run (restored at the end)' }
$s0 = Sample; $rows = @(); $t = [Diagnostics.Stopwatch]::StartNew()
"start: handles $($s0.handles), $($s0.mb) MB, GDI $($s0.gdi), USER $($s0.user), threads $($s0.threads)"
try {
    for ($i = 1; $i -le $Cycles; $i++) {
        Cycle
        if ($i % $SampleEvery -eq 0 -or $i -eq $Cycles) { $s = Sample; $rows += [pscustomobject]@{ cycle = $i; handles = $s.handles; mb = $s.mb; gdi = $s.gdi; user = $s.user; threads = $s.threads }; "cycle {0,3}: handles {1}  {2} MB  GDI {3}  USER {4}  threads {5}   ({6:N0} s)" -f $i, $s.handles, $s.mb, $s.gdi, $s.user, $s.threads, $t.Elapsed.TotalSeconds }
    }
} finally { if ($NoSnapshots -and $null -ne $prevPolicy) { [void](Invoke-Bridge "snapshot_policy $prevPolicy" 5000) } }
$s1 = Sample
"growth over $Cycles cycles: handles {0:+#;-#;0} ({1:N1}/cycle)  private MB {2:+#;-#;0} ({3:N1}/cycle)  GDI {4:+#;-#;0}  USER {5:+#;-#;0}  threads {6:+#;-#;0}" -f ($s1.handles - $s0.handles), (($s1.handles - $s0.handles) / $Cycles), ($s1.mb - $s0.mb), (($s1.mb - $s0.mb) / $Cycles), ($s1.gdi - $s0.gdi), ($s1.user - $s0.user), ($s1.threads - $s0.threads)
