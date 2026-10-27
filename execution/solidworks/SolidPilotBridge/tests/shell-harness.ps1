# Hosts the REAL add-in shell stack - UiThread, ToolsLoader (byte-loaded tools), ToolGate, HttpFacade - in a separate STA process, against the running SolidWorks over COM.
# Purpose: test the shell and the tools generation (JSON contract, gate, loader, REST façade) without deploying into SolidWorks, i.e. before any SolidWorks restart.
# What it cannot test: true in-process execution, and DIALOG_BLOCKING (the dialog scan looks at THIS process).   Stop it by killing the process or waiting for -Seconds.
# Usage: powershell -STA -NoProfile -File shell-harness.ps1 -ShellDir <folder with SolidPilotBridge.dll and a tools\ subfolder> [-Port 5002] [-Seconds 900]
param([Parameter(Mandatory)][string]$ShellDir, [int]$Port = 5002, [int]$Seconds = 900)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$ShellDir = (Resolve-Path $ShellDir).Path
$shell = [Reflection.Assembly]::LoadFrom("$ShellDir\SolidPilotBridge.dll")
$sw = [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application')
Add-Type -ReferencedAssemblies "$ShellDir\SolidPilotBridge.dll", "$ShellDir\SolidWorks.Interop.sldworks.dll" -TypeDefinition @"
using SolidWorks.Interop.sldworks; using SolidPilot.Bridge;
public class HarnessServices : IBridgeServices {
  readonly SldWorks _sw; public HarnessServices(object sw) { _sw = (SldWorks)sw; }
  public SldWorks SolidWorks { get { return _sw; } }
  public void Log(string message) { BridgeLog.Info("[harness] " + message); }
  public string DialogsJson() { return Dialogs.ToJson(Dialogs.List()); }
}
"@
$env:SOLIDPILOT_PORT = "$Port"
[SolidPilot.Bridge.UiThread]::Init()                                   # on THIS (STA) thread, which pumps messages below
$svc = New-Object HarnessServices($sw)
[SolidPilot.Bridge.ToolsLoader]::Init("$ShellDir\tools")
"tools: " + [SolidPilot.Bridge.ToolsLoader]::Load($svc)
$http = New-Object SolidPilot.Bridge.HttpFacade([Func[string]]{ '{"harness":true}' })
$http.Start()
"http: " + $http.InfoJson()
"serving on http://localhost:$Port/ for $Seconds s (process $PID)"
$timer = New-Object Windows.Forms.Timer; $timer.Interval = $Seconds * 1000; $timer.Add_Tick({ [Windows.Forms.Application]::ExitThread() }); $timer.Start()
[Windows.Forms.Application]::Run()
$http.Stop()
"stopped"
