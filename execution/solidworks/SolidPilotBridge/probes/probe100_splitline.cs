// Probe 100 (round 7): Split Line (projected) of ONE sketch onto ONE face of a surface body, so the sketch lines become body EDGES (pickable as Convert rip edges, no click on a sketch line needed).
// arg = "sketch=RIP_LEDGE;x=0.115;y=0;z=0.05;body=DeleteFace2"  (x y z in METERS: a point ON the face to split; body = the surface body's name)
using System;
using System.Collections.Generic;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc;
        Func<string, double> num = k => double.Parse(a[k], CultureInfo.InvariantCulture);
        string sketch = a["sketch"]; string bodyName = a.ContainsKey("body") ? a["body"] : null;
        double[] p = { num("x"), num("y"), num("z") };
        IFace2 best = null; double bd = 1e9; string bn = "";
        foreach (var o in (part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[]) ?? new object[0])
        {
            var b = o as IBody2; if (b == null) continue;
            if (bodyName != null && b.Name != bodyName) continue;
            foreach (var fo in (b.GetFaces() as object[]) ?? new object[0])
            {
                var f = fo as IFace2; if (f == null) continue;
                var cp = f.GetClosestPointOn(p[0], p[1], p[2]) as double[]; if (cp == null) continue;
                double d = Math.Sqrt((cp[0] - p[0]) * (cp[0] - p[0]) + (cp[1] - p[1]) * (cp[1] - p[1]) + (cp[2] - p[2]) * (cp[2] - p[2]));
                if (d < bd) { bd = d; best = f; bn = b.Name; }
            }
        }
        if (best == null || bd > 1e-4) return "FACE_NOT_FOUND nearest " + bd.ToString("0.#####", CultureInfo.InvariantCulture) + " m on body " + bn;
        doc.ClearSelection2(true);
        bool first = true;
        foreach (var sk in sketch.Split(','))                                    // several sketches (one line each) go in with mark 1
        {
            if (!doc.Extension.SelectByID2(sk.Trim(), "SKETCH", 0, 0, 0, !first, 1, null, 0)) return "SKETCH_NOT_SELECTED " + sk;
            first = false;
        }
        var sd = ((ISelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = 2;
        if (!((IEntity)best).Select4(true, sd)) return "FACE_NOT_SELECTED";
        int nSel = ((ISelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
        int before = best.GetEdgeCount();
        int fc0 = ((IFeatureManager)doc.FeatureManager).GetFeatureCount(false);
        doc.InsertSplitLineProject(a.ContainsKey("dir") && a["dir"] == "1", a.ContainsKey("flip") && a["flip"] == "1");                                  // IModelDoc2 (not the feature manager); returns void: the feature count tells
        doc.ClearSelection2(true);
        IFeature ft = null; var f0 = (IFeature)doc.FirstFeature(); while (f0 != null) { if (f0.GetTypeName2() == "SplitLine") ft = f0; f0 = (IFeature)f0.GetNextFeature(); }
        if (ft == null) return "SPLIT_FAILED (selections " + nSel + ", features " + fc0 + " -> " + ((IFeatureManager)doc.FeatureManager).GetFeatureCount(false) + ")";
        int after = 0; string faces = "";
        foreach (var o in (part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[]) ?? new object[0]) { var b = o as IBody2; if (b != null && b.Name == bn) { after = b.GetFaceCount(); faces = b.GetFaceCount().ToString(); } }
        return "split feature " + ft.Name + " (" + ft.GetTypeName2() + ") body " + bn + " faces now " + faces + " ;; face edges before " + before;
    }
}
