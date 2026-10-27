// Probe 66: sample ONE edge of the solid body at fractions of its length - used to compare an unfolded corner curve with a hand-drawn trim spline. The edge is the one whose end points are nearest to the two
// points in arg. arg = "a=269.938,211.381;b=275.907,207.929;n=8;after=Fillet1;z=0" (mm; the rollback is optional and restored).
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
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static double[] P(string s) { return s.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture) / 1000).ToArray(); }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager; bool rolled = false;
        if (a.ContainsKey("after")) rolled = fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, a["after"]);
        try
        {
            var pa = P(a["a"]); var pb = P(a["b"]); double z = a.ContainsKey("z") ? double.Parse(a["z"], CultureInfo.InvariantCulture) / 1000 : 0; int n = a.ContainsKey("n") ? int.Parse(a["n"]) : 8;
            Edge best = null; double bd = double.MaxValue;
            foreach (Body2 b in (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, true))
                foreach (Edge e in (object[])b.GetEdges())
                {
                    var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per); var p0 = (double[])c.Evaluate2(s, 0); var p1 = (double[])c.Evaluate2(en, 0);
                    if (Math.Abs(p0[2] - z) > 1e-6 || Math.Abs(p1[2] - z) > 1e-6) continue;
                    double d1 = Math.Abs(p0[0] - pa[0]) + Math.Abs(p0[1] - pa[1]) + Math.Abs(p1[0] - pb[0]) + Math.Abs(p1[1] - pb[1]), d2 = Math.Abs(p1[0] - pa[0]) + Math.Abs(p1[1] - pa[1]) + Math.Abs(p0[0] - pb[0]) + Math.Abs(p0[1] - pb[1]);
                    double d = Math.Min(d1, d2); if (d < bd) { bd = d; best = e; }
                }
            if (best == null || bd > 5e-5) return "no edge between the two points (nearest " + (bd * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm)";
            var cv = (Curve)best.GetCurve(); double t0, t1; bool cl2, per2; cv.GetEndParams(out t0, out t1, out cl2, out per2); double len = cv.GetLength3(t0, t1);
            Add("edge length " + (len * 1000).ToString("0.####", CultureInfo.InvariantCulture) + " mm, params " + t0.ToString("R", CultureInfo.InvariantCulture) + " .. " + t1.ToString("R", CultureInfo.InvariantCulture));
            for (int k = 0; k <= n; k++)
            {
                double u = t0 + (t1 - t0) * k / n; var q = (double[])cv.Evaluate2(u, 0);
                Add("  u " + (k / (double)n).ToString("0.###", CultureInfo.InvariantCulture) + ": (" + (q[0] * 1000).ToString("0.####", CultureInfo.InvariantCulture) + ", " + (q[1] * 1000).ToString("0.####", CultureInfo.InvariantCulture) + ")");
            }
        }
        finally { if (rolled) fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); }
        return sb.ToString();
    }
}
