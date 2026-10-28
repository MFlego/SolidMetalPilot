// Probe 79: MOVE FACE through IFeatureManager.InsertMoveFace3(MoveType, ReverseDir, Angle, Distance, TranslationParams, RotationParams, EndConditionType, OffsetDistance) - the one feature of the user's closed-corner
// chassis chain (Closed Corner -> Move Face -> Corner-Trim) that is still driven by keyboard scripts. The earlier probe returned null with a wall face selected; the lesson of the lofted bend and the tab and slot is
// that the INPUTS decide, so this one sweeps the cheap ones on a plain block (made by the caller: a box on the Front Plane, e.g. 20 x 20 x 10 mm, top face at z = top): the selection mark of the face (0, 1, 2),
// the move type (offset, translate), the direction flag, the end condition, empty / null parameters. It STOPS at the first feature (a second creation on the same face right after a success killed SolidWorks once).
// arg = "mode;key=value;..."  mode: sweep | one.  keys: top=<z m of the top face>, dist (m), log=<file>; for "one": mark, type, rev, end, off, trans (0/1 = pass a translation triad), faces (n = 1 top | 4 = the four side faces).
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
    static string Box(ModelDoc2 doc)
    {
        var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox(); var mp = (double[])b.GetMassProperties(1.0);
        return ((x[3] - x[0]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " x " + ((x[4] - x[1]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " x " + ((x[5] - x[2]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm, " + b.GetFaceCount() + "f, vol " + (mp[3] * 1e9).ToString("F3", CultureInfo.InvariantCulture) + " mm3";
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : dflt; }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var parts = (arg ?? "sweep").Split(';'); string mode = parts[0].Trim().ToLowerInvariant();
        foreach (var p in parts) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null;
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager;
        double top = N(a, "top", 0.01), dist = N(a, "dist", 0.003); L("mode " + mode + "; part: " + Box(doc));

        Func<int, bool, Func<int, bool, Feature>> mk = null;
        Func<string, int, Func<Feature>, bool> attempt = (label, mark, create) =>
        {
            var before = new HashSet<string>(Top(doc).Select(x => x.Name)); string thrown = null; Feature f = null;
            doc.ClearSelection2(true);
            bool sel = doc.Extension.SelectByID2("", "FACE", 0.0, 0.0, top, false, mark, null, 0);
            if (a.ContainsKey("faces") && N(a, "faces", 1) >= 4)       // the four SIDE faces of the block, appended (x = +-10 mm and y = +-10 mm; z in the middle)
            {
                doc.ClearSelection2(true); double hz = top / 2, h = N(a, "half", 0.01);
                sel = doc.Extension.SelectByID2("", "FACE", h, 0.0, hz, false, mark, null, 0);
                sel &= doc.Extension.SelectByID2("", "FACE", -h, 0.0, hz, true, mark, null, 0); sel &= doc.Extension.SelectByID2("", "FACE", 0.0, h, hz, true, mark, null, 0); sel &= doc.Extension.SelectByID2("", "FACE", 0.0, -h, hz, true, mark, null, 0);
            }
            int picked = ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
            try { f = create(); } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
            var fresh = Top(doc).Where(x => !before.Contains(x.Name)).ToList();
            L(label + ": selected " + sel + " (" + picked + "); returned " + (f == null ? "null" : f.Name + " [" + f.GetTypeName2() + "]") + (thrown != null ? "; THREW " + thrown : "") + "; new: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(x => x.Name).ToArray())) + "; " + Box(doc));
            return f != null || fresh.Count > 0;
        };

        if (mode == "one")
        {
            int mark = (int)N(a, "mark", 1), type = (int)N(a, "type", 0), end = (int)N(a, "end", 0); bool rev = N(a, "rev", 0) != 0; double off = N(a, "off", 0.0);
            object trans = N(a, "trans", 0) != 0 ? (object)new double[] { 0, 0, 0, 0, 0, 1 } : null;
            object rot = null; int tp = (int)N(a, "tp", 0);
            if (tp == 1) trans = new double[] { 0, 0, 1 };                                     // a direction only
            if (tp == 2) trans = new double[] { 0, 0, 0, 0, 0, 1 };                            // a point and a direction
            if (tp == 3) trans = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 };           // a triad: origin + three axes
            if (tp == 4) trans = new double[] { 0, 0, 0, 0, 0, 1, 0, 0, 0 };                   // 9 values
            if (tp == 5) trans = new double[] { 0, 0, 0 };
            if (tp == 6) { trans = new double[] { 0, 0, 0 }; rot = new double[] { 0, 0, 0, 0, 0, 1 }; }
            if (tp == 7) { trans = null; rot = new double[] { 0, 0, 0, 0, 0, 1 }; }
            if (tp == 8) { trans = null; rot = new double[] { 0.01, 0, 0.01, 0, 1, 0 }; }       // an axis through (10, 0, 10) mm along y
            Feature made = null;
            attempt("InsertMoveFace3 type " + type + ", rev " + rev + ", dist " + dist * 1000 + " mm, end " + end + ", off " + off + ", trans " + (trans != null), mark, () => { made = fm.InsertMoveFace3(type, rev, N(a, "angle", 0.0) * Math.PI / 180, dist, trans, rot, end, off); return made; });
            if (made != null && N(a, "readback", 0) != 0)
            {
                bool w; L("error code " + made.GetErrorCode2(out w) + (w ? " (warning)" : ""));
                var dd = made.GetDefinition() as IMoveFaceFeatureData; L("GetDefinition -> " + (dd != null));
                if (dd != null)
                {
                    bool acc = dd.AccessSelections((ModelDoc2)doc, null); L("AccessSelections -> " + acc);
                    L("MoveType " + dd.MoveType + ", Distance " + (dd.Distance * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm, Reverse " + dd.ReverseDirection + ", Angle " + dd.Angle + ", EndCondition " + dd.EndCondition + ", OffsetDistance " + dd.OffsetDistance + ", faces " + dd.GetFacesCount());
                    dd.ReleaseSelectionAccess();
                }
                var dim = made.GetFirstDisplayDimension() as DisplayDimension; int k = 0;
                while (dim != null && k++ < 10)
                {
                    var d = dim.GetDimension2(0); string txt = "?";
                    if (d != null) { var vv = d.GetValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[]; txt = d.FullName + " = " + (vv == null ? "?" : (vv[0] * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm"); }
                    L("dimension " + txt); dim = made.GetNextDisplayDimension(dim) as DisplayDimension;
                }
            }
            return sb.ToString();
        }

        bool done = false;
        foreach (int mark in new[] { 1, 0, 2 })
        {
            if (done) break;
            foreach (int end in new[] { 0, 1, 2 })
            {
                if (done) break;
                foreach (bool rev in new[] { false, true })
                {
                    if (done) break;
                    done = attempt("InsertMoveFace3 offset: mark " + mark + ", end " + end + ", rev " + rev, mark, () => fm.InsertMoveFace3(0, rev, 0.0, dist, null, null, end, 0.0));
                }
            }
            if (!done) done = attempt("InsertMoveFace2 offset: mark " + mark, mark, () => fm.InsertMoveFace2(0, false, 0.0, dist, null, null));
            if (!done) done = attempt("InsertMoveFace offset: mark " + mark, mark, () => fm.InsertMoveFace(0, false, 0.0, dist));
            if (!done) done = attempt("InsertMoveFace3 translate (triad z): mark " + mark, mark, () => fm.InsertMoveFace3(1, false, 0.0, dist, new double[] { 0, 0, 0, 0, 0, 1 }, null, 0, 0.0));
        }
        L(done ? "BUILT" : "nothing built");
        return sb.ToString();
    }
}
