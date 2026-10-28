// Probe 72: start a SolidWorks command (ISldWorks.RunCommand) so its PropertyManager opens. arg = "id=372;title=Jog" plus optionally
//   sketch=Sketch6                                  select that sketch (by name) first
//   faceline=fx,fy,fz:x1,y1,z1,x2,y2,z2             make a sketch on the face at (fx, fy, fz) (mm) with ONE line between the two model points (mm), leave it closed, select it, then run the command
//   face=fx,fy,fz                                   select the face at that point (mm) first (object pick, view independent)
// Nothing is closed afterwards: inspect the PropertyManager with probe 73 (Win32 children) or ui_inspect (WPF ones) and cancel it with probe 73 click=<the red X>.
using System;
using System.Collections.Generic;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static double[] P(string s) { var p = s.Split(','); var d = new double[p.Length]; for (int i = 0; i < p.Length; i++) d[i] = double.Parse(p[i], CultureInfo.InvariantCulture) / 1000; return d; }
    static Face2 FaceAt(ModelDoc2 doc, double x, double y, double z)
    {
        Face2 best = null; double bd = 1e9;
        foreach (Body2 b in (object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false))
            foreach (Face2 f in (object[])b.GetFaces()) { var q = f.GetClosestPointOn(x, y, z) as double[]; if (q == null) continue; double d = Math.Sqrt((q[0] - x) * (q[0] - x) + (q[1] - y) * (q[1] - y) + (q[2] - z) * (q[2] - z)); if (d < bd) { bd = d; best = f; } }
        return bd < 1e-4 ? best : null;
    }

    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; string log = "";
        if (a.ContainsKey("faceline"))
        {
            var fl = a["faceline"].Split(':'); var f = P(fl[0]); var l = P(fl[1]); var face = FaceAt(doc, f[0], f[1], f[2]); if (face == null) return "no face at " + fl[0];
            doc.ClearSelection2(true); ((Entity)face).Select4(false, null); doc.SketchManager.InsertSketch(true);
            var sk = (Sketch)doc.SketchManager.ActiveSketch; var mu = (MathUtility)sw.GetMathUtility(); var xf = (MathTransform)sk.ModelToSketchTransform;
            Func<double, double, double, double[]> to2 = (x, y, z) => (double[])((MathPoint)((MathPoint)mu.CreatePoint(new double[] { x, y, z })).MultiplyTransform(xf)).ArrayData;
            var p1 = to2(l[0], l[1], l[2]); var p2 = to2(l[3], l[4], l[5]); doc.SketchManager.CreateLine(p1[0], p1[1], 0.0, p2[0], p2[1], 0.0);
            string name = ((Feature)sk).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true);
            doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0); log += "sketch " + name + " selected; ";
        }
        string nm; if (a.TryGetValue("sketch", out nm)) { doc.ClearSelection2(true); log += "select sketch " + nm + ": " + doc.Extension.SelectByID2(nm, "SKETCH", 0, 0, 0, false, 0, null, 0) + "; "; }
        string fc; if (a.TryGetValue("face", out fc)) { var f = P(fc); var face = FaceAt(doc, f[0], f[1], f[2]); log += "face " + (face != null && ((Entity)face).Select4(a.ContainsKey("sketch") || a.ContainsKey("faceline"), null)) + "; "; }
        int id; if (!a.ContainsKey("id") || !int.TryParse(a["id"], out id)) return log + "no id: selection only; selected objects: " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
        bool ok = sw.RunCommand(id, a.ContainsKey("title") ? a["title"] : "");
        return log + "RunCommand(" + id + ") -> " + ok + "; selected objects: " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
    }
}
