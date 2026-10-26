# Per-user (HKCU, no admin) COM + SolidWorks add-in registration for the SolidPilot Bridge. Close SolidWorks first, run, start SolidWorks.
#   register:    powershell -NoProfile -ExecutionPolicy Bypass -File register-bridge.ps1 [-Dll <path to SolidPilotBridge.dll>]
#   unregister:  powershell -NoProfile -ExecutionPolicy Bypass -File register-bridge.ps1 -Unregister
# What it writes (all under HKCU, removed by -Unregister):
#   Software\Classes\SolidPilot.Bridge, Software\Classes\CLSID\{guid}              COM class -> mscoree.dll with the assembly CodeBase
#   Software\SolidWorks\Addins\{guid}                                             add-in entry (Title / Description)
#   Software\SolidWorks\AddInsStartup\{guid} = 1                                   load at SolidWorks start-up
# FINDING 2026-10-26: the HKCU-only registration was NOT listed in Tools > Add-Ins. SolidWorks enumerates add-ins from HKLM\SOFTWARE\SolidWorks\Addins (all built-in
# add-ins live there), so use -Hive HKLM from an ELEVATED PowerShell (COM class + add-in entry in HKLM; the start-up flag stays in HKCU).
# Remove the old per-user entries first:  register-bridge.ps1 -Unregister   (HKCU) ; then:  register-bridge.ps1 -Hive HKLM   (elevated).
param([string]$Dll = (Join-Path $PSScriptRoot 'bin\Debug\SolidPilotBridge.dll'), [switch]$Unregister, [ValidateSet('HKCU', 'HKLM')][string]$Hive = 'HKCU')
$guid = '{6F0B8E2A-3C41-4D6B-9A57-1E2D5C7A9B10}'; $prog = 'SolidPilot.Bridge'
$root = if ($Hive -eq 'HKCU') { 'HKCU:\Software' } else { 'HKLM:\SOFTWARE' }
$classes = "$root\Classes"; $sw = "$root\SolidWorks"
if ($Unregister) {
    foreach ($k in "$classes\CLSID\$guid", "$classes\$prog", "$sw\Addins\$guid") { if (Test-Path $k) { Remove-Item $k -Recurse -Force; "removed $k" } }
    if (Get-ItemProperty 'HKCU:\Software\SolidWorks\AddInsStartup' -Name $guid -ErrorAction SilentlyContinue) { Remove-ItemProperty 'HKCU:\Software\SolidWorks\AddInsStartup' -Name $guid; "removed start-up flag" }
    return
}
if (-not (Test-Path $Dll)) { throw "Bridge DLL not found: $Dll (build SolidPilotBridge.csproj first)" }
$dll = (Resolve-Path $Dll).Path; $uri = ([Uri]$dll).AbsoluteUri
$asm = [Reflection.AssemblyName]::GetAssemblyName($dll).FullName
New-Item "$classes\$prog" -Force | Out-Null; Set-ItemProperty "$classes\$prog" '(default)' 'SolidPilot Bridge'
New-Item "$classes\$prog\CLSID" -Force | Out-Null; Set-ItemProperty "$classes\$prog\CLSID" '(default)' $guid
New-Item "$classes\CLSID\$guid" -Force | Out-Null; Set-ItemProperty "$classes\CLSID\$guid" '(default)' 'SolidPilot Bridge'
New-Item "$classes\CLSID\$guid\ProgId" -Force | Out-Null; Set-ItemProperty "$classes\CLSID\$guid\ProgId" '(default)' $prog
$ip = "$classes\CLSID\$guid\InprocServer32"; New-Item $ip -Force | Out-Null
Set-ItemProperty $ip '(default)' "$env:WINDIR\System32\mscoree.dll"; Set-ItemProperty $ip 'ThreadingModel' 'Both'
Set-ItemProperty $ip 'Class' 'SolidPilot.Bridge.BridgeAddin'; Set-ItemProperty $ip 'Assembly' $asm; Set-ItemProperty $ip 'RuntimeVersion' 'v4.0.30319'; Set-ItemProperty $ip 'CodeBase' $uri
New-Item "$sw\Addins\$guid" -Force | Out-Null
Set-ItemProperty "$sw\Addins\$guid" '(default)' 0 -Type DWord; Set-ItemProperty "$sw\Addins\$guid" 'Title' 'SolidPilot Bridge'; Set-ItemProperty "$sw\Addins\$guid" 'Description' 'In-process bridge for the SolidPilot framework (UI-thread access, command events)'
New-Item 'HKCU:\Software\SolidWorks\AddInsStartup' -Force | Out-Null; New-ItemProperty 'HKCU:\Software\SolidWorks\AddInsStartup' -Name $guid -Value 1 -PropertyType DWord -Force | Out-Null   # start-up flag is ALWAYS per user
"registered $Hive : $guid -> $dll"
