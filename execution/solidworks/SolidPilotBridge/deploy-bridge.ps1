# Builds the SolidPilot Bridge add-in SHELL and the TOOLS into the folders the registered add-in loads from, then tells you what to do next.
#   Shell  -> SolidPilotBridge\bin\<Config>\SolidPilotBridge.dll          (the registered CodeBase; LOCKED while SolidWorks runs -> SolidWorks must be closed)
#   Tools  -> SolidPilotBridge\bin\<Config>\tools\SolidPilotBridge.Tools.dll (+ pdb, Newtonsoft.Json; loaded from BYTES, never locked -> rebuild any time, then send 'reload_tools')
# Usage:  deploy-bridge.ps1              build shell + tools (SolidWorks must be closed)
#         deploy-bridge.ps1 -ToolsOnly   build only the tools (SolidWorks may be running), then reload them:  bridge-ping.ps1 -Command reload_tools
param([switch]$ToolsOnly, [string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$sw = Split-Path -Parent $PSScriptRoot                                   # ...\execution\solidworks
$msb = Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe", "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $msb) { throw 'MSBuild (Visual Studio 2019+ or Build Tools) not found' }
$fw = Get-ChildItem "$sw\packages\Microsoft.NETFramework.ReferenceAssemblies.net48.*\build\.NETFramework\v4.8" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$fwArg = @(if ($fw) { "-p:FrameworkPathOverride=$fw" })                        # the 4.8 targeting pack is not installed on every machine (@() keeps it an array: a lone string would be splatted per character)

if (-not $ToolsOnly) {
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'SolidWorks is running: close it (the shell DLL is locked while loaded) or use -ToolsOnly' }
    & $msb "$PSScriptRoot\SolidPilotBridge.csproj" -nologo -v:minimal "-p:Configuration=$Configuration" -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'shell build failed' }
}
& $msb "$sw\SolidPilotBridge.Tools\SolidPilotBridge.Tools.csproj" -nologo -v:minimal "-p:Configuration=$Configuration" @fwArg
if ($LASTEXITCODE -ne 0) { throw 'tools build failed' }
$bin = "$PSScriptRoot\bin\$Configuration"
Get-Item "$bin\SolidPilotBridge.dll", "$bin\tools\SolidPilotBridge.Tools.dll" | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String
if ($ToolsOnly) { 'tools built. If SolidWorks is running with the bridge: powershell -File bridge-ping.ps1 -Command reload_tools' }
else { 'built. Start SolidWorks, then: powershell -File bridge-ping.ps1 -Command ping   (expect: pong v0.8 or newer)   and   -Command tools_info' }
