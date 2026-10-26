# A/B comparison for the add-in go/no-go: run the SAME typed C# probe (a) in-process through the bridge's run_cs and (b) out-of-process through oop_host.ps1, then diff the outputs as
# whitespace-normalised word streams. Timings are information only.
# Most probes only read the active document. probe09 / probe15 open a reference part read-only (never saved): pass -RefPart <a .SLDPRT that contains a Convert feature>.
# probe16 / probe19 work on a throw-away COPY of the cabinet part (they edit it in memory, never save): pass -ChainCopy <copy of a part with a Convert feature, 12 bends / 4 rips>.
# A probe whose file argument is missing is skipped.
# Usage: powershell -NoProfile -File ab_compare.ps1 [-Include probe05] [-RefPart C:\path\ref.SLDPRT] [-ChainCopy C:\path\scratch_copy.SLDPRT]
param([string]$Include = "", [string]$RefPart = "", [string]$ChainCopy = "")
$here = $PSScriptRoot
. "$here\_common.ps1"
$probes = @(
    @{ f = 'probe05_tree.cs';            a = '';                  need = '' },
    @{ f = 'probe06_convert_def.cs';     a = '';                  need = '' },
    @{ f = 'probe07_measure.cs';         a = '';                  need = '' },
    @{ f = 'probe12_fold_defs.cs';       a = '';                  need = '' },
    @{ f = 'probe10_flat_corners.cs';    a = '';                  need = '' },
    @{ f = 'probe18_census.cs';          a = '';                  need = '' },
    @{ f = 'probe09_read_reference.cs';  a = $RefPart;            need = 'RefPart' },
    # probe15 modifies the reference part IN MEMORY only (opened read-only, closed unsaved) - the write-path comparison
    @{ f = 'probe15_convert_write.cs';   a = "$RefPart;fix";      need = 'RefPart' },
    @{ f = 'probe15_convert_write.cs';   a = "$RefPart;rewrite";  need = 'RefPart' },
    # probe16 / probe19 create or rewrite features on a scratch COPY (opened, never saved)
    @{ f = 'probe16_chain.cs';           a = $ChainCopy;          need = 'ChainCopy' },
    @{ f = 'probe19_bendedges_write.cs'; a = $ChainCopy;          need = 'ChainCopy' }
)
function Words([string]$s) { ($s -split '\s+') | Where-Object { $_ -ne '' -and $_ -ne ';;' } }    # the bridge flattens line breaks into runs of spaces
foreach ($p in ($probes | Where-Object { $_.f -like "*$Include*" })) {
    if ($p.need -and -not (Get-Variable $p.need -ValueOnly)) { "=== $($p.f): skipped (needs -$($p.need))"; continue }
    $file = Join-Path $here $p.f
    "=== $($p.f)  arg='$($p.a)'"
    $t = [Diagnostics.Stopwatch]::StartNew()
    $arg = if ($p.a) { "|" + $p.a } else { "" }
    $inproc = Invoke-Bridge ("run_cs " + $file + $arg) 240000
    $tIn = $t.ElapsedMilliseconds; $t.Restart()
    $hostArgs = @('-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'oop_host.ps1'), '-Probe', $file)
    if ($p.a) { $hostArgs += @('-Arg', $p.a) }      # an empty -Arg is dropped by powershell.exe -File, so only pass it when there is one
    $raw = & powershell @hostArgs 2>&1 | Out-String
    $tOut = $t.ElapsedMilliseconds
    $idx = $raw.IndexOf('@@RESULT'); $oop = if ($idx -ge 0) { $raw.Substring($idx + 8) } else { $raw }
    $a = @(Words $inproc); $b = @(Words $oop)
    "in-process: $($a.Count) words, $tIn ms   |   out-of-process: $($b.Count) words, $tOut ms (incl. ~3 s process start + compile)"
    $d = Compare-Object $a $b -SyncWindow 0
    if (-not $d) { "IDENTICAL" } else {
        "DIFFERENT ($($d.Count) word positions):"
        "  in-proc : " + (($a | Select-Object -First 60) -join ' ')
        "  out-proc: " + (($b | Select-Object -First 60) -join ' ')
    }
}
