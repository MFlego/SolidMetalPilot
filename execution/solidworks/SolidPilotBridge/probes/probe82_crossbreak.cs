// Probe 82: the CROSS-BREAK again, with the lesson of the other features (the inputs decide). The earlier attempts made a Cross-Break feature that changed nothing: the lines were in a sketch on the Front PLANE
// (z = 0, the same place as the plate's top face but not attached to the face). Here the sketch is the caller's (made ON THE FACE with create_sketch on_face, or on a plane), the selection order and marks vary,
// and everything is reported: new features, the body's faces / box / volume, the flat pattern, and the definition read back (ICrossBreakFeatureData: BreakAngle, BreakRadius, ReverseDirection, Face).
// arg = "key=value;..."  sketch=<name>  angle=<deg>  radius=<m>  order=sf|fs|s  smark=<n>  fmark=<n>  fz=<z of the face to select (m)>  log=<file>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder(); static string logPath = null;
    static void L(string s) { sb.Append(s).Append(" ;; "); if (logPath != null) { try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); } catch { } } }
    static List<Feature> Top(ModelDoc2 doc) { var l = new List<Feature>(); var f = (Feature)doc.FirstFeature(); while (f != null) { l.Add(f); f = (Feature)f.GetNextFeature(); } return l; }
    static string Size(ModelDoc2 doc)
    {
        var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox(); var mp = (double[])b.GetMassProperties(1.0);
        return ((x[3] - x[0]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " x " + ((x[4] - x[1]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " x " + ((x[5] - x[2]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm, " + b.GetFaceCount() + "f, vol " + (mp[3] * 1e9).ToString("F3", CultureInfo.InvariantCulture) + " mm3";
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : dflt; }
    static Face2 FaceAt(ModelDoc2 doc, double x, double y, double z)
    {
        Face2 best = null; double bd = 1e9; var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs == null || bs.Length == 0) return null;
        foreach (Face2 f in (object[])((Body2)bs[0]).GetFaces())
        {
            var q = f.GetClosestPointOn(x, y, z) as double[]; if (q == null || q.Length < 3) continue;
            double d = Math.Sqrt((q[0] - x) * (q[0] - x) + (q[1] - y) * (q[1] - y) + (q[2] - z) * (q[2] - z)); if (d < bd) { bd = d; best = f; }
        }
        return bd < 1e-4 ? best : null;
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (arg ?? "").Split(';')) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null;
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager;
        try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
        string sk = a.ContainsKey("sketch") ? a["sketch"] : null; if (sk == null) return "sketch=<name> is required";
        double ang = N(a, "angle", 3.0) * Math.PI / 180, rad = N(a, "radius", 0.003), fz = N(a, "fz", 0.0); string order = a.ContainsKey("order") ? a["order"] : "sf"; int sm = (int)N(a, "smark", 0), fmk = (int)N(a, "fmark", 0);
        L("part before: " + Size(doc));
        var before = new HashSet<string>(Top(doc).Select(x => x.Name)); doc.ClearSelection2(true);
        var sdS = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); sdS.Mark = sm; var sdF = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); sdF.Mark = fmk;
        var face = FaceAt(doc, N(a, "fx", 0.02), N(a, "fy", 0.0), fz); L("face found: " + (face != null));
        Action selSketch = () => { var f = (Feature)((PartDoc)doc).FeatureByName(sk); L("sketch selected: " + (f != null && f.Select2(order == "fs", sm))); };
        Action selFace = () => { if (face != null) L("face selected: " + ((Entity)face).Select4(order == "sf", sdF)); };
        if (order == "fs") { selFace(); selSketch(); } else { selSketch(); if (order == "sf") selFace(); }
        L("selection count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));
        Feature made = null; string thrown = null;
        try { made = fm.InsertCrossBreak(ang, rad) as Feature; } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
        doc.ClearSelection2(true);
        L("InsertCrossBreak returned " + (made == null ? "null" : made.Name + " [" + made.GetTypeName2() + "]") + (thrown != null ? "; THREW " + thrown : ""));
        var fresh = Top(doc).Where(x => !before.Contains(x.Name)).ToList(); L("new features: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(x => x.Name + "[" + x.GetTypeName2() + "]").ToArray())));
        L("part after: " + Size(doc));
        if (made != null)
        {
            bool w; L("error code " + made.GetErrorCode2(out w) + (w ? " (warning)" : ""));
            var d = made.GetDefinition() as ICrossBreakFeatureData;
            if (d != null)
            {
                bool acc = d.AccessSelections(doc, null); L("definition: BreakAngle " + (d.BreakAngle * 180 / Math.PI).ToString("F3", CultureInfo.InvariantCulture) + " deg, BreakRadius " + (d.BreakRadius * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm, Reverse " + d.ReverseDirection + ", Face " + (d.Face != null));
                d.ReleaseSelectionAccess();
            }
        }
        return sb.ToString();
    }
}
