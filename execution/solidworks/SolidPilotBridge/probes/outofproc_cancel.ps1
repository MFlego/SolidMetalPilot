# go/no-go probe 17 (comparison side): can an OUT-OF-PROCESS client (a) CANCEL a SolidWorks command by returning 1 from CommandOpenPreNotify and (b) receive OnIdleNotify?
# Test command = Zoom To Fit (swCommands_ZoomToFit = 332), observed through the view scale: case A lets it run (the view must change), case B cancels it (the view must stay as it was).
param([int]$Command = 332)
. "$PSScriptRoot\_common.ps1"; $redist = Get-SwRedist
Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.swconst.dll"); [void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.sldworks.dll")
Add-Type -ReferencedAssemblies "$redist\SolidWorks.Interop.sldworks.dll", "$redist\SolidWorks.Interop.swconst.dll", "System.Windows.Forms" -TypeDefinition @"
using System; using System.Text; using System.Threading; using System.Windows.Forms; using SolidWorks.Interop.sldworks;
public class CancelTest {
  SldWorks sw; StringBuilder log = new StringBuilder(); public volatile int CancelCmd = -1; public int Idle = 0;
  void L(string s) { log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); }
  static void Pump(int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); } }
  public string Run(object o, int cmd) {
    sw = (SldWorks)o;
    sw.CommandOpenPreNotify += (c, u) => { L("pre-open " + c + " user=" + u + (c == CancelCmd ? " -> CANCEL (return 1)" : "")); return c == CancelCmd ? 1 : 0; };
    sw.CommandCloseNotify += (c, r) => { L("close " + c + " reason=" + r); return 0; };
    sw.OnIdleNotify += () => { Idle++; return 0; };
    var doc = (ModelDoc2)sw.ActiveDoc; var view = (ModelView)doc.ActiveView;
    doc.ViewZoomtofit2(); Pump(400); double fit = view.Scale2;
    view.ZoomByFactor(0.5); Pump(400); double zoomed = view.Scale2; L("scale: fit=" + fit.ToString("G6") + "  zoomed-out=" + zoomed.ToString("G6"));
    CancelCmd = -1; bool okA = sw.RunCommand(cmd, ""); Pump(600); double a = view.Scale2;
    L("A (allow)  : RunCommand=" + okA + " scale=" + a.ToString("G6") + " -> " + (Math.Abs(a - fit) < Math.Abs(a - zoomed) ? "command RAN (view fitted)" : "command did NOT run"));
    view.ZoomByFactor(0.5); Pump(400); double z2 = view.Scale2;
    CancelCmd = cmd; bool okB = sw.RunCommand(cmd, ""); Pump(600); double b = view.Scale2; CancelCmd = -1;
    L("B (cancel) : RunCommand=" + okB + " scale=" + b.ToString("G6") + " (was " + z2.ToString("G6") + ") -> " + (Math.Abs(b - z2) <= 1e-9 * Math.Max(1.0, Math.Abs(z2)) ? "command CANCELLED (view unchanged)" : "command still RAN"));
    doc.ViewZoomtofit2(); Pump(2000);
    L("OnIdleNotify events received during the whole run: " + Idle);
    return log.ToString();
  }
}
"@
$t = New-Object CancelTest
$t.Run((Get-SolidWorks), $Command)
