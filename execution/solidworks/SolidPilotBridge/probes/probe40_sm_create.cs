// Probe 40: sheet-metal CREATION calls on the tray fixture (sm_fixture.ps1: 100 x 60 mm base, 1.5 mm, R1.5, four 25 mm flanges, open corners): which selection each call needs, what it builds, what its definition reads.
// Each attempt selects, calls, reports the NEW features (+ error codes, body size before/after, the definition dump) and deletes what it created, so attempts do not pile up.   arg = mode:
//   hem | breakcorner | moveface | all-reads
// Tray geometry (mm): the +y wall is x -50..50, y 28.5..30, z up to 25; its outer top edge is (0, 30, 25); its outer face centre is (0, 30, 12.5); the +x wall mirrors it at x = 50.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void L(string s) { sb.Append(s).Append(" ;; "); }
    static string Fmt(object v)
    {
        if (v == null) return "null"; var arr = v as Array;
        if (arr != null) { var parts = new List<string>(); int n = 0; foreach (var o in arr) { if (n++ >= 12) { parts.Add("..."); break; } parts.Add(Fmt(o)); } return "[" + string.Join(", ", parts.ToArray()) + "]"; }
        if (v is double) return ((double)v).ToString("G6"); if (v is string) return "'" + v + "'";
        if (v.GetType().IsCOMObject) { var f = v as Feature; return f != null ? "Feature(" + f.Name + ")" : "COM"; }
        return v.ToString();
    }
    static string Size(ModelDoc2 doc)
    {
        var bs = (object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false); if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox();
        return ((x[3] - x[0]) * 1000).ToString("F2") + " x " + ((x[4] - x[1]) * 1000).ToString("F2") + " x " + ((x[5] - x[2]) * 1000).ToString("F2") + " mm, " + b.GetFaceCount() + "f/" + b.GetEdgeCount() + "e" + (bs.Length > 1 ? ", " + bs.Length + " bodies" : "");
    }
    static List<Feature> Top(ModelDoc2 doc) { var l = new List<Feature>(); var f = (Feature)doc.FirstFeature(); while (f != null) { l.Add(f); f = (Feature)f.GetNextFeature(); } return l; }
    static HashSet<string> Keys(ModelDoc2 doc)
    {
        var h = new HashSet<string>();
        foreach (var f in Top(doc)) { h.Add(f.Name + "|" + f.GetTypeName2()); var s = (Feature)f.GetFirstSubFeature(); while (s != null) { h.Add(s.Name + "|" + s.GetTypeName2()); s = (Feature)s.GetNextSubFeature(); } }
        return h;
    }
    // VIEW-INDEPENDENT picking. SelectByID2(x,y,z) picks through the CURRENT VIEW: in the wrong orientation it returned another edge, or failed on every second append pick. So edges and faces are found
    // by geometry (nearest line edge / closest point on a face) and selected as objects with Select4, which never depends on the view.
    static Body2 Body0(ModelDoc2 doc) { return (Body2)((object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false))[0]; }
    static Edge EdgeAt(ModelDoc2 doc, double x, double y, double z)
    {
        Edge best = null; double bd = 1e9;
        foreach (Edge e in (object[])Body0(doc).GetEdges())
        {
            try
            {
                if (!((Curve)e.GetCurve()).IsLine()) continue;
                var a = (double[])((Vertex)e.GetStartVertex()).GetPoint(); var b = (double[])((Vertex)e.GetEndVertex()).GetPoint();
                double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2], l2 = dx * dx + dy * dy + dz * dz; if (l2 < 1e-18) continue;
                double t = Math.Max(0, Math.Min(1, ((x - a[0]) * dx + (y - a[1]) * dy + (z - a[2]) * dz) / l2));
                double px = a[0] + t * dx - x, py = a[1] + t * dy - y, pz = a[2] + t * dz - z, d = Math.Sqrt(px * px + py * py + pz * pz);
                if (d < bd) { bd = d; best = e; }
            }
            catch { }
        }
        return bd < 1e-4 ? best : null;
    }
    static Face2 FaceAt(ModelDoc2 doc, double x, double y, double z)
    {
        Face2 best = null; double bd = 1e9;
        foreach (Face2 f in (object[])Body0(doc).GetFaces())
        {
            var q = f.GetClosestPointOn(x, y, z) as double[]; if (q == null || q.Length < 3) continue;
            double d = Math.Sqrt((q[0] - x) * (q[0] - x) + (q[1] - y) * (q[1] - y) + (q[2] - z) * (q[2] - z)); if (d < bd) { bd = d; best = f; }
        }
        return bd < 1e-4 ? best : null;
    }
    static bool Sel(ModelDoc2 doc, string type, double x, double y, double z, bool append, int mark)
    {
        var sd = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = mark;
        if (type == "EDGE") { var e = EdgeAt(doc, x, y, z); return e != null && ((Entity)e).Select4(append, sd); }
        if (type == "FACE") { var f = FaceAt(doc, x, y, z); return f != null && ((Entity)f).Select4(append, sd); }
        return doc.Extension.SelectByID2("", type, x, y, z, append, mark, null, 0);
    }

    // the most specific *FeatureData interface the definition answers to, dumped as Name=value (rw / ro)
    static string DumpDef(Feature f, ModelDoc2 doc)
    {
        object def = null; try { def = f.GetDefinition(); } catch { }
        if (def == null) return "(no definition)";
        Type best = null; int score = -1;
        foreach (var it in typeof(Feature).Assembly.GetTypes())
        {
            if (!it.IsInterface || it.Name.IndexOf("FeatureData", StringComparison.Ordinal) < 0 || !it.IsInstanceOfType(def)) continue;
            int sc = it.GetProperties().Length; if (sc > score) { best = it; score = sc; }
        }
        if (best == null) return "(no FeatureData interface)";
        bool acc = false; try { var m = best.GetMethod("AccessSelections"); if (m != null) acc = (bool)m.Invoke(def, new object[] { doc, null }); } catch { }
        var parts = new List<string>();
        foreach (var p in best.GetProperties().OrderBy(x => x.Name))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue; string val;
            try { val = Fmt(p.GetValue(def, null)); } catch (Exception e) { val = "!" + (e.InnerException != null ? e.InnerException.Message : e.Message); }
            parts.Add(p.Name + (p.CanWrite ? "" : "(ro)") + "=" + val);
        }
        if (acc) { try { var m = best.GetMethod("ReleaseSelectionAccess"); if (m != null) m.Invoke(def, null); } catch { } }
        return best.Name + ": " + string.Join("  ", parts.ToArray());
    }
    static void Remove(ModelDoc2 doc, Feature f)
    {
        doc.ClearSelection2(true);
        if (f.Select2(false, 0)) doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed | (int)swDeleteSelectionOptions_e.swDelete_Children);
        doc.ClearSelection2(true);
    }

    static void Attempt(ModelDoc2 doc, string label, Action select, Func<object> create)
    {
        var before = Keys(doc); string sizeBefore = Size(doc);
        doc.ClearSelection2(true);
        try { select(); } catch (Exception e) { L(label + ": select threw " + e.Message); return; }
        int picked = ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
        object ret = null; string thrown = null;
        try { ret = create(); } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
        var fresh = Top(doc).Where(f => !before.Contains(f.Name + "|" + f.GetTypeName2())).ToList();
        var freshSubs = new List<string>(); foreach (var f in Top(doc)) { var s = (Feature)f.GetFirstSubFeature(); while (s != null) { var k = s.Name + "|" + s.GetTypeName2(); if (!before.Contains(k)) freshSubs.Add(k); s = (Feature)s.GetNextSubFeature(); } }
        L(label + ": picked " + picked + "; returned " + (ret == null ? "null/void" : ret is Feature ? ((Feature)ret).Name : ret.ToString()) + (thrown != null ? "; THREW " + thrown : "") + "; new: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(f => f.Name + "[" + f.GetTypeName2() + "]").ToArray())) + (freshSubs.Count > 0 ? " (+sub " + string.Join(", ", freshSubs.ToArray()) + ")" : "") + "; body " + sizeBefore + " -> " + Size(doc));
        foreach (var f in fresh)
        {
            bool w; int err = 0; try { err = f.GetErrorCode2(out w); } catch { }
            L("   " + f.Name + (err != 0 ? " ERROR " + err : " ok") + "  " + DumpDef(f, doc));
        }
        for (int i = fresh.Count - 1; i >= 0; i--) Remove(doc, fresh[i]);
        doc.EditRebuild3();
        if (fresh.Count > 0 && Size(doc) != sizeBefore) L("   (after cleanup the body reads " + Size(doc) + ")");
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder();      // the compiled probe is cached between runs: start every run with an empty log
        var doc = (ModelDoc2)sw.ActiveDoc; var fm = doc.FeatureManager; string mode = string.IsNullOrEmpty(arg) ? "hem" : arg.ToLowerInvariant();
        L("tray: " + Size(doc));
        if (mode == "hem")
        {
            Action sel = () => Sel(doc, "EDGE", 0.0, 0.03, 0.025, false, 0);
            foreach (int type in new[] { 1, 0, 2, 3, 4 })
                foreach (int pos in new[] { 0, 1 })
                {
                    int t = type, p = pos;
                    Attempt(doc, "hem type " + t + " (" + new[] { "open", "closed", "teardrop", "rolled", "double" }[t] + ") pos " + p, sel,
                        () => fm.InsertSheetMetalHem2(t, p, false, 0.01, 0.0001524, t == 0 ? Math.PI / 2 : Math.PI, 0.0015, 0.0005, null, true, 1, 1, true, 0.5, 0.0, 0.0));
                }
        }
        else if (mode == "hem2")
        {
            // the two types that returned null with the first parameter set, plus fold direction (outer vs inner edge, reverse) and a two-edge selection
            Action outer = () => Sel(doc, "EDGE", 0.0, 0.03, 0.025, false, 0);
            foreach (double ang in new[] { Math.PI, 1.5 * Math.PI, 1.25 * Math.PI })
                foreach (double rad in new[] { 0.0015, 0.003 })
                {
                    double a = ang, r = rad;
                    Attempt(doc, "teardrop angle " + (a * 180 / Math.PI).ToString("F0") + " deg radius " + r * 1000 + " mm", outer, () => fm.InsertSheetMetalHem2(2, 0, false, 0.01, 0.0001524, a, r, 0.0005, null, true, 1, 1, true, 0.5, 0.0, 0.0));
                }
            foreach (double len in new[] { 0.01, 0.02, 0.006 })
            {
                double ln = len;
                Attempt(doc, "double hem length " + ln * 1000 + " mm", outer, () => fm.InsertSheetMetalHem2(4, 0, false, ln, 0.0001524, Math.PI, 0.0015, 0.0005, null, true, 1, 1, true, 0.5, 0.0, 0.0));
            }
            Attempt(doc, "closed hem on the INNER top edge", () => Sel(doc, "EDGE", 0.0, 0.0285, 0.025, false, 0), () => fm.InsertSheetMetalHem2(1, 0, false, 0.01, 0.0001524, Math.PI, 0.0015, 0.0005, null, true, 1, 1, true, 0.5, 0.0, 0.0));
            Attempt(doc, "closed hem, outer edge, reverse", outer, () => fm.InsertSheetMetalHem2(1, 0, true, 0.01, 0.0001524, Math.PI, 0.0015, 0.0005, null, true, 1, 1, true, 0.5, 0.0, 0.0));
            Attempt(doc, "closed hem on TWO edges (+y and +x walls, outer top edges)", () => { Sel(doc, "EDGE", 0.0, 0.03, 0.025, false, 0); Sel(doc, "EDGE", 0.05, 0.0, 0.025, true, 0); }, () => fm.InsertSheetMetalHem2(1, 0, false, 0.01, 0.0001524, Math.PI, 0.0015, 0.0005, null, true, 1, 1, true, 0.5, 0.0, 0.0));
            Attempt(doc, "closed hem with a K-factor 0.42 custom bend allowance", outer, () => { var cba = (CustomBendAllowance)fm.CreateCustomBendAllowance(); cba.Type = 2; cba.KFactor = 0.42; return fm.InsertSheetMetalHem2(1, 0, false, 0.01, 0.0001524, Math.PI, 0.0015, 0.0005, cba, true, 1, 1, true, 0.5, 0.0, 0.0); });
        }
        else if (mode == "breakcorner")
        {
            Attempt(doc, "break corner fillet 3 mm, the wall's outer vertical end edge", () => Sel(doc, "EDGE", 0.05, 0.03, 0.0125, false, 0), () => { doc.InsertSheetMetalBreakCorner(0, 0.003); return null; });
            Attempt(doc, "break corner chamfer 3 mm, the same edge", () => Sel(doc, "EDGE", 0.05, 0.03, 0.0125, false, 0), () => { doc.InsertSheetMetalBreakCorner(1, 0.003); return null; });
            Attempt(doc, "break corner fillet 3 mm, the wall's outer FACE", () => Sel(doc, "FACE", 0.0, 0.03, 0.0125, false, 0), () => { doc.InsertSheetMetalBreakCorner(0, 0.003); return null; });
            Attempt(doc, "break corner chamfer 3 mm, the wall's TOP face", () => Sel(doc, "FACE", 0.0, 0.02925, 0.025, false, 0), () => { doc.InsertSheetMetalBreakCorner(1, 0.003); return null; });
            Attempt(doc, "corner trim (internal=0, fillet 0, 3 mm) on the vertical end edge", () => Sel(doc, "EDGE", 0.05, 0.03, 0.0125, false, 0), () => fm.InsertSheetMetalCornerTrim(0, 0, 0.003, 1, 0.0));
        }
        else if (mode == "breakcorner2")
        {
            // Break-Corner takes FACES. Which faces make which corners? chamfer vs fillet, a flange END face, a flange's outer/inner face, two faces at once, and the option flags (they are properties of the feature).
            Func<object> chamfer = () => { doc.InsertSheetMetalBreakCorner(1, 0.003); return null; };
            Func<object> fillet = () => { doc.InsertSheetMetalBreakCorner(0, 0.003); return null; };
            Attempt(doc, "chamfer 3 mm, the +y wall's outer FACE", () => Sel(doc, "FACE", 0.0, 0.03, 0.0125, false, 0), chamfer);
            Attempt(doc, "chamfer 3 mm, the +y wall's INNER face", () => Sel(doc, "FACE", 0.0, 0.0285, 0.0125, false, 0), chamfer);
            Attempt(doc, "fillet 3 mm, the +y wall's END face (x = 50)", () => Sel(doc, "FACE", 0.05, 0.02925, 0.0125, false, 0), fillet);
            Attempt(doc, "fillet 3 mm, the base flange's TOP face (z = 0)", () => Sel(doc, "FACE", 0.0, 0.0, 0.0, false, 0), fillet);
            Attempt(doc, "fillet 3 mm, the base flange's BOTTOM face (z = -1.5)", () => Sel(doc, "FACE", 0.0, 0.0, -0.0015, false, 0), fillet);
            Attempt(doc, "fillet 3 mm, two walls' outer faces (+y, +x)", () => { Sel(doc, "FACE", 0.0, 0.03, 0.0125, false, 0); Sel(doc, "FACE", 0.05, 0.0, 0.0125, true, 0); }, fillet);
            Attempt(doc, "fillet 3 mm, the +y wall's outer face + the +x wall's outer face + the -y + the -x (all four)", () => { Sel(doc, "FACE", 0.0, 0.03, 0.0125, false, 0); Sel(doc, "FACE", 0.05, 0.0, 0.0125, true, 0); Sel(doc, "FACE", 0.0, -0.03, 0.0125, true, 0); Sel(doc, "FACE", -0.05, 0.0, 0.0125, true, 0); }, fillet);
        }
        else if (mode == "miter")
        {
            // PLATE fixture (base flange only, z -1.5..0). Profile = a vertical 20 mm line on the end face x = -50 at the corner y = +30; the edge chain = the four top-face perimeter edges.
            var mu = (MathUtility)sw.GetMathUtility();
            Func<string> profile = () =>
            {
                doc.ClearSelection2(true); Sel(doc, "FACE", -0.05, 0.0, -0.00075, false, 0); doc.SketchManager.InsertSketch(true);
                var sk = (Sketch)doc.SketchManager.ActiveSketch; var xf = (MathTransform)sk.ModelToSketchTransform;
                Func<double, double, double, double[]> toSketch = (x, y, z) => (double[])((MathPoint)((MathPoint)mu.CreatePoint(new double[] { x, y, z })).MultiplyTransform(xf)).ArrayData;
                var a = toSketch(-0.05, 0.03, 0.0); var b = toSketch(-0.05, 0.03, 0.02);
                var seg = doc.SketchManager.CreateLine(a[0], a[1], 0.0, b[0], b[1], 0.0);
                string name = ((Feature)sk).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true);
                return name;
            };
            // ONE look at the sketch before the attempts: model-space ends of the line, so the geometry is verified, not assumed
            string probeName = profile(); L("profile sketch " + probeName);
            { var sf = (Feature)null; var f0 = (Feature)doc.FirstFeature(); while (f0 != null) { if (f0.Name == probeName) sf = f0; f0 = (Feature)f0.GetNextFeature(); }
              if (sf != null) { var sk0 = (Sketch)sf.GetSpecificFeature2(); var xfi = (MathTransform)((MathTransform)sk0.ModelToSketchTransform).Inverse(); var segs = (object[])sk0.GetSketchSegments();
                foreach (object o in segs) { var ln = (SketchLine)o; var sp = (SketchPoint)ln.GetStartPoint2(); var ep = (SketchPoint)ln.GetEndPoint2();
                  var m1 = (double[])((MathPoint)((MathPoint)mu.CreatePoint(new double[] { sp.X, sp.Y, sp.Z })).MultiplyTransform(xfi)).ArrayData; var m2 = (double[])((MathPoint)((MathPoint)mu.CreatePoint(new double[] { ep.X, ep.Y, ep.Z })).MultiplyTransform(xfi)).ArrayData;
                  L("  line in model space (mm): (" + string.Join(", ", m1.Select(v => (v * 1000).ToString("F2")).ToArray()) + ") -> (" + string.Join(", ", m2.Select(v => (v * 1000).ToString("F2")).ToArray()) + ")"); }
                doc.ClearSelection2(true); sf.Select2(false, 0); doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed); } }

            var edges = new[] { new[] { 0.0, 0.03, 0.0 }, new[] { 0.05, 0.0, 0.0 }, new[] { 0.0, -0.03, 0.0 }, new[] { -0.05, 0.0, 0.0 } };
            foreach (int skMark in new[] { 0, 1 })
                foreach (int edMark in new[] { 0, 2, 4 })
                {
                    int sm = skMark, em = edMark;
                    Attempt(doc, "miter flange, sketch mark " + sm + ", edge mark " + em + ", 4 edges",
                        () => { string n = profile(); doc.Extension.SelectByID2(n, "SKETCH", 0, 0, 0, false, sm, null, 0); for (int i = 0; i < 4; i++) Sel(doc, "EDGE", edges[i][0], edges[i][1], edges[i][2], true, em); },
                        () => fm.InsertSheetMetalMiterFlange(true, 0.0015, 0.0001524, true, true, 0.5, 0.0, 0.0, 1, true, 1, 0.0, 0.0, null));
                }
        }
        else if (mode == "seldbg2")
        {
            // order and marks: sketch first vs edges first, count after every pick
            var muu = (MathUtility)sw.GetMathUtility(); var smgr = (SelectionMgr)doc.SelectionManager;
            Func<string> prof = () =>
            {
                doc.ClearSelection2(true); Sel(doc, "FACE", -0.05, 0.0, -0.00075, false, 0); doc.SketchManager.InsertSketch(true);
                var sk = (Sketch)doc.SketchManager.ActiveSketch; var xf = (MathTransform)sk.ModelToSketchTransform;
                Func<double, double, double, double[]> toS = (x, y, z) => (double[])((MathPoint)((MathPoint)muu.CreatePoint(new double[] { x, y, z })).MultiplyTransform(xf)).ArrayData;
                var a = toS(-0.05, 0.03, 0.0); var b = toS(-0.05, 0.03, 0.02); doc.SketchManager.CreateLine(a[0], a[1], 0.0, b[0], b[1], 0.0);
                string name = ((Feature)sk).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); return name;
            };
            string n1 = prof(); L("sketch " + n1 + ", selection count after the sketch exit: " + smgr.GetSelectedObjectCount2(-1));
            doc.ClearSelection2(true);
            L("A) sketch first: select sketch -> " + doc.Extension.SelectByID2(n1, "SKETCH", 0, 0, 0, false, 0, null, 0) + " count " + smgr.GetSelectedObjectCount2(-1));
            for (int i = 0; i < 4; i++) { var e = new[] { new[] { 0.0, 0.03, 0.0 }, new[] { 0.05, 0.0, 0.0 }, new[] { 0.0, -0.03, 0.0 }, new[] { -0.05, 0.0, 0.0 } }[i]; L("   + edge " + i + " -> " + Sel(doc, "EDGE", e[0], e[1], e[2], true, 0) + " count " + smgr.GetSelectedObjectCount2(-1)); }
            doc.ClearSelection2(true);
            for (int i = 0; i < 4; i++) { var e = new[] { new[] { 0.0, 0.03, 0.0 }, new[] { 0.05, 0.0, 0.0 }, new[] { 0.0, -0.03, 0.0 }, new[] { -0.05, 0.0, 0.0 } }[i]; L("B) edges first: edge " + i + " -> " + Sel(doc, "EDGE", e[0], e[1], e[2], i > 0, 0) + " count " + smgr.GetSelectedObjectCount2(-1)); }
            L("   + sketch (append) -> " + doc.Extension.SelectByID2(n1, "SKETCH", 0, 0, 0, true, 0, null, 0) + " count " + smgr.GetSelectedObjectCount2(-1));
            for (int k = 1; k <= smgr.GetSelectedObjectCount2(-1); k++) L("   item " + k + ": type " + smgr.GetSelectedObjectType3(k, -1) + " mark " + smgr.GetSelectedObjectMark(k));
            doc.ClearSelection2(true);
            // clean the sketch
            var f0 = (Feature)doc.FirstFeature(); while (f0 != null) { if (f0.Name == n1) { f0.Select2(false, 0); doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed); break; } f0 = (Feature)f0.GetNextFeature(); }
        }
        else if (mode == "seldbg3")
        {
            // what exactly is in the selection list after each pick? (append picks of several edges, no sketch involved)
            var smgr = (SelectionMgr)doc.SelectionManager; doc.ClearSelection2(true);
            Func<string> items = () =>
            {
                var l = new List<string>();
                for (int k = 1; k <= smgr.GetSelectedObjectCount2(-1); k++)
                {
                    int t = smgr.GetSelectedObjectType3(k, -1); var o = smgr.GetSelectedObject6(k, -1); string d = "t" + t;
                    var e = o as Edge; if (e != null) { var a = (double[])((Vertex)e.GetStartVertex()).GetPoint(); var b = (double[])((Vertex)e.GetEndVertex()).GetPoint(); d += " edge(" + (a[0] * 1000).ToString("F0") + "," + (a[1] * 1000).ToString("F0") + "," + (a[2] * 1000).ToString("F1") + ")->(" + (b[0] * 1000).ToString("F0") + "," + (b[1] * 1000).ToString("F0") + "," + (b[2] * 1000).ToString("F1") + ")"; }
                    l.Add(d);
                }
                return string.Join(" | ", l.ToArray());
            };
            var pts = new[] { new[] { 0.0, 0.03, 0.0 }, new[] { 0.05, 0.0, 0.0 }, new[] { 0.0, -0.03, 0.0 }, new[] { -0.05, 0.0, 0.0 } };
            var view = (ModelView)doc.ActiveView; L("view: " + (view == null ? "none" : "orientation " + view.Orientation3 + ", scale " + view.Scale2));
            for (int i = 0; i < 4; i++) { bool ok = Sel(doc, "EDGE", pts[i][0], pts[i][1], pts[i][2], i > 0, 0); L("append pick " + i + " -> " + ok + ": " + items()); }
            doc.ClearSelection2(true);
            doc.ShowNamedView2("*Isometric", 7); doc.ViewZoomtofit2(); doc.GraphicsRedraw2(); view = (ModelView)doc.ActiveView; L("after isometric + zoom to fit: scale " + view.Scale2);
            for (int i = 0; i < 4; i++) { bool ok = Sel(doc, "EDGE", pts[i][0], pts[i][1], pts[i][2], i > 0, 0); L("ISO append pick " + i + " -> " + ok + ": " + items()); }
            doc.ClearSelection2(true);
            doc.ShowNamedView2("*Top", 5); doc.ViewZoomtofit2(); doc.GraphicsRedraw2();
            for (int i = 0; i < 4; i++) { bool ok = Sel(doc, "EDGE", pts[i][0], pts[i][1], pts[i][2], i > 0, 0); L("TOP append pick " + i + " -> " + ok + ": " + items()); }
            doc.ClearSelection2(true);
            doc.ClearSelection2(true);
            for (int i = 0; i < 4; i++) { bool ok = doc.Extension.SelectByID2("", "EDGE", pts[i][0], pts[i][1], pts[i][2], true, 0, null, 0); L("pure append pick " + i + " -> " + ok + ": " + items()); }
            doc.ClearSelection2(true);
            // by the edge object itself (Select4 with append)
            var body = (Body2)((object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false))[0]; var sd = smgr.CreateSelectData(); int n = 0;
            foreach (Edge e in (object[])body.GetEdges())
            {
                var a = (double[])((Vertex)e.GetStartVertex()).GetPoint(); var b = (double[])((Vertex)e.GetEndVertex()).GetPoint();
                if (Math.Abs(a[2]) < 1e-9 && Math.Abs(b[2]) < 1e-9) { bool ok = ((Entity)e).Select4(n > 0, sd); n++; L("Select4 append edge " + n + " -> " + ok + ": " + items()); }
            }
            doc.ClearSelection2(true);
        }
        else if (mode == "seldbg")
        {
            // does each selection land? (count after every step) - edges on the plate's top face and sides, by SelectByID2 and by walking the body
            var smgr = (SelectionMgr)doc.SelectionManager; doc.ClearSelection2(true);
            foreach (var pt in new[] { new[] { 0.0, 0.03, 0.0 }, new[] { 0.05, 0.0, 0.0 }, new[] { 0.0, 0.03, -0.0015 }, new[] { 0.0, 0.03, -0.00075 }, new[] { 0.0, 0.0285, 0.0 } })
            {
                doc.ClearSelection2(true);
                bool ok = Sel(doc, "EDGE", pt[0], pt[1], pt[2], false, 0);
                L("EDGE at (" + pt[0] * 1000 + ", " + pt[1] * 1000 + ", " + pt[2] * 1000 + ") -> " + ok + ", count " + smgr.GetSelectedObjectCount2(-1));
            }
            var body = (Body2)((object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false))[0];
            foreach (Edge e in (object[])body.GetEdges())
            {
                var cv = (Curve)e.GetCurve(); if (!cv.IsLine()) continue;
                var sv = (Vertex)e.GetStartVertex(); var ev = (Vertex)e.GetEndVertex(); var a = (double[])sv.GetPoint(); var b = (double[])ev.GetPoint();
                L("edge (" + (a[0] * 1000).ToString("F1") + "," + (a[1] * 1000).ToString("F1") + "," + (a[2] * 1000).ToString("F2") + ") -> (" + (b[0] * 1000).ToString("F1") + "," + (b[1] * 1000).ToString("F1") + "," + (b[2] * 1000).ToString("F2") + ")");
            }
        }
        else if (mode == "moveface")
        {
            Attempt(doc, "move face offset +2 mm, the +y wall's outer face", () => Sel(doc, "FACE", 0.0, 0.03, 0.0125, false, 0), () => fm.InsertMoveFace3(0, false, 0.0, 0.002, null, null, 0, 0.0));
            Attempt(doc, "move face translate (null params), the +y wall's outer face", () => Sel(doc, "FACE", 0.0, 0.03, 0.0125, false, 0), () => fm.InsertMoveFace3(1, false, 0.0, 0.002, new double[] { 0, 0.002, 0 }, new double[6], 0, 0.0));
            Attempt(doc, "move face offset -2 mm (reverse), the +y wall's outer face", () => Sel(doc, "FACE", 0.0, 0.03, 0.0125, false, 0), () => fm.InsertMoveFace3(0, true, 0.0, 0.002, null, null, 0, 0.0));
        }
        else return "unknown mode " + mode;
        L("tray at the end: " + Size(doc));
        return sb.ToString();
    }
}
