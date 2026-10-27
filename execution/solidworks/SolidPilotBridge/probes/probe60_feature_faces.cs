// Probe 60: the faces a feature OWNS (IFeature.GetFaces) - where did the feature actually change the body? Per face: centre of its box (mm), area (mm2), surface kind. arg = "feature=Mirror14[;rollback=1]"
// (rollback=1 puts the rollback bar just after the feature while reading, and restores it).
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
        sb.Clear(); var doc = (ModelDoc2)sw.ActiveDoc; var fm = doc.FeatureManager; string name = null; bool roll = false;
        foreach (var kv in (arg ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2) { if (p[0].Trim() == "feature") name = p[1].Trim(); if (p[0].Trim() == "rollback") roll = p[1].Trim() == "1"; } }
        Feature f = (Feature)doc.FirstFeature(); while (f != null && f.Name != name) f = (Feature)f.GetNextFeature();
        if (f == null) return "no feature named " + name;
        bool rolled = false; if (roll) rolled = fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, name);
        try
        {
            Add(name + " [" + f.GetTypeName2() + "] suppressed=" + f.IsSuppressed());
            var faces = f.GetFaces() as object[]; Add("faces owned: " + (faces == null ? 0 : faces.Length));
            if (faces != null)
                foreach (Face2 fa in faces)
                {
                    var bx = (double[])fa.GetBox(); var s = (Surface)fa.GetSurface();
                    string kind = s.IsPlane() ? "plane" : s.IsCylinder() ? "cylinder" : s.IsCone() ? "cone" : s.IsSphere() ? "sphere" : s.IsTorus() ? "torus" : "other";
                    Add("  face centre (" + F((bx[0] + bx[3]) / 2) + "," + F((bx[1] + bx[4]) / 2) + "," + F((bx[2] + bx[5]) / 2) + ") size (" + F(bx[3] - bx[0]) + "," + F(bx[4] - bx[1]) + "," + F(bx[5] - bx[2]) + ") area " + (fa.GetArea() * 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " " + kind);
                }
        }
        finally { if (rolled) fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); }
        return sb.ToString();
    }
}
