// Probe 59: every body (solid and surface) of the active part with its box, visibility and face count, with the feature tree ROLLED BACK to just after a named feature (restored at the end), plus the
// features that exist at that point. arg = "after=Mirror14".
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
    static string F(double v) { return (v * 1000).ToString("0.###", CultureInfo.InvariantCulture); }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager; bool rolled = false;
        string after = null; foreach (var kv in (arg ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2 && p[0].Trim() == "after") after = p[1].Trim(); }
        if (after != null) { rolled = fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, after); Add("rollback to after " + after + ": " + rolled); }
        try
        {
            var all = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
            Add("bodies: " + (all == null ? 0 : all.Length));
            if (all != null)
                foreach (Body2 b in all)
                {
                    var x = (double[])b.GetBodyBox();
                    Add("  " + (b.GetType() == (int)swBodyType_e.swSolidBody ? "SOLID" : "SHEET") + " visible=" + b.Visible + " faces=" + b.GetFaceCount() + " box mm [" + F(x[0]) + "," + F(x[1]) + "," + F(x[2]) + "] .. [" + F(x[3]) + "," + F(x[4]) + "," + F(x[5]) + "]");
                }
            var f = (Feature)doc.FirstFeature(); var names = new List<string>();
            while (f != null) { string t = f.GetTypeName2(); if (t == "SurfCut" || t == "ExtruRefSurface" || t == "MirrorPattern" || t == "Fillet" || t == "UnFold" || t == "Fold" || t == "ProfileFeature") names.Add(f.Name + "[" + t + (f.IsSuppressed() ? ",suppressed" : "") + "]"); f = (Feature)f.GetNextFeature(); }
            Add("features: " + string.Join(" ", names.ToArray()));
        }
        finally { if (rolled) fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); }
        return sb.ToString();
    }
}
