// Probe 80: the SWEPT FLANGE through CreateDefinition(swFmSweptFlange = 117) -> ISweptFlangeFeatureData, with a STEP LOG written BEFORE every call (the process died on the first attempt, with sketch Features as
// Profile and Path, and the reply died with it). Empty scratch part. The path: a straight line along x on the Top Plane (0 .. 100 mm); the profile: an open L on the Right Plane at the start of the path
// (15 mm along y, 20 mm along z). arg = "variant;key=value;..."  with the variant chosen from the list below; log=<file> REQUIRED. Each variant says which object types Profile / Path get:
//   feat    Profile = the sketch FEATURE, Path = the sketch FEATURE            (the first attempt - crashed)
//   sketch  Profile = ISketch, Path = ISketch
//   seg     Profile = ISketch, Path = the path sketch's ISketchSegment
//   segs    Profile = ISketch, Path = an array (DispatchWrapper[]) of the path's segments
//   sel     nothing assigned: both sketches SELECTED (profile mark 1, path mark 2) before CreateFeature
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swAllBodies, false) as object[]; if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox();
        return ((x[3] - x[0]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " x " + ((x[4] - x[1]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " x " + ((x[5] - x[2]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " mm, " + b.GetFaceCount() + "f";
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : dflt; }
    static string Poly(ModelDoc2 doc, string plane, double[][] pts)
    {
        doc.ClearSelection2(true); doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); doc.SketchManager.InsertSketch(true);
        for (int i = 0; i + 1 < pts.Length; i++) doc.SketchManager.CreateLine(pts[i][0], pts[i][1], 0, pts[i + 1][0], pts[i + 1][1], 0);
        string name = ((Feature)((Sketch)doc.SketchManager.ActiveSketch)).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); return name;
    }

    static string Sketch(ModelDoc2 doc, string plane, Action draw)
    {
        doc.ClearSelection2(true); doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); doc.SketchManager.InsertSketch(true);
        draw(); string name = ((Feature)((Sketch)doc.SketchManager.ActiveSketch)).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); return name;
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var parts = (arg ?? "sketch").Split(';'); string variant = parts[0].Trim().ToLowerInvariant();
        foreach (var p in parts) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null; if (logPath == null) return "log=<file> is required (the reply is lost if SolidWorks dies)";
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager; double t = N(a, "t", 0.0015), r = N(a, "r", 0.0015);
        try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }        // sketches made by the caller may still be open
        L("variant " + variant + "; part: " + Size(doc));
        string pshape = a.ContainsKey("path") ? a["path"] : "line", fshape = a.ContainsKey("prof") ? a["prof"] : "L";
        string pathName = a.ContainsKey("pathsketch") ? a["pathsketch"] : pshape == "L" ? Poly(doc, "Top Plane", new[] { new[] { 0.0, 0.0 }, new[] { 0.1, 0.0 }, new[] { 0.1, -0.06 } })
            : pshape == "arc" ? Sketch(doc, "Top Plane", () => doc.SketchManager.CreateArc(0.0, -0.05, 0, 0.0, 0.0, 0, 0.05, -0.05, 0, 1))
            : Poly(doc, "Top Plane", new[] { new[] { 0.0, 0.0 }, new[] { 0.1, 0.0 } });
        string profName = a.ContainsKey("profsketch") ? a["profsketch"] : fshape == "U" ? Poly(doc, "Right Plane", new[] { new[] { 0.015, 0.02 }, new[] { 0.015, 0.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, 0.02 } })
            : fshape == "Z" ? Poly(doc, "Right Plane", new[] { new[] { 0.015, 0.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, 0.02 }, new[] { -0.01, 0.02 } })
            : fshape == "line" ? Poly(doc, "Right Plane", new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 0.02 } })
            : Poly(doc, "Right Plane", new[] { new[] { 0.015, 0.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, 0.02 } });
        L("path sketch " + pathName + ", profile sketch " + profName);
        var pathFeat = (Feature)((PartDoc)doc).FeatureByName(pathName); var profFeat = (Feature)((PartDoc)doc).FeatureByName(profName);
        var pathSk = (Sketch)pathFeat.GetSpecificFeature2(); var profSk = (Sketch)profFeat.GetSpecificFeature2();
        var before = new HashSet<string>(Top(doc).Select(x => x.Name));
        if (variant == "pathtype")      // which form of the Path does the definition recognise? (GetPathType != -1) - no CreateFeature here
        {
            var segs0 = pathSk.GetSketchSegments() as object[]; object seg0 = segs0[0];
            var forms = new List<KeyValuePair<string, Func<object>>> {
                new KeyValuePair<string, Func<object>>("Feature", () => pathFeat), new KeyValuePair<string, Func<object>>("ISketch", () => pathSk), new KeyValuePair<string, Func<object>>("segment", () => seg0),
                new KeyValuePair<string, Func<object>>("DispatchWrapper[] of the segment", () => new[] { new DispatchWrapper(seg0) }), new KeyValuePair<string, Func<object>>("object[] of the segment", () => new object[] { seg0 }),
                new KeyValuePair<string, Func<object>>("object[] of the Feature", () => new object[] { pathFeat }), new KeyValuePair<string, Func<object>>("DispatchWrapper[] of the Feature", () => new[] { new DispatchWrapper(pathFeat) }) };
            foreach (var form in forms)
            {
                L("path form: " + form.Key); var d0 = fm.CreateDefinition(117) as ISweptFlangeFeatureData;
                try { d0.Path = form.Value(); L("   set ok; GetPathType " + d0.GetPathType()); } catch (Exception e) { L("   THREW " + (e.InnerException != null ? e.InnerException : e).Message); }
            }
            L("profile forms:");
            foreach (var form in new List<KeyValuePair<string, Func<object>>> { new KeyValuePair<string, Func<object>>("Feature", () => profFeat), new KeyValuePair<string, Func<object>>("ISketch", () => profSk) })
            {
                var d0 = fm.CreateDefinition(117) as ISweptFlangeFeatureData;
                try { d0.Profile = form.Value(); object back = d0.Profile; L("   Profile = " + form.Key + ": set ok; read back " + (back == null ? "null" : back.GetType().Name)); } catch (Exception e) { L("   Profile = " + form.Key + " THREW " + (e.InnerException != null ? e.InnerException : e).Message); }
            }
            return sb.ToString();
        }
        L("about to CreateDefinition(117)"); var def = fm.CreateDefinition(117); var d = def as ISweptFlangeFeatureData; L("definition " + (def == null ? "null" : "ok") + ", typed " + (d != null));
        if (d == null) return sb.ToString();
        L("about to set OverrideDefaultSheetMetalParameters"); d.OverrideDefaultSheetMetalParameters = true;
        L("about to set Thickness"); d.Thickness = t;
        L("about to set UseDefaultRadius"); d.UseDefaultRadius = false;
        L("about to set BendRadius"); d.BendRadius = r;
        if (variant == "feat") { L("about to set Profile = Feature"); d.Profile = profFeat; L("about to set Path = Feature"); d.Path = pathFeat; }
        else if (variant == "pw1" || variant == "pw2" || variant == "pw3")      // Path = DispatchWrapper[] { path sketch FEATURE } - the one form that gives a path type (9)
        {
            L("about to set Path = DispatchWrapper[] { path feature }"); d.Path = new[] { new DispatchWrapper(pathFeat) };
            if (variant == "pw1") { L("about to set Profile = Feature"); d.Profile = profFeat; }
            if (variant == "pw2") { L("about to set Profile = ISketch"); d.Profile = profSk; }
            if (variant == "pw3") { L("about to set Profile = DispatchWrapper[] { profile feature }"); d.Profile = new[] { new DispatchWrapper(profFeat) }; }
            if (a.ContainsKey("pos")) { L("about to set FlangePosition"); d.FlangePosition = (int)N(a, "pos", 1); }
            if (a.ContainsKey("mi")) { L("about to set MaterialInside"); d.MaterialInside = N(a, "mi", 1) != 0; }
            if (a.ContainsKey("rev")) { L("about to set ReverseDirection"); d.ReverseDirection = N(a, "rev", 0) != 0; }
            if (a.ContainsKey("trim")) { L("about to set TrimSideBends"); d.TrimSideBends = N(a, "trim", 1) != 0; }
            if (a.ContainsKey("so")) { L("about to set StartOffset"); d.StartOffset = N(a, "so", 0); }
            if (a.ContainsKey("eo")) { L("about to set EndOffset"); d.EndOffset = N(a, "eo", 0); }
            if (a.ContainsKey("flat")) { L("about to set FlattenAlongPath"); d.FlattenAlongPath = N(a, "flat", 0) != 0; }
            if (a.ContainsKey("dbr")) { L("about to set UseDefaultBendRelief"); d.UseDefaultBendRelief = N(a, "dbr", 1) != 0; }
            if (a.ContainsKey("rt")) { L("about to set ReliefType"); d.ReliefType = (int)N(a, "rt", 0); }
        }
        else if (variant == "sketch") { L("about to set Profile = ISketch"); d.Profile = profSk; L("about to set Path = ISketch"); d.Path = pathSk; }
        else if (variant == "seg" || variant == "segs")
        {
            L("about to set Profile = ISketch"); d.Profile = profSk;
            var segs = pathSk.GetSketchSegments() as object[]; L("path segments: " + (segs == null ? "null" : segs.Length.ToString()));
            if (variant == "seg") { L("about to set Path = segment 0"); d.Path = segs[0]; } else { L("about to set Path = DispatchWrapper[] of the segments"); d.Path = segs.Select(o => new DispatchWrapper(o)).ToArray(); }
        }
        else if (variant == "sel")
        {
            int m1 = (int)N(a, "m1", 1), m2 = (int)N(a, "m2", 2); L("selecting the sketches (profile mark " + m1 + ", path mark " + m2 + ")"); doc.ClearSelection2(true);
            bool s1 = doc.Extension.SelectByID2(profName, "SKETCH", 0, 0, 0, false, m1, null, 0), s2 = doc.Extension.SelectByID2(pathName, "SKETCH", 0, 0, 0, true, m2, null, 0); L("selected " + s1 + ", " + s2);
        }
        else if (variant == "selseg")
        {
            // the profile sketch selected as a sketch (mark 1), the path SEGMENT selected (mark 2): by the sketch segment object
            doc.ClearSelection2(true); var sd1 = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); sd1.Mark = (int)N(a, "m1", 1); var sd2 = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); sd2.Mark = (int)N(a, "m2", 2);
            bool s1 = profFeat.Select2(false, sd1.Mark); var segs2 = pathSk.GetSketchSegments() as object[]; bool s2 = ((SketchSegment)segs2[0]).Select4(true, sd2); L("selected profile " + s1 + ", path segment " + s2);
        }
        L("about to GetPathType"); try { L("path type " + d.GetPathType()); } catch (Exception e) { L("GetPathType threw " + e.Message); }
        L("about to CreateFeature"); Feature f = null; string thrown = null;
        try { f = fm.CreateFeature(def) as Feature; } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
        L("CreateFeature returned " + (f == null ? "null" : f.Name + " [" + f.GetTypeName2() + "]") + (thrown != null ? "; THREW " + thrown : ""));
        var fresh = Top(doc).Where(x => !before.Contains(x.Name)).ToList(); L("new features: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(x => x.Name + "[" + x.GetTypeName2() + "]").ToArray())) + "; " + Size(doc));
        if (f != null) { bool w; L("error code " + f.GetErrorCode2(out w) + (w ? " (warning)" : "")); }
        return sb.ToString();
    }
}
