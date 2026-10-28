// Probe 75: more sheet-metal creation routes that probe 69 did not cover, on an EMPTY part: the SWEPT FLANGE (CreateDefinition(swFmSweptFlange = 117) -> ISweptFlangeFeatureData, a profile sketch and a path
// sketch) and the LOFTED BEND (IFeatureManager.InsertSheetMetalLoftedBend2 with two open profile sketches). arg = "mode;key=value;..."   modes: swept | loft
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void L(string s) { sb.Append(s).Append(" ;; "); }
    static List<Feature> Top(ModelDoc2 doc) { var l = new List<Feature>(); var f = (Feature)doc.FirstFeature(); while (f != null) { l.Add(f); f = (Feature)f.GetNextFeature(); } return l; }
    static string Size(ModelDoc2 doc)
    {
        var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox();
        return ((x[3] - x[0]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " x " + ((x[4] - x[1]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " x " + ((x[5] - x[2]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " mm, " + b.GetFaceCount() + "f";
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : dflt; }
    // a sketch on a named plane (an offset z for the plane is given by a reference plane name) with a polyline (x, y in the sketch, meters); returns the sketch's name; the sketch is closed afterwards
    static string Poly(ModelDoc2 doc, string plane, double[][] pts, bool close)
    {
        doc.ClearSelection2(true); doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); doc.SketchManager.InsertSketch(true);
        for (int i = 0; i + 1 < pts.Length; i++) doc.SketchManager.CreateLine(pts[i][0], pts[i][1], 0, pts[i + 1][0], pts[i + 1][1], 0);
        if (close) doc.SketchManager.CreateLine(pts[pts.Length - 1][0], pts[pts.Length - 1][1], 0, pts[0][0], pts[0][1], 0);
        string name = ((Feature)((Sketch)doc.SketchManager.ActiveSketch)).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); return name;
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var parts = (arg ?? "swept").Split(';'); string mode = parts[0].Trim().ToLowerInvariant();
        foreach (var p in parts) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var fm = doc.FeatureManager; L("mode " + mode + "; part: " + Size(doc));
        double t = N(a, "t", 0.0015), r = N(a, "r", 0.0015);
        if (mode == "swept")
        {
            // the path: a straight line along x on the Top Plane (x 0 .. 100 mm); the profile: an open L on the Right Plane at the path's start (15 mm along y, 20 mm along z)
            string path = Poly(doc, "Top Plane", new[] { new[] { 0.0, 0.0 }, new[] { 0.1, 0.0 } }, false);
            string prof = Poly(doc, "Right Plane", new[] { new[] { 0.015, 0.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, 0.02 } }, false);
            L("path sketch " + path + ", profile sketch " + prof);
            Func<string, Action<ISweptFlangeFeatureData>, bool> tryIt = (label, setup) =>
            {
                var before = new HashSet<string>(Top(doc).Select(x => x.Name)); Feature f = null; string thrown = null;
                try { var d = (ISweptFlangeFeatureData)fm.CreateDefinition(117); setup(d); f = fm.CreateFeature(d) as Feature; }
                catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
                var fresh = Top(doc).Where(x => !before.Contains(x.Name)).ToList();
                L(label + ": returned " + (f == null ? "null" : f.Name) + (thrown != null ? "; THREW " + thrown : "") + "; new: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(x => x.Name + "[" + x.GetTypeName2() + "]").ToArray())) + "; " + Size(doc));
                if (f != null) { bool w; L("   error " + f.GetErrorCode2(out w)); }
                return f != null;
            };
            Func<string, object> sk = n => ((PartDoc)doc).FeatureByName(n);
            if (!tryIt("profile + path sketch features, thickness " + t * 1000 + " radius " + r * 1000, d => { d.Profile = sk(prof); d.Path = sk(path); d.OverrideDefaultSheetMetalParameters = true; d.Thickness = t; d.UseDefaultRadius = false; d.BendRadius = r; }))
                tryIt("same, wrapped in DispatchWrapper arrays", d => { d.Profile = new System.Runtime.InteropServices.DispatchWrapper(sk(prof)); d.Path = new[] { new System.Runtime.InteropServices.DispatchWrapper(sk(path)) }; d.OverrideDefaultSheetMetalParameters = true; d.Thickness = t; d.UseDefaultRadius = false; d.BendRadius = r; });
        }
        else if (mode == "loft")
        {
            // two OPEN rectangles (one corner left open) on parallel planes 30 mm apart: 40 x 30 at z = 0 and 20 x 15 at z = 30 (planes: Front Plane and an offset plane named by the caller: plane2)
            string plane2 = a.ContainsKey("plane2") ? a["plane2"] : "Plane1";
            string s1 = Poly(doc, "Front Plane", new[] { new[] { -0.02, -0.015 }, new[] { 0.02, -0.015 }, new[] { 0.02, 0.015 }, new[] { -0.02, 0.015 }, new[] { -0.02, -0.0145 } }, false);
            string s2 = Poly(doc, plane2, new[] { new[] { -0.01, -0.0075 }, new[] { 0.01, -0.0075 }, new[] { 0.01, 0.0075 }, new[] { -0.01, 0.0075 }, new[] { -0.01, -0.007 } }, false);
            L("profile sketches " + s1 + ", " + s2);
            Func<string, Action, Func<object>, bool> attempt = (label, select, create) =>
            {
                var before = new HashSet<string>(Top(doc).Select(x => x.Name)); doc.ClearSelection2(true); select();
                int picked = ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1); object ret = null; string thrown = null;
                try { ret = create(); } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
                var fresh = Top(doc).Where(x => !before.Contains(x.Name)).ToList();
                L(label + ": picked " + picked + "; returned " + (ret == null ? "null" : ret is Feature ? ((Feature)ret).Name : ret.ToString()) + (thrown != null ? "; THREW " + thrown : "") + "; new: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(x => x.Name + "[" + x.GetTypeName2() + "]").ToArray())) + "; " + Size(doc));
                return fresh.Count > 0;
            };
            Action selBoth = () => { doc.Extension.SelectByID2(s1, "SKETCH", 0, 0, 0, false, 0, null, 0); doc.Extension.SelectByID2(s2, "SKETCH", 0, 0, 0, true, 0, null, 0); };
            foreach (int dirType in new[] { 0, 1 })
                if (attempt("lofted bend, thick dir " + dirType + ", t " + t * 1000, selBoth, () => fm.InsertSheetMetalLoftedBend2(dirType, t, false, r, false, 0, 0.0001, 4, 0.01, 0.1))) break;
            attempt("lofted bend (version 1), t " + t * 1000, selBoth, () => fm.InsertSheetMetalLoftedBend(0, t));
        }
        return sb.ToString();
    }
}
