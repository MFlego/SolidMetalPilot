# Out-of-process host for a typed probe: compiles the probe's C# (the very file the bridge runs in-process through run_cs) in THIS separate STA process and calls Script.Run(sw, arg)
# against the running SolidWorks over COM. Used by ab_compare.ps1 for the "does in-process give different RESULTS than out-of-process?" comparison.
param([Parameter(Mandatory)][string]$Probe, [string]$Arg = "")
. "$PSScriptRoot\_common.ps1"; $redist = Get-SwRedist
[void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.swconst.dll"); [void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.sldworks.dll")
$src = [IO.File]::ReadAllText($Probe) + "`r`npublic static class OopHost { public static string Go(object o, string arg) { return Script.Run((SldWorks)o, arg); } }`r`n"
Add-Type -ReferencedAssemblies "$redist\SolidWorks.Interop.sldworks.dll", "$redist\SolidWorks.Interop.swconst.dll" -TypeDefinition $src
$t = [Diagnostics.Stopwatch]::StartNew()
try { $r = [OopHost]::Go((Get-SolidWorks), $Arg) } catch { $r = "EXCEPTION " + $_.Exception.GetType().Name + ": " + $_.Exception.Message }
"@@ELAPSED " + $t.ElapsedMilliseconds
"@@RESULT"
$r
