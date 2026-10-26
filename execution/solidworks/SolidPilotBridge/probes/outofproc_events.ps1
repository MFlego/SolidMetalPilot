# go/no-go probe 13 (comparison side): can an OUT-OF-PROCESS client receive SolidWorks events (command open/close, selection) without the add-in?
# Run with:  powershell -STA -NoProfile -File outofproc_events.ps1 -Seconds 15 -Out events.txt      then trigger commands / selections in SolidWorks (or via the bridge) meanwhile.
param([int]$Seconds = 15, [string]$Out = "$env:TEMP\outofproc_events.txt")
. "$PSScriptRoot\_common.ps1"; $redist = Get-SwRedist
Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.swconst.dll"); [void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.sldworks.dll")
Add-Type -ReferencedAssemblies "$redist\SolidWorks.Interop.sldworks.dll", "$redist\SolidWorks.Interop.swconst.dll" -TypeDefinition @"
using System; using System.IO; using SolidWorks.Interop.sldworks;
public class EvSink {
  SldWorks sw; PartDoc part; string path;
  public EvSink(object swObj, string path) { sw = (SldWorks)swObj; this.path = path; File.WriteAllText(path, ""); }
  void Log(string s) { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + System.Environment.NewLine); }
  public void Attach() {
    sw.CommandOpenPreNotify += (c, u) => { Log("command-open " + c); return 0; };
    sw.CommandCloseNotify += (c, r) => { Log("command-close " + c + " reason " + r); return 0; };
    sw.ActiveDocChangeNotify += () => { Log("active-doc-change"); return 0; };
    part = sw.ActiveDoc as PartDoc;
    if (part != null) { part.NewSelectionNotify += () => { Log("selection-new"); return 0; }; part.UserSelectionPostNotify += () => { Log("user-select-post"); return 0; }; Log("attached to part"); }
  }
}
"@
$sw = Get-SolidWorks
$sink = New-Object EvSink($sw, $Out); $sink.Attach()
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
"done; events written to $Out"
