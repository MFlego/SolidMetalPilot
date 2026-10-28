// Probe 78: the LOFTED BEND again, with the simplest open profiles there are - ONE ARC in each of two sketches on parallel planes (a half circle of radius R1 on the Front Plane, one of radius R2 on the named
// offset plane) - and a U of lines + tangent arcs, with every variant of the call that costs nothing to try. arg = "mode;key=value;..."  mode: arcs | u
// keys: plane2=<name> (default Plane1), r1, r2 (m), t (thickness m), log=<file>. Reports per attempt: how many objects were selected, what came back, what is new in the tree, the body box.
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
        var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swAllBodies, false) as object[]; if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox();
        return ((x[3] - x[0]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " x " + ((x[4] - x[1]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " x " + ((x[5] - x[2]) * 1000).ToString("F2", CultureInfo.InvariantCulture) + " mm, " + b.GetFaceCount() + "f, " + bs.Length + " bodies";
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : dflt; }

    // a sketch on a named plane: the caller draws inside; returns the sketch name
    static string Sketch(ModelDoc2 doc, string plane, Action draw)
    {
        doc.ClearSelection2(true); doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); doc.SketchManager.InsertSketch(true);
        draw(); string name = ((Feature)((Sketch)doc.SketchManager.ActiveSketch)).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); return name;
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var parts = (arg ?? "arcs").Split(';'); string mode = parts[0].Trim().ToLowerInvariant();
        foreach (var p in parts) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null;
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager; var sm = doc.SketchManager;
        double r1 = N(a, "r1", 0.02), r2 = N(a, "r2", 0.01), t = N(a, "t", 0.0015), rad = N(a, "r", 0.0015); string plane2 = a.ContainsKey("plane2") ? a["plane2"] : "Plane1";
        L("mode " + mode + "; part: " + Size(doc));
        string s1, s2;
        if (mode == "arcs")
        {
            s1 = Sketch(doc, "Front Plane", () => sm.CreateArc(0, 0, 0, -r1, 0, 0, r1, 0, 0, 1));
            s2 = Sketch(doc, plane2, () => sm.CreateArc(0, 0, 0, -r2, 0, 0, r2, 0, 0, 1));
        }
        else if (mode == "u")
        {
            // a U: two vertical lines joined by a half circle (a slot without its top), 40 x 30 mm and 20 x 15 mm
            Func<double, double, Action> u = (w, h) => () => { double r = w; sm.CreateLine(-r, h, 0, -r, 0, 0); sm.CreateArc(0, 0, 0, -r, 0, 0, r, 0, 0, 1); sm.CreateLine(r, 0, 0, r, h, 0); };      // the arc runs from (-r, 0) to (r, 0) counter-clockwise = through the bottom (0, -r)
            s1 = Sketch(doc, "Front Plane", u(r1, 0.03)); s2 = Sketch(doc, plane2, u(r2, 0.015));
        }
        else if (mode == "l2" || mode == "lines3" || mode == "lines4" || mode == "line1" || mode == "spline")
        {
            Func<double, Action> shape = k => () =>
            {
                switch (mode)
                {
                    case "l2": sm.CreateLine(-k, k, 0, -k, 0, 0); sm.CreateLine(-k, 0, 0, k, 0, 0); break;                                                     // an L
                    case "lines3": sm.CreateLine(-k, k, 0, -k, 0, 0); sm.CreateLine(-k, 0, 0, k, 0, 0); sm.CreateLine(k, 0, 0, k, k, 0); break;             // a square U
                    case "lines4": sm.CreateLine(-k, k, 0, -k, 0, 0); sm.CreateLine(-k, 0, 0, k, 0, 0); sm.CreateLine(k, 0, 0, k, k, 0); sm.CreateLine(k, k, 0, k * 0.4, k, 0); break;
                    case "line1": sm.CreateLine(-k, 0, 0, k, 0, 0); break;                                                                                     // a single straight line
                    case "spline": { var pts = new double[] { -k, k, 0, -k * 0.6, 0.1 * k, 0, 0, 0, 0, k * 0.6, 0.1 * k, 0, k, k, 0 }; sm.CreateSpline(pts); break; }
                }
            };
            s1 = Sketch(doc, "Front Plane", shape(r1)); s2 = Sketch(doc, plane2, shape(r2));
        }
        else throw new Exception("unknown mode " + mode);
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
        bool done = false;
        if (a.ContainsKey("facet"))      // ONE call with every argument from the keys (facet, dir, chord, nb, seglen, segang, formed, refend, mark) - for the loops of the caller
        {
            int mark = (int)N(a, "mark", 0), dirType = (int)N(a, "dir", 0), facet = (int)N(a, "facet", 0), nb = (int)N(a, "nb", 4); bool formed = N(a, "formed", 0) != 0, refend = N(a, "refend", 0) != 0;
            Action sel1 = () => { doc.Extension.SelectByID2(s1, "SKETCH", 0, 0, 0, false, mark, null, 0); doc.Extension.SelectByID2(s2, "SKETCH", 0, 0, 0, true, mark, null, 0); };
            attempt("lofted bend 2: facet " + facet + ", dir " + dirType + ", formed " + formed + ", refend " + refend + ", chord " + N(a, "chord", 0.0001) + ", nb " + nb + ", seglen " + N(a, "seglen", 0.01) + ", segang " + N(a, "segang", 0.1), sel1,
                () => fm.InsertSheetMetalLoftedBend2(dirType, t, formed, rad, refend, facet, N(a, "chord", 0.0001), nb, N(a, "seglen", 0.01), N(a, "segang", 0.1)));
            return sb.ToString();
        }
        foreach (int mark in new[] { 0, 1, 2 })
        {
            if (done) break;
            Action sel = () => { doc.Extension.SelectByID2(s1, "SKETCH", 0, 0, 0, false, mark, null, 0); doc.Extension.SelectByID2(s2, "SKETCH", 0, 0, 0, true, mark, null, 0); };
            foreach (int dirType in new[] { 0, 1 })
                foreach (int facet in new[] { 0, 1, 2 })
                {
                    if (done) break;
                    done = attempt("lofted bend 2: mark " + mark + ", thick dir " + dirType + ", facet option " + facet, sel, () => fm.InsertSheetMetalLoftedBend2(dirType, t, false, rad, false, facet, 0.0001, 4, 0.01, 0.1));
                }
            if (!done) done = attempt("lofted bend 1: mark " + mark, sel, () => fm.InsertSheetMetalLoftedBend(0, t));
        }
        if (!done)
        {
            // second marks apart: profile 1 mark 1, profile 2 mark 2 (as a loft's profiles and guide curves)
            Action sel2 = () => { doc.Extension.SelectByID2(s1, "SKETCH", 0, 0, 0, false, 1, null, 0); doc.Extension.SelectByID2(s2, "SKETCH", 0, 0, 0, true, 2, null, 0); };
            done = attempt("lofted bend 2: marks 1 and 2", sel2, () => fm.InsertSheetMetalLoftedBend2(0, t, false, rad, false, 0, 0.0001, 4, 0.01, 0.1));
        }
        L(done ? "BUILT" : "nothing built");
        return sb.ToString();
    }
}
