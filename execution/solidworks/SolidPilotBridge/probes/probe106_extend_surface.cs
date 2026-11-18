// EXTEND SURFACE of a surface body (round 10 build helper; no typed tool yet). Selects edges of the named surface body and calls IModelDoc2.InsertExtendSurface(ExtendLinear, EndCondition, Distance):
// a SURFACE-EXTEND feature (dimension D1@<feature> can be linked to an equation). Measured 2026-11-18: with NOTHING selected the call CRASHES SolidWorks (an empty selection killed the first attempt),
// with the edges selected it makes the feature, error 0; the modeler IBody2.ExtendSurface needs the edges wrapped as DispatchWrapper[] (a plain object[] answers null).
// arg = "<surface body name>|<edges>|<linear 0/1>|<distance in metres | upto:<plane or face name>@<selection mark>>"   edges = "top" (every edge longer than 1 mm whose two vertices lie within 0.6 mm of the body's highest y) or "pt:x,y,z;x,y,z" (mm; the edge nearest to each point, within 0.5 mm).
// Result: the feature, its error code, how many edges were selected, the surface body's box before and after (the extended body takes the FEATURE's name).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Box(Body2 b) { var x = (double[])b.GetBodyBox(); return string.Format(CultureInfo.InvariantCulture, "x {0:F2}..{1:F2} y {2:F2}..{3:F2} z {4:F2}..{5:F2} mm", x[0] * 1000, x[3] * 1000, x[1] * 1000, x[4] * 1000, x[2] * 1000, x[5] * 1000); }

    public static string Run(SldWorks sw, string arg)
    {
        var a = arg.Split('|'); if (a.Length < 4) return "arg = body|edges|linear|distance_m";
        string name = a[0], edgesSpec = a[1]; bool linear = a[2] == "1"; bool upto = a[3].StartsWith("upto:"); string uptoName = ""; int uptoMark = 0; double dist = 0;
        if (upto) { var u = a[3].Substring(5).Split('@'); uptoName = u[0]; uptoMark = u.Length > 1 ? int.Parse(u[1]) : 0; } else dist = double.Parse(a[3], CultureInfo.InvariantCulture);
        var doc = sw.ActiveDoc as ModelDoc2; var part = sw.ActiveDoc as IPartDoc; if (doc == null || part == null) return "no active part";
        Body2 body = null;
        var arr = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
        if (arr != null) foreach (var o in arr) { var b = o as Body2; if (b != null && b.Name == name) { body = b; break; } }
        if (body == null) return "no surface body " + name;
        var sb = new StringBuilder(); sb.Append("before: " + Box(body) + " ;; ");
        var edges = body.GetEdges() as object[]; var pick = new List<Edge>();
        if (edgesSpec == "top")
        {
            double ymax = ((double[])body.GetBodyBox())[4];
            foreach (var o in edges)
            {
                var e = o as Edge; var v0 = e.GetStartVertex() as Vertex; var v1 = e.GetEndVertex() as Vertex; if (v0 == null || v1 == null) continue;
                var p0 = (double[])v0.GetPoint(); var p1 = (double[])v1.GetPoint();
                double len = Math.Sqrt((p0[0] - p1[0]) * (p0[0] - p1[0]) + (p0[1] - p1[1]) * (p0[1] - p1[1]) + (p0[2] - p1[2]) * (p0[2] - p1[2]));
                if (len < 0.001) continue;                                    // an OFFSET copy has 0.11 mm sliver edges between the faces at the top: selecting them makes the extend fail silently (no feature)
                if (p0[1] > ymax - 0.0006 && p1[1] > ymax - 0.0006) pick.Add(e);
            }
        }
        else if (edgesSpec.StartsWith("pt:"))
        {
            foreach (var pt in edgesSpec.Substring(3).Split(';'))
            {
                var c = pt.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture) / 1000.0).ToArray(); Edge best = null; double bd = 0.0005;
                foreach (var o in edges)
                {
                    var e = o as Edge; var cv = e.GetCurve() as Curve; if (cv == null) continue;
                    var cp = cv.GetClosestPointOn(c[0], c[1], c[2]) as double[]; if (cp == null) continue;
                    double d = Math.Sqrt((cp[0] - c[0]) * (cp[0] - c[0]) + (cp[1] - c[1]) * (cp[1] - c[1]) + (cp[2] - c[2]) * (cp[2] - c[2]));
                    if (d < bd) { bd = d; best = e; }
                }
                if (best != null && !pick.Contains(best)) pick.Add(best);
            }
        }
        sb.Append("edges chosen: " + pick.Count + " ;; ");
        if (pick.Count == 0) return sb.Append("NOTHING SELECTED: not calling InsertExtendSurface (an empty selection crashes SolidWorks)").ToString();
        doc.ClearSelection2(true);
        int sel = 0; foreach (var e in pick) if (((Entity)e).Select4(true, null)) sel++;
        sb.Append("selected: " + sel + " ;; ");
        if (sel == 0) return sb.Append("selection failed: not calling InsertExtendSurface").ToString();
        if (upto)
        {
            bool ok2 = doc.Extension.SelectByID2(uptoName, "PLANE", 0, 0, 0, true, uptoMark, null, 0);
            if (!ok2) ok2 = doc.Extension.SelectByID2(uptoName, "REFSURFACE", 0, 0, 0, true, uptoMark, null, 0);
            sb.Append("up-to target '" + uptoName + "' selected with mark " + uptoMark + ": " + ok2 + " ;; ");
            if (!ok2) return sb.Append("target not selected: not calling InsertExtendSurface").ToString();
        }
        int n0 = doc.GetFeatureCount();
        doc.InsertExtendSurface(linear, upto ? 2 : 0, dist);              // end condition 0 = distance (1 = up to point, 2 = up to surface)
        doc.ClearSelection2(true);
        Feature last = null; var f = doc.FirstFeature() as Feature;
        while (f != null) { if (f.Name.StartsWith("Surface-Extend")) last = f; f = f.GetNextFeature() as Feature; }
        bool w = false; int ec = last != null ? last.GetErrorCode2(out w) : -1;
        sb.Append("features " + n0 + " -> " + doc.GetFeatureCount() + " ;; feature: " + (last != null ? last.Name : "none") + " error " + ec + " ;; ");
        var arr2 = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
        if (arr2 != null && last != null) foreach (var o in arr2) { var b = o as Body2; if (b != null && b.Name == last.Name) sb.Append("after: " + Box(b) + " ;; "); }
        return sb.ToString();
    }
}
