// Probe 35: corner operations through the typed API on the ACTIVE tray fixture.
//   arg "list"            list the thin planar faces (flange END faces) near the corners with index, centroid, normal, area
//   arg "closed <i>"      select thin face #i (as listed) and call IModelDoc2.InsertSheetMetalClosedCorner()
//   arg "trim <i> [<j>]"  select thin face(s) and call IFeatureManager.InsertSheetMetalCornerTrim
// Reports the new feature (name / type / error) and the typed definition afterwards.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    class FaceInfo { public Face2 F; public double[] C; public double[] N; public double A; }

    static List<FaceInfo> ThinFaces(ModelDoc2 doc)
    {
        var res = new List<FaceInfo>(); var part = (PartDoc)doc;
        var body = (Body2)((object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false))[0];
        foreach (Face2 f in (object[])body.GetFaces())
        {
            var s = (Surface)f.GetSurface(); if (!s.IsPlane()) continue;
            double a = f.GetArea() * 1e6; if (a < 15 || a > 80) continue;            // mm2: a flange end face is ~ thickness x flange height
            var n = (double[])f.Normal; if (Math.Abs(n[2]) > 0.2) continue;           // vertical faces only (normal horizontal)
            var bx = (double[])f.GetBox(); var c = new[] { (bx[0] + bx[3]) / 2, (bx[1] + bx[4]) / 2, (bx[2] + bx[5]) / 2 };
            res.Add(new FaceInfo { F = f, C = c, N = n, A = a });
        }
        return res.OrderBy(x => x.C[0]).ThenBy(x => x.C[1]).ToList();
    }

    static string Describe(IEnumerable<FaceInfo> l)
    {
        var sb = new StringBuilder(); int i = 0;
        foreach (var x in l) sb.AppendLine("   #" + (i++) + " centre (" + (x.C[0] * 1000).ToString("F2") + ", " + (x.C[1] * 1000).ToString("F2") + ", " + (x.C[2] * 1000).ToString("F2") + ") mm  normal (" + x.N[0].ToString("F2") + ", " + x.N[1].ToString("F2") + ", " + x.N[2].ToString("F2") + ")  area " + x.A.ToString("F1") + " mm2");
        return sb.ToString();
    }

    static Feature LastFeature(ModelDoc2 doc) { Feature last = null; var f = (Feature)doc.FirstFeature(); while (f != null) { last = f; f = (Feature)f.GetNextFeature(); } return last; }
    static int CountFeatures(ModelDoc2 doc) { int n = 0; var f = (Feature)doc.FirstFeature(); while (f != null) { n++; f = (Feature)f.GetNextFeature(); } return n; }
    static List<string> FeatureNames(ModelDoc2 doc) { var l = new List<string>(); var f = (Feature)doc.FirstFeature(); while (f != null) { l.Add(f.Name + "[" + f.GetTypeName2() + "]"); f = (Feature)f.GetNextFeature(); } return l; }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sm = (SelectionMgr)doc.SelectionManager; var sb = new StringBuilder(); var faces = ThinFaces(doc);
        var parts = (arg ?? "list").Split(' '); string mode = parts[0];
        if (mode == "list") { sb.AppendLine(faces.Count + " thin vertical faces:"); sb.Append(Describe(faces)); return sb.ToString(); }

        var idx = parts.Skip(1).Select(p => int.Parse(p)).ToList(); var before = FeatureNames(doc);
        doc.ClearSelection2(true); var sd = sm.CreateSelectData();
        foreach (int i in idx) sb.AppendLine("select #" + i + " -> " + ((Entity)faces[i].F).Select4(true, sd));
        sb.AppendLine("selection count " + sm.GetSelectedObjectCount2(-1));
        object result = null; string how;
        try
        {
            if (mode == "closed") { doc.InsertSheetMetalClosedCorner(); how = "InsertSheetMetalClosedCorner()"; }
            else if (mode == "trim") { result = doc.FeatureManager.InsertSheetMetalCornerTrim(1, (int)swBreakCornerTypes_e.swBreakCornerTypeFillet, 0.002, 0, 0); how = "InsertSheetMetalCornerTrim(internal=1, fillet 2 mm)"; }
            else { return "unknown mode"; }
        }
        catch (Exception e) { return sb + "CALL THREW " + e.GetType().Name + ": " + e.Message; }
        sb.AppendLine(how + " -> " + (result == null ? "(void / null)" : "feature " + ((Feature)result).Name));
        var after = FeatureNames(doc); var added = after.Except(before).ToList();
        sb.AppendLine("features before " + before.Count + ", after " + after.Count + "; new: " + (added.Count == 0 ? "NONE" : string.Join(", ", added.ToArray())));
        var last = LastFeature(doc); bool w; int err = 0; try { err = last.GetErrorCode2(out w); } catch { }
        sb.AppendLine("last feature " + last.Name + " [" + last.GetTypeName2() + "] error " + err);
        return sb.ToString();
    }
}
