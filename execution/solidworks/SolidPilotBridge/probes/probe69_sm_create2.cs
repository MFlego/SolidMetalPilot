// Probe 69: sheet-metal creation calls the typed tools do not cover yet - jog (IModelDoc2.InsertSheetMetalJog), shell + rip + insert bends (InsertFeatureShell, InsertRip, InsertBends2), cross-break, gusset.
// Same harness as probe 40: each attempt selects, calls, reports the NEW features (+ error codes, size before / after, the definition dump) and deletes what it created.   arg = "mode;key=value;..."
//   jog | jog2 (plate fixture: base flange only)   shell | rip | insertbends (a solid box on the Front Plane, made with tools)   crossbreak | gusset
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

    // ---- probe 69 proper: creation calls the typed tools do not cover yet (jog, rip + insert bends, cross-break, gusset). arg = "mode;key=value;..." ----
    static Dictionary<string, string> Args(string arg)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var parts = (arg ?? "").Split(';');
        d["mode"] = parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim().ToLowerInvariant() : "jog";
        for (int i = 1; i < parts.Length; i++) { int k = parts[i].IndexOf('='); if (k > 0) d[parts[i].Substring(0, k).Trim()] = parts[i].Substring(k + 1).Trim(); }
        return d;
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : dflt; }
    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder();
        var doc = (ModelDoc2)sw.ActiveDoc; var fm = doc.FeatureManager; var a = Args(arg); string mode = a["mode"];
        L("part: " + Size(doc) + "   mode " + mode);
        var mu = (MathUtility)sw.GetMathUtility();
        // a sketch on the face at (fx, fy, fz) holding the given lines (model-space end points); the sketch is left CLOSED; returns its name
        Func<double, double, double, double[][], string> lineSketch = (fx, fy, fz, lines) =>
        {
            doc.ClearSelection2(true); if (!Sel(doc, "FACE", fx, fy, fz, false, 0)) return null;
            doc.SketchManager.InsertSketch(true); var sk = (Sketch)doc.SketchManager.ActiveSketch; var xf = (MathTransform)sk.ModelToSketchTransform;
            Func<double, double, double, double[]> to2 = (x, y, z) => (double[])((MathPoint)((MathPoint)mu.CreatePoint(new double[] { x, y, z })).MultiplyTransform(xf)).ArrayData;
            foreach (var l in lines) { var p = to2(l[0], l[1], l[2]); var q = to2(l[3], l[4], l[5]); doc.SketchManager.CreateLine(p[0], p[1], 0.0, q[0], q[1], 0.0); }
            string name = ((Feature)sk).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); return name;
        };

        if (mode == "jog")
        {
            // PLATE fixture (base flange only, 100 x 60 x 1.5, z -1.5..0): a jog line across the plate at x = jx, fixed face = the part of the plate on the -x side.
            double jx = N(a, "x", 0.0), off = N(a, "offset", 0.01), r = N(a, "radius", 0.0015);
            string name = lineSketch(0.0, 0.0, 0.0, new[] { new[] { jx, -0.03, 0.0, jx, 0.03, 0.0 } }); L("jog line sketch: " + name);
            if (name == null) return sb.ToString();
            Func<string, Func<bool>> pick = which => () => true;
            foreach (int dim in new[] { 1, 2, 3 })
                foreach (int bend in new[] { 1, 2, 3, 4 })
                {
                    int d = dim, b = bend;
                    Attempt(doc, "jog 90 deg, r " + r * 1000 + " mm, offset " + off * 1000 + " mm, dim pos " + d + ", bend pos " + b + " (sketch, then the fixed face at -x)",
                        () => { doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0); Sel(doc, "FACE", -0.03, 0.0, 0.0, true, 0); },
                        () => { doc.InsertSheetMetalJog(Math.PI / 2, r, off, false, false, (short)d, (short)b); return null; });
                }
        }
        else if (mode == "jog2")
        {
            double jx = N(a, "x", 0.0), off = N(a, "offset", 0.01), r = N(a, "radius", 0.0015);
            string name = lineSketch(0.0, 0.0, 0.0, new[] { new[] { jx, -0.03, 0.0, jx, 0.03, 0.0 } }); L("jog line sketch: " + name);
            if (name == null) return sb.ToString();
            Action selSketchFirst = () => { doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0); Sel(doc, "FACE", -0.03, 0.0, 0.0, true, 0); };
            Action selFaceFirst = () => { Sel(doc, "FACE", -0.03, 0.0, 0.0, false, 0); doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, true, 0, null, 0); };
            Action selSketchOnly = () => { doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0); };
            Action selSketchMarked = () => { doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 1, null, 0); Sel(doc, "FACE", -0.03, 0.0, 0.0, true, 2); };
            Action selFixedFaceOnPlusX = () => { doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0); Sel(doc, "FACE", 0.03, 0.0, 0.0, true, 0); };
            var calls = new Dictionary<string, Action> { { "sketch, face(-x)", selSketchFirst }, { "face(-x), sketch", selFaceFirst }, { "sketch only", selSketchOnly }, { "sketch mark 1, face mark 2", selSketchMarked }, { "sketch, face(+x)", selFixedFaceOnPlusX } };
            foreach (var kv in calls)
            {
                var sel = kv.Value;
                Attempt(doc, "jog (" + kv.Key + ")", sel, () => { doc.InsertSheetMetalJog(Math.PI / 2, r, off, false, false, 1, 1); return null; });
            }
        }
        else if (mode == "jog3")
        {
            // the sketched-bend recipe: the sketch with the line stays ACTIVE (the command is started from inside it) and the fixed face is selected with append
            double jx = N(a, "x", 0.0), off = N(a, "offset", 0.01), r = N(a, "radius", 0.0015);
            foreach (int bend in new[] { 1, 4 })
            {
                int b = bend;
                Attempt(doc, "jog from the ACTIVE sketch, bend pos " + b,
                    () =>
                    {
                        Sel(doc, "FACE", 0.0, 0.0, 0.0, false, 0); doc.SketchManager.InsertSketch(true);
                        doc.SketchManager.CreateLine(jx, -0.03, 0.0, jx, 0.03, 0.0);
                        Sel(doc, "FACE", -0.03, 0.0, 0.0, false, 0);
                    },
                    () => { doc.InsertSheetMetalJog(Math.PI / 2, r, off, false, false, 1, (short)b); return null; });
                if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);
            }
            Attempt(doc, "jog from the ACTIVE sketch, fixed face selected FIRST (before the sketch opens)",
                () =>
                {
                    Sel(doc, "FACE", 0.0, 0.0, 0.0, false, 0); doc.SketchManager.InsertSketch(true);
                    doc.SketchManager.CreateLine(jx, -0.03, 0.0, jx, 0.03, 0.0);
                    doc.Extension.SelectByID2("", "FACE", -0.03, 0.0, 0.0, true, 0, null, 0);
                },
                () => { doc.InsertSheetMetalJog(Math.PI / 2, r, off, false, false, 1, 1); return null; });
            if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);
        }
        else if (mode == "shell")
        {
            // a solid box on the Front Plane (made by the caller with tools): shell it from its top face, then rip the vertical corner edges and insert bends
            double t = N(a, "t", 0.0015), top = N(a, "top", 0.03);
            Attempt(doc, "shell " + t * 1000 + " mm from the top face (z = " + top * 1000 + ")", () => Sel(doc, "FACE", 0.0, 0.0, top, false, 0), () => { doc.InsertFeatureShell(t, false); return null; });
        }
        else if (mode == "rip")
        {
            double gap = N(a, "gap", 0.0001), hx = N(a, "hx", 0.05), hy = N(a, "hy", 0.03), zm = N(a, "zmid", 0.015);
            Attempt(doc, "rip gap " + gap * 1000 + " mm on the four vertical corner edges (doc.InsertRip)", () => { Sel(doc, "EDGE", hx, hy, zm, false, 0); Sel(doc, "EDGE", -hx, hy, zm, true, 0); Sel(doc, "EDGE", -hx, -hy, zm, true, 0); Sel(doc, "EDGE", hx, -hy, zm, true, 0); }, () => { doc.InsertRip(gap); return null; });
            Attempt(doc, "rip gap " + gap * 1000 + " mm on ONE corner edge", () => Sel(doc, "EDGE", hx, hy, zm, false, 0), () => { doc.InsertRip(gap); return null; });
        }
        else if (mode == "insertbends")
        {
            // after shell + rip (kept: this mode does NOT clean up the rip)
            double r = N(a, "radius", 0.0015), k = N(a, "k", 0.5), bz = N(a, "bottom", 0.0);
            Attempt(doc, "InsertBends2 r " + r * 1000 + " K " + k + " fixed face = the bottom (z = " + bz * 1000 + ")", () => Sel(doc, "FACE", 0.0, 0.0, bz, false, 0), () => ((PartDoc)doc).InsertBends2(r, "", k, 0.0, false, 0.5, false));
            Attempt(doc, "InsertBends (old) r " + r * 1000 + " K " + k, () => Sel(doc, "FACE", 0.0, 0.0, bz, false, 0), () => ((PartDoc)doc).InsertBends(r, "", k, 0.0, false, 0.5));
        }
        else if (mode == "crossbreak")
        {
            // PLATE fixture. A cross-break sketch on the top face: an X of two lines across a rectangle (lines only); then the creation with the sketch selected, with and without the face.
            double ang = N(a, "angle", 3.0) * Math.PI / 180, rad = N(a, "radius", 0.003);
            string xs = lineSketch(0.0, 0.0, 0.0, new[] { new[] { -0.03, -0.015, 0.0, 0.03, 0.015, 0.0 }, new[] { -0.03, 0.015, 0.0, 0.03, -0.015, 0.0 } }); L("cross-break sketch (an X): " + xs);
            string rs = lineSketch(0.0, 0.0, 0.0, new[] { new[] { -0.03, -0.015, 0.0, 0.03, -0.015, 0.0 }, new[] { 0.03, -0.015, 0.0, 0.03, 0.015, 0.0 }, new[] { 0.03, 0.015, 0.0, -0.03, 0.015, 0.0 }, new[] { -0.03, 0.015, 0.0, -0.03, -0.015, 0.0 } }); L("cross-break sketch (a rectangle): " + rs);
            foreach (var nm in new[] { xs, rs })
            {
                string sk = nm;
                Attempt(doc, "cross-break " + sk + " angle " + ang * 180 / Math.PI + " deg radius " + rad * 1000 + " mm: sketch only", () => doc.Extension.SelectByID2(sk, "SKETCH", 0, 0, 0, false, 0, null, 0), () => fm.InsertCrossBreak(ang, rad));
                Attempt(doc, "cross-break " + sk + ": sketch + the top face", () => { doc.Extension.SelectByID2(sk, "SKETCH", 0, 0, 0, false, 0, null, 0); Sel(doc, "FACE", 0.02, 0.0, 0.0, true, 0); }, () => fm.InsertCrossBreak(ang, rad));
                Attempt(doc, "cross-break " + sk + ": the top face, then the sketch", () => { Sel(doc, "FACE", 0.02, 0.0, 0.0, false, 0); doc.Extension.SelectByID2(sk, "SKETCH", 0, 0, 0, true, 0, null, 0); }, () => fm.InsertCrossBreak(ang, rad));
            }
        }
        else if (mode == "crossbreak2")
        {
            // create it and LEAVE it: rebuild, size, faces, the flat pattern, the definition (angle in degrees, radius in mm)
            double ang = N(a, "angle", 3.0) * Math.PI / 180, rad = N(a, "radius", 0.003);
            double hx = N(a, "hx", 0.03), hy = N(a, "hy", 0.015);
            string xs = lineSketch(0.0, 0.0, 0.0, new[] { new[] { -hx, -hy, 0.0, hx, hy, 0.0 }, new[] { -hx, hy, 0.0, hx, -hy, 0.0 } }); L("cross-break sketch (an X, half sizes " + hx * 1000 + " x " + hy * 1000 + " mm): " + xs);
            doc.ClearSelection2(true); doc.Extension.SelectByID2(xs, "SKETCH", 0, 0, 0, false, 0, null, 0); Sel(doc, "FACE", 0.02, 0.0, 0.0, true, 0);
            var f = fm.InsertCrossBreak(ang, rad) as Feature; L("created: " + (f == null ? "null" : f.Name));
            doc.EditRebuild3(); L("after rebuild: " + Size(doc));
            var box = (double[])Body0(doc).GetBodyBox(); L("body box mm: [" + (box[0] * 1000).ToString("0.###") + "," + (box[1] * 1000).ToString("0.###") + "," + (box[2] * 1000).ToString("0.###") + "] .. [" + (box[3] * 1000).ToString("0.###") + "," + (box[4] * 1000).ToString("0.###") + "," + (box[5] * 1000).ToString("0.###") + "]");
            if (f != null) { bool w; L("error " + f.GetErrorCode2(out w) + "  " + DumpDef(f, doc)); }
        }
        else if (mode == "gusset")
        {
            // fixture: the plate + ONE 25 mm flange on the +y edge (sm_fixture.ps1 -Flanges 1). The faces at the bend: the plate's top face (z = 0), the flange's inner face (y = 28.5), the bend's cylinders.
            double len = N(a, "length", 0.01), th = N(a, "thickness", 0.0015), depth = N(a, "depth", 0.004);
            var cyl = new List<Face2>(); foreach (Face2 f in (object[])Body0(doc).GetFaces()) { var sf = (Surface)f.GetSurface(); if (sf.IsCylinder()) cyl.Add(f); }
            L("cylindrical faces: " + cyl.Count + "  (" + string.Join(", ", cyl.Select(f => { var bx = (double[])f.GetBox(); return "[" + (bx[0] * 1000).ToString("0.##") + "," + (bx[1] * 1000).ToString("0.##") + "," + (bx[2] * 1000).ToString("0.##") + "]..[" + (bx[3] * 1000).ToString("0.##") + "," + (bx[4] * 1000).ToString("0.##") + "," + (bx[5] * 1000).ToString("0.##") + "]"; }).ToArray()) + ")");
            Func<object> gusset = () => fm.InsertSheetMetalGussetFeature3(false, 0.0, false, 0, depth, len, false, 0.01, Math.PI / 4, false, 0.01, th, false, 0.0, false, 0.0, false, 0.0, 0, false, 0.0, false, false, false);
            Action selCyl = () => { bool app = false; foreach (var f in cyl) { ((Entity)f).Select4(app, null); app = true; } };
            Attempt(doc, "gusset, all the bend cylinders selected", selCyl, gusset);
            Attempt(doc, "gusset, the plate top face + the flange inner face", () => { Sel(doc, "FACE", 0.0, 0.0, 0.0, false, 0); Sel(doc, "FACE", 0.0, 0.0285, 0.012, true, 0); }, gusset);
            Attempt(doc, "gusset, the flange inner face + the plate top face", () => { Sel(doc, "FACE", 0.0, 0.0285, 0.012, false, 0); Sel(doc, "FACE", 0.0, 0.0, 0.0, true, 0); }, gusset);
            Attempt(doc, "gusset, the flange outer face", () => Sel(doc, "FACE", 0.0, 0.03, 0.012, false, 0), gusset);
            Attempt(doc, "gusset 2 (arrays), the plate top + flange inner", () => { }, () => fm.InsertSheetMetalGussetFeature2(false, 0.0, false, 0, depth, len, false, 0.01, Math.PI / 4, false, 0.01, th, false, 0.0, false, 0.0, false, 0.0, 0, false, 0.0, new object[] { (Face2)FaceAt(doc, 0.0, 0.0, 0.0), (Face2)FaceAt(doc, 0.0, 0.0285, 0.012) }, null, null));
        }
        return sb.ToString();
    }
}
