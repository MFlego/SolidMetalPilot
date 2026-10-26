# go/no-go probe 13b (comparison side): does an OUT-OF-PROCESS selection-event handler get the same TYPED detail (object type, mark, edge midpoint / face box) at event time as the add-in?
# The test selects an edge with mark 2 and a face with mark 1 through the API and logs what the handler can read while the event is being delivered.
. "$PSScriptRoot\_common.ps1"; $redist = Get-SwRedist
Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.swconst.dll"); [void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.sldworks.dll")
Add-Type -ReferencedAssemblies "$redist\SolidWorks.Interop.sldworks.dll", "$redist\SolidWorks.Interop.swconst.dll", "System.Windows.Forms" -TypeDefinition @"
using System; using System.Text; using System.Threading; using System.Windows.Forms; using SolidWorks.Interop.sldworks; using SolidWorks.Interop.swconst;
public class SelSink {
  ModelDoc2 doc; PartDoc part; StringBuilder log = new StringBuilder();
  static void Pump(int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); } }
  void Describe(string ev) {
    try {
      var sm = (SelectionMgr)doc.SelectionManager; int n = sm.GetSelectedObjectCount2(-1); if (n == 0) { log.AppendLine(ev + " count=0"); return; }
      int t = sm.GetSelectedObjectType3(n, -1); int mark = sm.GetSelectedObjectMark(n); string d = "";
      if (t == (int)swSelectType_e.swSelEDGES) { var e = (Edge)sm.GetSelectedObject6(n, -1); var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per); var p = (double[])c.Evaluate2((s + en) / 2, 0); d = string.Format("edge mid ({0:F2},{1:F2},{2:F2}) mm", p[0] * 1000, p[1] * 1000, p[2] * 1000); }
      else if (t == (int)swSelectType_e.swSelFACES) { var f = (Face2)sm.GetSelectedObject6(n, -1); var b = (double[])f.GetBox(); d = string.Format("face box {0:F2},{1:F2},{2:F2} .. {3:F2},{4:F2},{5:F2} mm", b[0]*1000, b[1]*1000, b[2]*1000, b[3]*1000, b[4]*1000, b[5]*1000); }
      log.AppendLine(ev + " count=" + n + " type=" + t + " mark=" + mark + " " + d);
    } catch (Exception e) { log.AppendLine(ev + " handler error " + e.GetType().Name + ": " + e.Message); }
  }
  public string Run(object o) {
    var sw = (SldWorks)o; doc = (ModelDoc2)sw.ActiveDoc; part = (PartDoc)doc;
    part.NewSelectionNotify += () => { Describe("NewSelectionNotify     "); return 0; };
    part.UserSelectionPostNotify += () => { Describe("UserSelectionPostNotify"); return 0; };
    var sm = (SelectionMgr)doc.SelectionManager; var body = (Body2)((object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false))[0];
    Face2 plate = null; foreach (Face2 f in (object[])body.GetFaces()) { var bx = (double[])f.GetBox(); var s = (Surface)f.GetSurface(); if (s.IsPlane() && Math.Abs(bx[2]) < 1e-4 && Math.Abs(bx[5]) < 1e-4 && bx[0] < 0 && bx[3] > 0 && bx[1] < 0 && bx[4] > 0) plate = f; }
    Edge edge = null; foreach (Edge e in (object[])plate.GetEdges()) { edge = e; break; }
    doc.ClearSelection2(true); Pump(200);
    var sd = sm.CreateSelectData(); sd.Mark = 1; log.AppendLine("select plate face (mark 1) -> " + ((Entity)plate).Select4(false, sd)); Pump(500);
    sd = sm.CreateSelectData(); sd.Mark = 2; log.AppendLine("select plate edge  (mark 2) -> " + ((Entity)edge).Select4(true, sd)); Pump(500);
    doc.ClearSelection2(true); Pump(300);
    return log.ToString();
  }
}
"@
$t = New-Object SelSink
$t.Run((Get-SolidWorks))
