# Shared helpers for the PowerShell probes in this folder (dot-source it). Nothing here is machine specific: the SolidWorks API folder is discovered, or taken from $env:SW_REDIST.
#   Get-SwRedist       -> folder that holds SolidWorks.Interop.sldworks.dll / swconst.dll
#   Import-SwInterops  -> loads both assemblies, returns the folder
#   Get-SolidWorks     -> the running SolidWorks application object (if two SOLIDWORKS processes exist, close the windowless stray one first)
#   Invoke-Bridge      -> one command to the SolidPilot Bridge add-in over its named pipe; returns the payload after "OK " or $null when the bridge is absent / slow
function Get-SwRedist {
    if ($env:SW_REDIST -and (Test-Path "$env:SW_REDIST\SolidWorks.Interop.sldworks.dll")) { return $env:SW_REDIST }
    $hit = Get-ChildItem -Path "$env:ProgramFiles\Dassault Systemes\*\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $hit) { throw 'SolidWorks API assemblies not found - set $env:SW_REDIST to the folder that contains SolidWorks.Interop.sldworks.dll' }
    return $hit.DirectoryName
}
function Import-SwInterops { $r = Get-SwRedist; [void][Reflection.Assembly]::LoadFrom("$r\SolidWorks.Interop.swconst.dll"); [void][Reflection.Assembly]::LoadFrom("$r\SolidWorks.Interop.sldworks.dll"); $r }
function Get-SolidWorks { [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application') }
function Invoke-Bridge([string]$Command, [int]$TimeoutMs = 3000) {
    try {
        $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'SolidPilotBridge', [IO.Pipes.PipeDirection]::InOut)
        $c.Connect($TimeoutMs)
        $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c)
        $w.WriteLine($Command)
        $task = $r.ReadLineAsync(); if (-not $task.Wait($TimeoutMs)) { $c.Dispose(); return $null }
        $line = $task.Result; $c.Dispose()
        if ($line -and $line.StartsWith('OK ')) { return $line.Substring(3) } else { return $null }
    } catch { return $null }
}
