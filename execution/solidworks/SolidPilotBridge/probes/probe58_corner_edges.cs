// Probe 58: the edges of the solid body inside a window of the XY plane (mm), optionally with the feature tree ROLLED BACK to just after a named feature (restored at the end) - to compare the corner
// geometry of two sheet-metal models stage by stage. arg = "after=Mirror14;x=273;y=209;r=8;zmin=-1;zmax=2" (all optional; r = half width of the square window in mm).
// Output per edge: curve kind, start, end, length (mm), z range; faces are summarised by count.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static Dictionary<string, string> Parse(string a) { var d = new Dictionary<string, string>(); foreach (var kv in (a ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2) d[p[0].Trim()] = p[1].Trim(); } return d; }
    static string F(double v) { return (v * 1000).ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Dictionary<string, string> a, string k, double dflt) { return a.ContainsKey(k) ? double.Parse(a[k], CultureInfo.InvariantCulture) : dflt; }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = Parse(arg); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager;
        bool rolled = false;
        if (a.ContainsKey("after")) { rolled = fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, a["after"]); Add("rollback to after " + a["after"] + ": " + rolled); }
        try
        {
            double cx = D(a, "x", 273) / 1000, cy = D(a, "y", 209) / 1000, r = D(a, "r", 8) / 1000, zmin = D(a, "zmin", -1e6) / 1000, zmax = D(a, "zmax", 1e6) / 1000;
            var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[]; if (bodies == null) return "no solid bodies";
            Add("solid bodies: " + bodies.Length);
            var rows = new List<string>();
            foreach (Body2 b in bodies)
            {
                Add("body faces " + b.GetFaceCount() + ", edges " + b.GetEdgeCount());
                foreach (Edge e in (object[])b.GetEdges())
                {
                    var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per);
                    var p0 = (double[])c.Evaluate2(s, 0); var p1 = (double[])c.Evaluate2(en, 0); var pm = (double[])c.Evaluate2((s + en) / 2, 0);
                    bool inWin = Math.Abs(pm[0] - cx) <= r && Math.Abs(pm[1] - cy) <= r && pm[2] >= zmin && pm[2] <= zmax;
                    if (!inWin) continue;
                    string kind = c.IsLine() ? "line" : c.IsCircle() ? "arc" : "curve";
                    double len = c.GetLength3(s, en);
                    var d0 = (double[])c.Evaluate2(s, 1); var d1 = (double[])c.Evaluate2(en, 1); double n0 = Math.Sqrt(d0[3] * d0[3] + d0[4] * d0[4] + d0[5] * d0[5]), n1 = Math.Sqrt(d1[3] * d1[3] + d1[4] * d1[4] + d1[5] * d1[5]);
                    string tan = n0 > 0 && n1 > 0 ? string.Format(CultureInfo.InvariantCulture, " tangent start ({0:0.####},{1:0.####}) end ({2:0.####},{3:0.####})", d0[3] / n0, d0[4] / n0, d1[3] / n1, d1[4] / n1) : "";
                    rows.Add(string.Format(CultureInfo.InvariantCulture, "{0,-5} ({1},{2},{3}) -> ({4},{5},{6}) len {7}{8}", kind, F(p0[0]), F(p0[1]), F(p0[2]), F(p1[0]), F(p1[1]), F(p1[2]), F(len), tan));
                }
            }
            rows.Sort(); foreach (var row in rows) Add(row);
            Add("edges in the window: " + rows.Count);
        }
        finally { if (rolled) fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); }
        return sb.ToString();
    }
}
