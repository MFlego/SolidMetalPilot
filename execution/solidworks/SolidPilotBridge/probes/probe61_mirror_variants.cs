// Probe 61: how many faces does a MIRROR of a surface cut own, for different InsertMirrorFeature2 settings? Run on a SCRATCH COPY of the v4 reference: suppress the reference's own mirror, roll the bar back to
// after the cut, mirror the cut about the Right Plane with the settings in arg, report the faces the new mirror owns, and delete it again.
// arg = "cut=SurfaceCut1;mirror=Mirror14;plane=Right Plane;body=0|1;geom=0|1;merge=0|1;knit=0|1;scope=0|1|2;selbody=0|1"
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
    static Dictionary<string, string> Parse(string a) { var d = new Dictionary<string, string>(); foreach (var kv in (a ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2) d[p[0].Trim()] = p[1].Trim(); } return d; }
    static string G(Dictionary<string, string> a, string k, string dflt) { return a.ContainsKey(k) ? a[k] : dflt; }
    static HashSet<string> Names(ModelDoc2 doc) { var h = new HashSet<string>(); var f = (Feature)doc.FirstFeature(); while (f != null) { h.Add(f.Name); f = (Feature)f.GetNextFeature(); } return h; }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = Parse(arg); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager;
        string cutName = G(a, "cut", "SurfaceCut1"), mirrorName = G(a, "mirror", "Mirror14"), plane = G(a, "plane", "Right Plane");
        bool body = G(a, "body", "0") == "1", geom = G(a, "geom", "0") == "1", merge = G(a, "merge", "0") == "1", knit = G(a, "knit", "0") == "1", selbody = G(a, "selbody", "0") == "1"; int scope = int.Parse(G(a, "scope", "0"));
        Feature cut = null, mir = null; var f = (Feature)doc.FirstFeature();
        while (f != null) { if (f.Name == cutName) cut = f; if (f.Name == mirrorName) mir = f; f = (Feature)f.GetNextFeature(); }
        if (cut == null) return "no feature " + cutName;
        if (mir != null) Add("suppress the reference mirror: " + mir.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null));
        bool rolled = fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, cutName); Add("rollback to after " + cutName + ": " + rolled);
        try
        {
            var names = Names(doc); doc.ClearSelection2(true);
            bool s1 = cut.Select2(false, 1); bool s2 = doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, true, 2, null, 0); bool s3 = true;
            if (selbody) { var bs = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs != null && bs.Length > 0) s3 = ((Body2)bs[0]).Select2(true, null); }
            Add("selected: cut " + s1 + ", plane " + s2 + (selbody ? ", body " + s3 : "") + "; count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));
            Feature nf = fm.InsertMirrorFeature2(body, geom, merge, knit, scope) as Feature;
            Add("InsertMirrorFeature2(body " + body + ", geom " + geom + ", merge " + merge + ", knit " + knit + ", scope " + scope + ") -> " + (nf == null ? "null" : nf.Name));
            doc.ClearSelection2(true); doc.EditRebuild3();
            if (nf != null)
            {
                var faces = nf.GetFaces() as object[]; Add("the new mirror owns " + (faces == null ? 0 : faces.Length) + " faces");
                if (faces != null) foreach (Face2 fa in faces) { var bx = (double[])fa.GetBox(); Add("   face centre (" + F((bx[0] + bx[3]) / 2) + "," + F((bx[1] + bx[4]) / 2) + ") area " + (fa.GetArea() * 1e6).ToString("0.###", CultureInfo.InvariantCulture)); }
                doc.ClearSelection2(true); nf.Select2(false, 0); doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed); doc.ClearSelection2(true);
            }
        }
        finally { if (rolled) fm.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); }
        return sb.ToString();
    }
}
