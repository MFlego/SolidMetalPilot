// sheet_metal_convert - Convert to Sheet Metal, driven from inside SolidWorks. The API cannot do this: CreateFeature on a Convert definition returns null, InsertConvertToSheetMetal2 half-builds on a
// surface body, a different bend list cannot be written into an existing feature and API selections never reach the PropertyManager's lists (probes 04, 06, 15, 19-21; DESIGN-NOTES P4, P13-P15).
// What does work is what a user does: preselect the fixed face, start the command, fill the lists with genuine picks, press OK. This tool does exactly that, in ONE gate call:
//   * everything is resolved BEFORE the first UI action (fixed face, bend edges, rip sketches, settings), so a bad request fails with nothing opened;
//   * the PropertyManager is a WPF control in this very process (see PmUi): its lists, numeric boxes, check boxes and buttons are driven through their automation peers;
//   * picks are POSTED mouse messages at pixels computed from the view transform, chosen where the edge is farthest from every other edge on screen, and VERIFIED twice: the PM list must grow by
//     one and the selection manager's newest entry must be the intended edge / sketch - a wrong pick cancels the whole command instead of continuing;
//   * the PropertyManager is always closed again (OK on success, Cancel on any failure) and hidden bodies are made visible again;
//   * the settings are write-verified against the created feature's IConvertSolidFeatureData, as everywhere else.
// Selection ORDER is part of the recipe (fixed face -> bends nearest the fixed face -> rip sketches -> the next bend series ...): `steps` keeps it exactly.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidPilot.Bridge;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        private const string ConvertTitle = "SolidToSheetMetalDVE";          // the WPF root's title is "SheetMetalWPF.SolidToSheetMetalDVE_c"
        private const int ConvertCommand = 2882;                              // swCommands_SolidToSheetMetal
        private static readonly string[] ConvertGroups = { "UseMaterialDisplayGroup", "GaugeOptionDisplayGroup", "FixedFaceDisplayGroup", "BendEdgesDisplayGroup", "RipEdgesDisplayGroup", "RipSketchDisplayGroup", "CornerTypeDisplayGroup", "BendAllowanceDisplayGroup", "ReliefDisplayGroup" };
        private static readonly string[] DefaultPickViews = { "current", "*Isometric", "*Back", "*Front", "*Left", "*Right", "*Top", "*Bottom" };

        // ---- the request, resolved -----------------------------------------------------------------------------------------------------------------------------------------------------------
        // The Convert command REBUILDS the body when it starts: every IBody2 / IEdge / IFace2 resolved before it is dead during it (RPC_E_DISCONNECTED). The plan therefore carries NUMBERS only for the
        // picking phase - sample points, polylines, midpoints - and COM objects only for what happens before the command starts (hiding bodies, selecting the fixed face).
        private static readonly double[] PickFractions = { 0.5, 0.35, 0.65, 0.2, 0.8, 0.1, 0.9 };

        private sealed class PickTarget
        {
            public string Label;                       // for messages: "bend 3" / "rip Sketch30"
            public string List;                        // "BendList" | "RipSketches"
            public bool IsEdge;                        // bend edge (else a rip sketch)
            public string Sketch;                      // rip sketches
            public double[] Mid;                       // where the selection manager must report it (edge midpoint / sketch segment midpoint)
            public double[][] Samples;                 // points ON it, at PickFractions of its length
            public double[] A, B;                      // bend edges: the end points (for the "do the bends fit" check)
            public string[] FaceKeys;                  // bend edges: the faces on both sides (centre + area as a key)
            public double[] FaceReach;                 // bend edges: how far each of those faces reaches away from the edge, in its plane (m)
        }

        private sealed class ConvertPlan
        {
            public IFace2 FixedFace; public IBody2 Body; public string FixedLabel;
            public List<double[][]> Polylines = new List<double[][]>();            // every edge of the body, sampled in model space (the "what else is on screen" map)
            public List<PickTarget> Targets = new List<PickTarget>();
            public double? Thickness, Radius, RipGap, OverlapRatio, ReliefRatio, KFactor; public bool? Reverse, KeepBody; public string Corner, Relief;
            public List<string> Views = new List<string>(DefaultPickViews); public bool Accept = true, HideOthers = true, CornerEarly;
        }

        private static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double Len(double[] a) { return Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); }

        private static double[] EdgeAt(IEdge e, double t)
        {
            var cv = e.GetCurve() as ICurve; double s, en; bool closed, periodic; cv.GetEndParams(out s, out en, out closed, out periodic);
            var r = cv.Evaluate2(s + (en - s) * t, 0) as double[]; return new[] { r[0], r[1], r[2] };
        }

        // (AllBodies / FaceNearest live in SolidWorksService.SurfacePrep.cs, shared with the delete_face and offset_surface tools)

        // the edge of `body` that passes through `pt` (within tol); two different edges that close are an ambiguity, not a guess
        private static IEdge EdgeNearest(IBody2 body, double[] pt, double tol, out string problem)
        {
            problem = null; IEdge best = null; double bd = double.MaxValue, second = double.MaxValue;
            var es = body.GetEdges() as object[]; if (es == null) { problem = "the body has no edges"; return null; }
            foreach (var o in es)
            {
                var e = o as IEdge; if (e == null) continue;
                var q = e.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; if (q == null || q.Length < 3) continue;
                double d = Len(Sub(q, pt));
                if (d < bd) { second = bd; bd = d; best = e; } else if (d < second) second = d;
            }
            if (best == null || bd > tol) { problem = "no edge of the body within " + (tol * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " mm of the point (nearest " + (bd * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm)"; return null; }
            if (second <= tol) { problem = "ambiguous: two edges pass within " + (tol * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " mm of the point"; return null; }
            return best;
        }

        private ConvertPlan ParseConvertPlan(IModelDoc2 doc, JObject p)
        {
            var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_convert needs an active part.");
            var plan = new ConvertPlan();

            // the fixed face: a point ON it (meters)
            JToken fixedTok = p["fixed_face"]; if (fixedTok == null) throw new ToolFailure("MISSING_PARAMETER", "fixed_face is required: [x, y, z] (meters), a point ON the face that stays put.");
            if (fixedTok is JObject) fixedTok = fixedTok["point"];
            if (fixedTok != null && fixedTok.Type == JTokenType.String) fixedTok = AsArray(fixedTok, "fixed_face");          // a JSON-encoded string is accepted, like everywhere at the MCP boundary
            var fp = ToPoint(fixedTok, "fixed_face");
            double fd; plan.FixedFace = FaceNearest(part, fp, out fd);
            if (plan.FixedFace == null || fd > 1e-4) throw new ToolFailure("FACE_NOT_FOUND", "No face within 0.1 mm of fixed_face (" + string.Join(", ", fp.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)).ToArray()) + ") m (nearest " + (fd * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm).");
            plan.Body = plan.FixedFace.GetBody() as IBody2; if (plan.Body == null) throw new ToolFailure("FACE_NOT_FOUND", "The fixed face belongs to no body.");
            plan.FixedLabel = "fixed face at " + string.Join(",", fp.Select(v => v.ToString("G5", CultureInfo.InvariantCulture)).ToArray());

            // the ordered steps: steps = [{"list": "bend", "edges": [[x,y,z], ...]}, {"list": "rip", "sketches": ["Sketch30", ...]}, ...]  or the shorthand bend_edges + rip_sketches (bends first, then rips)
            var steps = AsArray(p["steps"], "steps");
            if (steps == null)
            {
                steps = new JArray(); var be = AsArray(p["bend_edges"], "bend_edges"); var rs = AsArray(p["rip_sketches"], "rip_sketches");
                if (be != null && be.Count > 0) steps.Add(new JObject { ["list"] = "bend", ["edges"] = be });
                if (rs != null && rs.Count > 0) steps.Add(new JObject { ["list"] = "rip", ["sketches"] = rs });
            }
            if (steps.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "bend_edges (a list of points, one ON each bend edge, in pick order) or steps is required.");
            int nb = 0, nr = 0;
            foreach (var st in steps)
            {
                var so = st as JObject; if (so == null) throw new ToolFailure("INVALID_PARAMETER", "each step must be an object {\"list\": \"bend\"|\"rip\", ...}.");
                string kind = (so.Value<string>("list") ?? "").ToLowerInvariant();
                if (kind == "bend")
                {
                    var edges = AsArray(so["edges"], "steps[].edges"); if (edges == null || edges.Count == 0) throw new ToolFailure("INVALID_PARAMETER", "a bend step needs edges: [[x, y, z], ...] points ON the bend edges.");
                    foreach (var et in edges)
                    {
                        nb++; var pt = ToPoint(et is JObject ? et["point"] ?? et["mid"] : et, "bend edge " + nb); string why;
                        var e = EdgeNearest(plan.Body, pt, 5e-4, out why); if (e == null) throw new ToolFailure("EDGE_NOT_FOUND", "bend edge " + nb + ": " + why + ".");
                        var tgt = new PickTarget { Label = "bend " + nb, List = "BendList", IsEdge = true, Mid = EdgeAt(e, 0.5), Samples = PickFractions.Select(f => EdgeAt(e, f)).ToArray(), A = EdgeAt(e, 0), B = EdgeAt(e, 1) };
                        FaceGeometryAround(e, tgt);
                        plan.Targets.Add(tgt);
                    }
                }
                else if (kind == "rip")
                {
                    var names = AsArray(so["sketches"], "steps[].sketches"); if (names == null || names.Count == 0) throw new ToolFailure("INVALID_PARAMETER", "a rip step needs sketches: [\"Sketch30\", ...].");
                    foreach (var nt in names)
                    {
                        nr++; string nm = (string)nt; var feat = FindFeatureByName(doc, nm, null); var sk = feat == null ? null : feat.GetSpecificFeature2() as ISketch;
                        if (sk == null) throw new ToolFailure("SKETCH_NOT_FOUND", "rip sketch '" + nm + "' does not exist (or is not a sketch).");
                        var line = LongestSketchLine(sk); if (line == null) throw new ToolFailure("SKETCH_EMPTY", "rip sketch '" + nm + "' has no line to pick.");
                        var a = line.Item1; var b = line.Item2;
                        plan.Targets.Add(new PickTarget { Label = "rip " + nm, List = "RipSketches", Sketch = nm, Mid = new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 }, Samples = PickFractions.Select(f => new[] { a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f }).ToArray() });
                    }
                }
                else throw new ToolFailure("INVALID_PARAMETER", "a step's list must be \"bend\" or \"rip\".");
            }
            if (nb == 0) throw new ToolFailure("MISSING_PARAMETER", "at least one bend edge is required.");

            // the map of everything else on screen: every edge of the body, sampled about every 4 mm (3 .. 80 samples), as model-space points
            foreach (var o in (plan.Body.GetEdges() as object[]) ?? new object[0])
            {
                var e = o as IEdge; if (e == null) continue;
                try
                {
                    double len = Len(Sub(EdgeAt(e, 1), EdgeAt(e, 0))); int n = Math.Max(3, Math.Min(80, (int)(len / 0.004)));
                    var pts = new double[n + 1][]; for (int i = 0; i <= n; i++) pts[i] = EdgeAt(e, (double)i / n);
                    plan.Polylines.Add(pts);
                }
                catch (COMException) { }
            }

            // settings (meters; absent = the PropertyManager's own defaults)
            Func<string, double?> num = k => p.Value<double?>(k);
            plan.Thickness = num("thickness"); plan.Radius = num("bend_radius"); plan.RipGap = num("rip_gap"); plan.OverlapRatio = num("overlap_ratio"); plan.ReliefRatio = num("relief_ratio"); plan.KFactor = num("k_factor");
            // SolidWorks REMEMBERS the PropertyManager's last values, so a call must not lean on them: thickness and radius have to be given, everything else the request leaves out takes the PropertyManager's
            // FACTORY default below and is written explicitly (a stale 'keep body' leaves a second body, a stale 'reverse thickness' puts the material on the other side).
            if (!plan.Thickness.HasValue) throw new ToolFailure("MISSING_PARAMETER", "thickness is required (meters): SolidWorks remembers the PropertyManager's last value, so the call must say it.");
            if (!plan.Radius.HasValue) throw new ToolFailure("MISSING_PARAMETER", "bend_radius is required (meters, the inside radius): SolidWorks remembers the PropertyManager's last value, so the call must say it.");
            if (plan.Thickness <= 0 || plan.Thickness > 0.05) throw new ToolFailure("INVALID_PARAMETER", "thickness is in METERS (0 < t <= 50 mm).");
            if (plan.Radius <= 0 || plan.Radius > 0.1) throw new ToolFailure("INVALID_PARAMETER", "bend_radius is in METERS (0 < r <= 100 mm).");
            if (plan.RipGap.HasValue && (plan.RipGap < 0 || plan.RipGap > 0.01)) throw new ToolFailure("INVALID_PARAMETER", "rip_gap is in METERS (0 .. 10 mm).");
            string rev = (p.Value<string>("reverse_thickness") ?? "default").ToLowerInvariant(); plan.Reverse = rev == "on" || rev == "true" ? true : rev == "off" || rev == "false" ? false : (bool?)null;
            plan.KeepBody = p.Value<bool?>("keep_body");
            plan.Corner = (p.Value<string>("corner") ?? "").ToLowerInvariant(); if (plan.Corner.Length > 0 && plan.Corner != "butt" && plan.Corner != "overlap" && plan.Corner != "underlap") throw new ToolFailure("INVALID_PARAMETER", "corner must be butt, overlap or underlap.");
            plan.Relief = (p.Value<string>("relief") ?? "").ToLowerInvariant(); if (plan.Relief.Length > 0 && plan.Relief != "rectangular" && plan.Relief != "obround" && plan.Relief != "tear") throw new ToolFailure("INVALID_PARAMETER", "relief must be rectangular, obround or tear.");
            var views = AsArray(p["views"], "views"); if (views != null && views.Count > 0) plan.Views = views.Select(v => (string)v).ToList();
            plan.Accept = p.Value<bool?>("accept") ?? true; plan.HideOthers = p.Value<bool?>("hide_other_bodies") ?? true;
            string timing = (p.Value<string>("corner_timing") ?? "late").ToLowerInvariant(); if (timing != "late" && timing != "early") throw new ToolFailure("INVALID_PARAMETER", "corner_timing must be late (after the picks, the default) or early (before them: SolidWorks then raises a dialog while rip sketches are added).");
            plan.CornerEarly = timing == "early";
            // the PropertyManager's factory defaults (SolidWorks 2026, first use): reverse thickness ON, keep body OFF, corner Butt, rip gap 2 mm, overlap ratio 0.5, relief Rectangular with ratio 0.5
            plan.Reverse = plan.Reverse ?? true; plan.KeepBody = plan.KeepBody ?? false;
            if (plan.Corner.Length == 0) plan.Corner = "butt"; if (plan.Relief.Length == 0) plan.Relief = "rectangular";
            plan.RipGap = plan.RipGap ?? 0.002; plan.OverlapRatio = plan.OverlapRatio ?? 0.5;
            // the relief ratio has no usable box in the PropertyManager (see ApplyConvertReliefRatio): left out = the document template's value, given = written into the created feature's definition
            if (plan.ReliefRatio.HasValue && plan.Relief == "obround") throw new ToolFailure("INVALID_PARAMETER", "an obround relief stores no ratio: leave relief_ratio out (rectangular and tear take one).");
            if (plan.ReliefRatio.HasValue && (plan.ReliefRatio <= 0 || plan.ReliefRatio > 10)) throw new ToolFailure("INVALID_PARAMETER", "relief_ratio is a ratio of the thickness (0 < ratio <= 10).");
            return plan;
        }

        // distance from a point to the infinite line through a and b
        private static double DistToLine(double[] p, double[] a, double[] b)
        {
            var d = Sub(b, a); double l = Len(d); if (l < 1e-12) return Len(Sub(p, a));
            var ap = Sub(p, a); var cx = new[] { ap[1] * d[2] - ap[2] * d[1], ap[2] * d[0] - ap[0] * d[2], ap[0] * d[1] - ap[1] * d[0] };
            return Len(cx) / l;
        }

        // what the "do the bends fit" check needs about the two faces on either side of a bend edge: a key for each, and how far each reaches away from the edge (taken from the vertices of its edges)
        private void FaceGeometryAround(IEdge e, PickTarget t)
        {
            t.FaceKeys = new string[0]; t.FaceReach = new double[0];
            try
            {
                var adj = e.GetTwoAdjacentFaces2() as object[]; if (adj == null) return;
                var keys = new List<string>(); var reach = new List<double>();
                foreach (var o in adj)
                {
                    var f = o as IFace2; if (f == null) continue;
                    var bx = f.GetBox() as double[]; if (bx == null) continue;
                    keys.Add(((bx[0] + bx[3]) / 2 * 1e4).ToString("0", CultureInfo.InvariantCulture) + "," + ((bx[1] + bx[4]) / 2 * 1e4).ToString("0", CultureInfo.InvariantCulture) + "," + ((bx[2] + bx[5]) / 2 * 1e4).ToString("0", CultureInfo.InvariantCulture) + "," + (f.GetArea() * 1e8).ToString("0", CultureInfo.InvariantCulture));
                    double far = 0;
                    foreach (var eo in (f.GetEdges() as object[]) ?? new object[0]) { var fe = eo as IEdge; if (fe == null) continue; foreach (var fr in new[] { 0.0, 0.5, 1.0 }) far = Math.Max(far, DistToLine(EdgeAt(fe, fr), t.A, t.B)); }
                    reach.Add(far);
                }
                t.FaceKeys = keys.ToArray(); t.FaceReach = reach.ToArray();
            }
            catch (COMException) { }
        }

        // A bend takes up s = r + t of the flat on each side of the virtual sharp (90 degrees). A face between two parallel bend edges needs D > 2 s, a face with one bend edge needs to reach farther than s from
        // it. When they do not fit SolidWorks fails the build ("Unable to thicken the surface") and - seen 2026-10-27 - its OK handler can then spin for good: so the request is refused BEFORE anything is
        // opened. Returns a description of the first violation, or null.
        private static string BendZonesProblem(ConvertPlan plan, double thickness, double radius)
        {
            double s = radius + thickness; var byFace = new Dictionary<string, List<PickTarget>>();
            foreach (var t in plan.Targets.Where(x => x.IsEdge && x.FaceKeys != null))
                foreach (var k in t.FaceKeys) { List<PickTarget> l; if (!byFace.TryGetValue(k, out l)) byFace[k] = l = new List<PickTarget>(); l.Add(t); }
            foreach (var kv in byFace)
            {
                var ts = kv.Value;
                if (ts.Count == 1)
                {
                    int idx = Array.IndexOf(ts[0].FaceKeys, kv.Key); double reach = idx >= 0 && idx < ts[0].FaceReach.Length ? ts[0].FaceReach[idx] : double.MaxValue;
                    // a single bend edge on a face that is itself bounded by other bends is covered by the pair rule; here only a free flange matters
                    if (reach <= s + 1e-5) return ts[0].Label + ": the face beyond it reaches only " + (reach * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " mm from the edge, but a bend with inside radius " + (radius * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm and thickness " + (thickness * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm takes up " + (s * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm of it";
                    continue;
                }
                for (int i = 0; i < ts.Count; i++)
                    for (int j = i + 1; j < ts.Count; j++)
                    {
                        var di = Sub(ts[i].B, ts[i].A); var dj = Sub(ts[j].B, ts[j].A); double li = Len(di), lj = Len(dj); if (li < 1e-9 || lj < 1e-9) continue;
                        var cx = new[] { di[1] * dj[2] - di[2] * dj[1], di[2] * dj[0] - di[0] * dj[2], di[0] * dj[1] - di[1] * dj[0] };
                        if (Len(cx) / (li * lj) > 0.05) continue;                                   // not parallel: they do not run along the same strip of the face
                        double dist = DistToLine(ts[j].Mid, ts[i].A, ts[i].B);
                        if (dist <= 2 * s + 1e-5) return ts[i].Label + " and " + ts[j].Label + " are only " + (dist * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " mm apart on the same face, but two bends of inside radius " + (radius * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm and thickness " + (thickness * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm need 2 x " + (s * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " = " + (2 * s * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm between them";
                    }
            }
            return null;
        }

        // "4.572mm", "0.18in", "2" -> millimetres (a bare number is taken as mm)
        private static double? LengthMm(string s)
        {
            var n = FirstNumber(s); if (!n.HasValue) return null; string u = (s ?? "").Trim().ToLowerInvariant();
            if (u.EndsWith("in") || u.EndsWith("\"")) return n.Value * 25.4; if (u.EndsWith("ft")) return n.Value * 304.8; if (u.EndsWith("cm")) return n.Value * 10; if (u.EndsWith("m") && !u.EndsWith("mm")) return n.Value * 1000;
            return n.Value;
        }

        // the longest real line of a sketch, in MODEL coordinates (a rip sketch is one line; the longest is the one to click)
        private Tuple<double[], double[]> LongestSketchLine(ISketch sk)
        {
            var xf = sk.ModelToSketchTransform as IMathTransform; if (xf == null) return null; var back = xf.Inverse() as IMathTransform; var mu = _solidWorks.GetMathUtility() as IMathUtility;
            Tuple<double[], double[]> best = null; double bl = -1;
            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
            {
                var ln = o as ISketchLine; if (ln == null || ((ISketchSegment)ln).ConstructionGeometry) continue;
                var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint; if (a == null || b == null) continue;
                Func<ISketchPoint, double[]> toModel = sp => (double[])((IMathPoint)((IMathPoint)mu.CreatePoint(new[] { sp.X, sp.Y, sp.Z })).MultiplyTransform(back)).ArrayData;
                var pa = toModel(a); var pb = toModel(b); double l = Len(Sub(pb, pa)); if (l > bl) { bl = l; best = Tuple.Create(pa, pb); }
            }
            return best;
        }

        // ---- picking ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private sealed class ViewCache
        {
            public string Name; public IModelView View; public int[] Box;
            public List<List<int[]>> EdgePolylines = new List<List<int[]>>();      // every OTHER edge of the body, as pixels
        }

        private static double PixelDistToPolyline(int[] p, List<int[]> poly)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                double x1 = poly[i][0], y1 = poly[i][1], x2 = poly[i + 1][0], y2 = poly[i + 1][1], dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((p[0] - x1) * dx + (p[1] - y1) * dy) / l2)); double qx = x1 + t * dx - p[0], qy = y1 + t * dy - p[1];
                double d = Math.Sqrt(qx * qx + qy * qy); if (d < best) best = d;
            }
            return best;
        }

        // pixel polylines of all edges of the body in the CURRENT view (projected from the numbers the plan carries: no COM object of the body is touched)
        private ViewCache BuildViewCache(IModelDoc2 doc, ConvertPlan plan, string name)
        {
            var vc = new ViewCache { Name = name, View = doc.ActiveView as IModelView }; vc.Box = PmUi.VisibleBox(vc.View);
            foreach (var pts in plan.Polylines) vc.EdgePolylines.Add(pts.Select(q => PmUi.Project(vc.View, q)).ToList());
            return vc;
        }

        // wait until the view stopped moving (zoom-to-fit animates) and the window under a pixel is the graphics window
        private void SettleView(IModelDoc2 doc, IntPtr graphics, int[] probe)
        {
            double[] last = null; int stable = 0;
            PmUi.PumpUntil(() =>
            {
                var m = (double[])((IMathTransform)((IModelView)doc.ActiveView).Transform).ArrayData;
                if (last != null && m.Zip(last, (x, y) => Math.Abs(x - y)).Max() < 1e-9) stable++; else stable = 0;
                last = m; return stable >= 6 && (graphics == IntPtr.Zero || PmUi.WindowAt(probe[0], probe[1]) == graphics);
            }, 4000);
        }

        private string ShowView(IModelDoc2 doc, string name)
        {
            if (name != "current") { doc.ShowNamedView2(name, -1); PmUi.Pump(200); doc.ViewZoomtofit2(); }
            PmUi.Pump(500); return name;
        }

        private int NewestSelectionMatches(IModelDoc2 doc, PickTarget t, out JObject last)
        {
            var sm = (ISelectionMgr)doc.SelectionManager; int n = sm.GetSelectedObjectCount2(-1); last = n > 0 ? DescribeSelected(sm, n) : null;
            if (last == null) return 0;
            if (t.IsEdge) { var m = last["edge_mid"] as JArray; if (m == null) return -1; return Len(Sub(new[] { m[0].Value<double>(), m[1].Value<double>(), m[2].Value<double>() }, t.Mid)) < 5e-4 ? 1 : -1; }
            return (string)last["feature"] == t.Sketch ? 1 : -1;
        }

        // ---- the tool --------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static string Mm(double m) { return (m * 1000).ToString("0.######", CultureInfo.InvariantCulture) + "mm"; }
        private static double? FirstNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return null; int i = 0; while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-' || s[i] == ',')) i++;
            double v; return double.TryParse(s.Substring(0, i).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : (double?)null;
        }

        // Press OK or Cancel and wait for what SolidWorks does about it: the PropertyManager closes ("closed"), an error balloon appears while it stays open ("error": the rebuild failed - OK only),
        // a dialog with a choice needs a person ("dialog"), or nothing happens in time ("timeout").
        private string PressAndWait(bool accept, DialogWatchdog watch, int timeoutMs, out string balloon)
        {
            balloon = null; var root = PmUi.FindRoot(_solidWorks, ConvertTitle); if (root == null) return "closed";
            var before = PmUi.BalloonTexts();
            var btn = PmUi.Find(root.Visual, accept ? "OnOKButton" : "OnCancelButton"); if (btn != null) PmUi.Act(btn, "invoke", null);
            string outcome = null, seen = null;
            PmUi.PumpUntil(() =>
            {
                if (PmUi.FindRoot(_solidWorks, ConvertTitle) == null) { outcome = "closed"; return true; }
                if (accept) { var now = PmUi.BalloonTexts().Where(b => !before.Contains(b)).ToList(); if (now.Count > 0) { seen = string.Join(" // ", now.ToArray()); outcome = "error"; return true; } }
                if (watch != null && watch.Undecided != null) { outcome = "dialog"; return true; }
                return false;
            }, timeoutMs);
            balloon = seen; return outcome ?? "timeout";
        }

        // never leave a half-filled PropertyManager behind: Cancel it, even when the caller has given up on the call (IgnoreCancel)
        private void CancelPmQuietly(DialogWatchdog watch)
        {
            bool prev = PmUi.IgnoreCancel; PmUi.IgnoreCancel = true;
            try
            {
                string b; var o = PressAndWait(false, watch, 12000, out b);
                ExecLog.Write("convert: cancel -> " + o);
                if (o != "closed") ExecLog.Write("convert: the PropertyManager did NOT close after Cancel (" + o + "); it needs to be closed by hand");
            }
            catch (Exception ex) { ExecLog.Write("convert: cancel failed: " + ex.GetType().Name + " " + ex.Message); }
            finally { PmUi.IgnoreCancel = prev; }
        }

        private static string DialogText()
        {
            var ds = Dialogs.List(); if (ds.Count == 0) return null;
            return "[" + ds[0].Title + "] " + string.Join(" | ", ds[0].Text.ToArray());
        }

        public ExecutionResponse SheetMetalConvert(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = (IPartDoc)doc;
                if (PmUi.FindRoot(_solidWorks, ConvertTitle) != null) throw new ToolFailure("PM_ALREADY_OPEN", "A Convert to Sheet Metal PropertyManager is already open: finish or cancel it first.");
                var dlg0 = DialogText(); if (dlg0 != null) throw new ToolFailure("DIALOG_BLOCKING", "A dialog is open: " + dlg0);
                var plan = ParseConvertPlan(doc, p);                                     // every failure up to here leaves SolidWorks untouched
                if (plan.Thickness.HasValue && plan.Radius.HasValue)
                {
                    var misfit = BendZonesProblem(plan, plan.Thickness.Value, plan.Radius.Value);
                    if (misfit != null) throw new ToolFailure("BEND_ZONES_DO_NOT_FIT", misfit + ". SolidWorks would fail the build (and its OK handler has been seen to hang on it): nothing was opened.");
                }
                if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);      // a sketch still open (the last rip sketch, usually) would swallow the command

                var problems = new JArray(); var picks = new JArray(); var hidden = new List<IBody2>(); bool pmOpen = false; DialogWatchdog watch = null;
                int convertsBefore = TopFeatures(doc).Count(f => f.GetTypeName2() == "SolidToSheetMetal");
                ExecLog.Write("convert: plan ready - " + plan.Targets.Count + " picks, " + plan.Polylines.Count + " edges mapped");
                try
                {
                    // other visible bodies would sit in front of the picks (and could be hit instead): hide them for the duration
                    if (plan.HideOthers)
                        foreach (var b in AllBodies(part, true)) if (!SameBody(b, plan.Body)) { b.HideBody(true); hidden.Add(b); }
                    doc.ClearSelection2(true);
                    var sd = ((ISelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = 1;
                    if (!((IEntity)plan.FixedFace).Select4(false, sd)) throw new ToolFailure("SELECTION_FAILED", "Could not select the fixed face.");
                    watch = new DialogWatchdog();                                          // from here a dialog raised inside one of our clicks can be answered by ANOTHER thread
                    if (!_solidWorks.RunCommand(ConvertCommand, "")) throw new ToolFailure("COMMAND_FAILED", "SolidWorks refused to start Convert to Sheet Metal (is the body a surface or solid body?).");
                    var root = PmUi.WaitForRoot(_solidWorks, ConvertTitle, 10000);
                    if (root == null) throw new ToolFailure("PM_NOT_OPENED", "The Convert to Sheet Metal PropertyManager did not appear within 10 s." + (watch.Count > 0 ? " Dialog: " + string.Join(" / ", watch.Messages.ToArray()) : ""));
                    pmOpen = true; var vis = root.Visual; ExecLog.Write("convert: PropertyManager open");
                    foreach (var g in ConvertGroups) { var ex = PmUi.Find(vis, g); if (ex != null) PmUi.Act(ex, "expand", null); }
                    PmUi.Pump(150);
                    int fixedItems = PmUi.ItemCount(vis, "SheetMetalFixedEnt"); if (fixedItems != 1) throw new ToolFailure("FIXED_FACE_NOT_ACCEPTED", "The PropertyManager holds " + fixedItems + " fixed faces instead of 1.");

                    // thickness, radius, reverse, keep body, relief go in BEFORE the picks; the corner type, rip gap and overlap ratio go in AFTER them: with the Overlap corner type set while rip sketches are
                    // being added SolidWorks raises "Overlaps/underlaps could not be applied to all rips" in the middle of the pick
                    ApplyConvertSettings(vis, plan, problems, false);
                    if (plan.CornerEarly) ApplyConvertSettings(vis, plan, problems, true);
                    // what the PropertyManager really shows now (it re-formats and may clamp): check the bends against THAT
                    var tMm = LengthMm(PmUi.Text(vis, "SheetThickness")); var rMm = LengthMm(PmUi.Text(vis, "DefaultBendRadius"));
                    if (tMm.HasValue && rMm.HasValue)
                    {
                        var misfit = BendZonesProblem(plan, tMm.Value / 1000, rMm.Value / 1000);
                        if (misfit != null) throw new ToolFailure("BEND_ZONES_DO_NOT_FIT", misfit + ". The command was cancelled before anything was picked.");
                    }

                    // ---- picks, in order, each verified -------------------------------------------------------------------------------------------------------------------------------------
                    var graphics = IntPtr.Zero; var caches = new Dictionary<string, ViewCache>(); string goodView = null; string lastList = null; string shownView = null;
                    foreach (var t in plan.Targets)
                    {
                        if (t.List != lastList)
                        {
                            var lb = PmUi.Find(vis, t.List); if (lb == null) throw new ToolFailure("PM_CONTROL_MISSING", "The PropertyManager has no list '" + t.List + "' (a different SolidWorks version?).");
                            PmUi.Act(lb, "focus", null); PmUi.Pump(250); lastList = t.List;
                        }
                        var order = new List<string>(); if (goodView != null) order.Add(goodView); foreach (var v in plan.Views) if (!order.Contains(v)) order.Add(v);
                        bool done = false; var tried = new List<string>();
                        foreach (var view in order)
                        {
                            if (view != "current" && view != shownView) { ShowView(doc, view); shownView = view; }       // a view that is already showing is not re-fitted for every pick
                            else if (view == "current" && shownView != null) continue;                                    // "the current view" no longer exists once another one was shown
                            else PmUi.Pump(60);
                            var mv = (IModelView)doc.ActiveView; var box = PmUi.VisibleBox(mv); var centre = new[] { (box[0] + box[2]) / 2, (box[1] + box[3]) / 2 };
                            if (graphics == IntPtr.Zero)
                            {
                                var at = PmUi.WindowAt(centre[0], centre[1]);
                                if (!PmUi.IsOurs(at)) throw new ToolFailure("GRAPHICS_COVERED", "The SolidWorks graphics area is covered by a window of another application at its centre (" + PmUi.Who(at) + "). Nothing was clicked: bring SolidWorks to the front (or move that window away) and call again.");
                                graphics = at;
                            }
                            SettleView(doc, graphics, centre);
                            ViewCache vc; var ck = view + "#" + string.Join(",", ((double[])((IMathTransform)mv.Transform).ArrayData).Take(13).Select(v => v.ToString("G6", CultureInfo.InvariantCulture)).ToArray());
                            if (!caches.TryGetValue(ck, out vc)) { vc = BuildViewCache(doc, plan, view); caches[ck] = vc; }
                            // candidate pixels along the target on screen; a pixel is good when the nearest OTHER edge is far away (hard against another edge, a click could select that one instead)
                            var scored = new List<Tuple<double, int[], double>>();
                            for (int si = 0; si < t.Samples.Length; si++)
                            {
                                var px = PmUi.Project(mv, t.Samples[si]); if (px[0] < vc.Box[0] + 8 || px[0] > vc.Box[2] - 8 || px[1] < vc.Box[1] + 8 || px[1] > vc.Box[3] - 8) continue;
                                scored.Add(Tuple.Create(ClearanceExcludingSelf(px, vc, t), px, PickFractions[si]));
                            }
                            scored = scored.OrderByDescending(c => c.Item1).Take(5).ToList();
                            foreach (var c in scored)
                            {
                                if (c.Item1 < 4) continue;
                                PmUi.PumpUntil(() => PmUi.WindowAt(c.Item2[0], c.Item2[1]) == graphics, 2000);
                                var atPx = PmUi.WindowAt(c.Item2[0], c.Item2[1]);
                                if (atPx != graphics) { tried.Add(view + "@" + c.Item2[0] + "," + c.Item2[1] + " covered by " + PmUi.Who(atPx)); continue; }      // never click what is not the graphics area: a window of another application may be in front
                                int before = PmUi.ItemCount(vis, t.List);
                                PmUi.Click(c.Item2[0], c.Item2[1], false);
                                if (watch.Undecided != null) throw new ToolFailure("PM_DIALOG", t.Label + ": SolidWorks raised a dialog that needs a decision: " + watch.Undecided);
                                int after = PmUi.ItemCount(vis, t.List);
                                tried.Add(view + "@" + c.Item2[0] + "," + c.Item2[1]);
                                if (after == before + 1)
                                {
                                    JObject last; int ok = NewestSelectionMatches(doc, t, out last);
                                    if (ok == 1) { picks.Add(new JObject { ["target"] = t.Label, ["list"] = t.List, ["mid"] = new JArray(Math.Round(t.Mid[0], 6), Math.Round(t.Mid[1], 6), Math.Round(t.Mid[2], 6)), ["view"] = view, ["pixel"] = new JArray(c.Item2[0], c.Item2[1]), ["clearance_px"] = Math.Round(c.Item1, 1) }); goodView = view; done = true; ExecLog.Write("convert: picked " + t.Label + " in " + view); break; }
                                    throw new ToolFailure("WRONG_PICK", t.Label + ": the click added " + (last == null ? "nothing selectable" : last.ToString(Newtonsoft.Json.Formatting.None)) + " instead of the intended " + (t.IsEdge ? "edge" : "sketch") + " (view " + view + "); the command was cancelled.");
                                }
                            }
                            if (done) break;
                        }
                        if (!done) throw new ToolFailure("NOT_PICKABLE", t.Label + " could not be picked from any view (" + string.Join(", ", tried.Take(12).ToArray()) + "). It may be hidden behind a face or coincide with another edge on screen in every view tried; pass other `views`.");
                    }

                    int bends = PmUi.ItemCount(vis, "BendList"), rips = PmUi.ItemCount(vis, "RipSketches"), ripEdges = Math.Max(0, PmUi.ItemCount(vis, "RipEdges"));
                    int wantBends = plan.Targets.Count(x => x.List == "BendList"), wantRips = plan.Targets.Count(x => x.List == "RipSketches");
                    if (bends != wantBends || rips != wantRips) throw new ToolFailure("LIST_MISMATCH", "The PropertyManager holds " + bends + " bend edges and " + rips + " rip sketches; expected " + wantBends + " and " + wantRips + ".");
                    var summary = new JObject { ["bend_edges"] = bends, ["rip_sketches"] = rips, ["rip_edges_found"] = ripEdges, ["fixed_face"] = plan.FixedLabel };
                    if (!plan.CornerEarly) ApplyConvertSettings(vis, plan, problems, true);      // corner type, rip gap, overlap ratio: after the picks
                    PmUi.Pump(300); ExecLog.Write("convert: all picks done, settings applied");

                    if (!plan.Accept) { pmOpen = true; return new JObject { ["accepted"] = false, ["pm_left_open"] = true, ["lists"] = summary, ["picks"] = picks, ["dialogs"] = new JArray(watch.Messages.Cast<object>().ToArray()), ["problems"] = problems, ["verified"] = problems.Count == 0 }; }

                    // ---- accept and verify ---------------------------------------------------------------------------------------------------------------------------------------------------
                    string balloon; ExecLog.Write("convert: pressing OK");
                    var outcome = PressAndWait(true, watch, 45000, out balloon);
                    ExecLog.Write("convert: OK -> " + outcome + (balloon == null ? "" : " [" + balloon + "]"));
                    if (outcome == "closed") pmOpen = false;
                    else if (outcome == "error") throw new ToolFailure("CONVERT_FAILED", "SolidWorks could not build the sheet-metal part: " + balloon + ". The command was cancelled and nothing was created. (Typical cause: the thickness or bend radius does not fit the rim / lip dimensions of the body.)" + (watch.Count > 0 ? " Dialogs: " + string.Join(" / ", watch.Messages.ToArray()) : ""));
                    else if (outcome == "dialog") throw new ToolFailure("PM_DIALOG", "SolidWorks raised a dialog that needs a decision after OK: " + watch.Undecided);
                    else throw new ToolFailure("PM_STUCK", "OK was pressed and nothing happened within 45 s" + (watch.Count > 0 ? "; dialogs: " + string.Join(" / ", watch.Messages.ToArray()) : "") + ".");
                    PmUi.Pump(300);
                    foreach (var m in watch.Messages) problems.Add("SolidWorks said: " + m);
                    var converts = TopFeatures(doc).Where(f => f.GetTypeName2() == "SolidToSheetMetal").ToList();
                    if (converts.Count <= convertsBefore) throw new ToolFailure("NOT_CREATED", "The PropertyManager closed but no Convert to Sheet Metal feature exists. " + string.Join(" / ", watch.Messages.ToArray()));
                    var feat = converts[converts.Count - 1];
                    if (plan.ReliefRatio.HasValue) { try { ApplyConvertReliefRatio(feat, doc, plan.ReliefRatio.Value, problems); } catch (Exception ex) { problems.Add("relief ratio: " + ex.Message); } }
                    int err = FeatureError(feat);
                    var def = DefinitionValues(feat, doc);
                    VerifyConvertSettings(plan, def, problems);
                    if (plan.KFactor.HasValue) { try { if (!ApplyGlobalKFactor(doc, plan.KFactor.Value)) problems.Add("the global K-factor " + plan.KFactor.Value + " could not be applied"); } catch (Exception ex) { problems.Add("K-factor: " + ex.Message); } }
                    var res = new JObject { ["accepted"] = true, ["feature"] = feat.Name, ["feature_error"] = err, ["lists"] = summary, ["definition"] = def, ["picks"] = picks };
                    try { res["sheet_metal"] = SheetMetalSummary(doc); } catch (Exception ex) { problems.Add("sheet-metal summary: " + ex.Message); }
                    if (problems.Count > 0) res["problems"] = problems;
                    res["verified"] = err == 0 && problems.Count == 0;
                    return res;
                }
                catch (Exception ex)
                {
                    ExecLog.Write("convert: FAILED " + ex.GetType().Name + ": " + ex.Message);
                    if (pmOpen) CancelPmQuietly(watch);                                       // never leave a half-filled PropertyManager behind
                    throw;
                }
                finally
                {
                    if (watch != null) watch.Dispose();
                    foreach (var b in hidden) { try { b.HideBody(false); } catch (COMException) { } }
                    try { doc.ClearSelection2(true); } catch (COMException) { }
                }
            });
        }

        // two COM wrappers for the same body are different objects: compare by their faces' count and first face box
        private static bool SameBody(IBody2 a, IBody2 b)
        {
            try
            {
                if (a.GetFaceCount() != b.GetFaceCount() || a.GetEdgeCount() != b.GetEdgeCount()) return false;
                var ba = a.GetBodyBox() as double[]; var bb = b.GetBodyBox() as double[]; if (ba == null || bb == null) return false;
                for (int i = 0; i < 6; i++) if (Math.Abs(ba[i] - bb[i]) > 1e-9) return false;
                return true;
            }
            catch (COMException) { return false; }
        }

        // nearest distance from a pixel to anything that is NOT the target itself (the target's own polyline passes through the pixel by construction)
        private double ClearanceExcludingSelf(int[] px, ViewCache vc, PickTarget t)
        {
            double best = double.MaxValue; int skipped = 0;
            foreach (var poly in vc.EdgePolylines)
            {
                double d = PixelDistToPolyline(px, poly);
                if (d < 1.5 && skipped < 1 && t.IsEdge) { skipped++; continue; }            // exactly one polyline is the target
                if (d < best) best = d;
            }
            return best;
        }

        // Write every setting into the PropertyManager's own controls - the request's value, or the factory default the plan filled in, so nothing is left to what an earlier call remembered - and read each
        // one back from the control. `late` selects the corner-related group (corner type, rip gap, overlap ratio), which goes in after the picks (the neutral Butt corner is set before them);
        // everything else goes in before them.
        private void ApplyConvertSettings(System.Windows.DependencyObject vis, ConvertPlan plan, JArray problems, bool late)
        {
            Action<string, double?> length = (name, v) =>
            {
                if (!v.HasValue) return; var el = PmUi.Find(vis, name); if (el == null) { problems.Add("PropertyManager has no '" + name + "' control"); return; }
                PmUi.Act(el, "set_text", Mm(v.Value)); var shown = LengthMm(PmUi.Text(vis, name)); if (!shown.HasValue || Math.Abs(shown.Value - v.Value * 1000) > 1e-3) problems.Add(name + " shows '" + PmUi.Text(vis, name) + "' after setting " + Mm(v.Value));       // the box shows the DOCUMENT's unit (0.060in): compare in mm
            };
            Action<string> corner = kind =>
            {
                var el = PmUi.Find(vis, kind == "butt" ? "ButtCorner" : kind == "overlap" ? "OverlapCorner" : "UnderlapCorner");
                if (el == null) { problems.Add("PropertyManager has no " + kind + " corner control"); return; }
                if (!(((System.Windows.Controls.Primitives.ToggleButton)el).IsChecked ?? false)) PmUi.Act(el, "toggle", null);
            };
            if (!late)
            {
                length("SheetThickness", plan.Thickness); length("DefaultBendRadius", plan.Radius);
                PmUi.SetChecked(vis, "ReverseThicknessDir", plan.Reverse ?? true); PmUi.SetChecked(vis, "KeepBody", plan.KeepBody ?? false);
                var rel = PmUi.Find(vis, "ReliefType"); if (rel != null) PmUi.Act(rel, "select_index", plan.Relief == "rectangular" ? "0" : plan.Relief == "obround" ? "1" : "2"); else problems.Add("PropertyManager has no 'ReliefType' control");
                corner("butt");                // the NEUTRAL corner type while rip sketches are added (Overlap / Underlap raise a dialog then); the requested one goes in below, after the picks
            }
            else
            {
                corner(plan.Corner);
                length("DefaultRipGap", plan.RipGap);
                var ov = PmUi.Find(vis, "DefaultRipOverlapRatio"); if (ov != null) PmUi.Act(ov, "set_text", (plan.OverlapRatio ?? 0.5).ToString("0.####", CultureInfo.InvariantCulture)); else problems.Add("PropertyManager has no 'DefaultRipOverlapRatio' control");
            }
            // A numeric box only COMMITS its text when it loses focus (the one edited last kept the old value: RipOverlapRatio stayed 0.5 with "1" typed in): move the focus to a neutral control.
            var neutral = PmUi.Find(vis, "BendList"); if (neutral != null) PmUi.Act(neutral, "focus", null);
            PmUi.Pump(250);
        }

        // The relief RATIO has no usable box in the PropertyManager: ReliefRatio is hidden and disabled behind an "Override default parameters" check box that is hidden as well (toggling it changes nothing),
        // and the feature simply takes the template's value. The created feature's definition does accept it - OverrideDefaultAutoReliefParameters + ReliefRatio through ModifyDefinition, read back as
        // asked and without a rebuild error - so that is where the request's relief_ratio goes.
        private void ApplyConvertReliefRatio(IFeature feat, IModelDoc2 doc, double ratio, JArray problems)
        {
            object def = feat.GetDefinition(); var iface = typeof(IConvertSolidFeatureData); bool access = AccessSelections(iface, def, doc);
            try
            {
                var d = (IConvertSolidFeatureData)def; d.OverrideDefaultAutoReliefParameters = true; d.ReliefRatio = ratio;
                if (!feat.ModifyDefinition(def, doc, null)) problems.Add("ModifyDefinition refused the relief ratio " + ratio.ToString("0.####", CultureInfo.InvariantCulture));
            }
            finally { if (access) ReleaseSelections(iface, def); }
        }

        // the created feature is the ground truth: compare every requested setting with its definition
        private static void VerifyConvertSettings(ConvertPlan plan, JObject def, JArray problems)
        {
            Func<string, JToken> val = n => def == null ? null : def[n];
            Action<string, double?, double> numeric = (name, want, tol) =>
            {
                if (!want.HasValue) return; var got = val(name); if (got == null) { problems.Add(name + " is not in the feature definition"); return; }
                if (Math.Abs(got.Value<double>() - want.Value) > tol) problems.Add(name + " is " + got.Value<double>().ToString("G6", CultureInfo.InvariantCulture) + " in the feature, not " + want.Value.ToString("G6", CultureInfo.InvariantCulture));
            };
            numeric("SheetThickness", plan.Thickness, 1e-7); numeric("BendRadius", plan.Radius, 1e-7); numeric("RipGap", plan.RipGap, 1e-7); numeric("RipOverlapRatio", plan.OverlapRatio, 1e-6);
            numeric("ReliefRatio", plan.ReliefRatio, 1e-6);                  // only when the request gave one (otherwise the template's value stands)
            if (plan.Reverse.HasValue) { var g = val("ReverseThickness"); if (g == null || g.Value<bool>() != plan.Reverse.Value) problems.Add("ReverseThickness is " + g + " in the feature, not " + plan.Reverse.Value); }
            if (plan.KeepBody.HasValue) { var g = val("KeepBody"); if (g == null || g.Value<bool>() != plan.KeepBody.Value) problems.Add("KeepBody is " + g + " in the feature, not " + plan.KeepBody.Value); }
            if (plan.Corner.Length > 0) { int w = plan.Corner == "butt" ? 0 : plan.Corner == "overlap" ? 1 : 2; var g = val("CornerDefaults"); if (g == null || g.Value<int>() != w) problems.Add("CornerDefaults is " + g + " in the feature, not " + w + " (" + plan.Corner + ")"); }
            if (plan.Relief.Length > 0) { int w = plan.Relief == "rectangular" ? 0 : plan.Relief == "obround" ? 1 : 2; var g = val("ReliefType"); if (g == null || g.Value<int>() != w) problems.Add("ReliefType is " + g + " in the feature, not " + w + " (" + plan.Relief + ")"); }
        }
    }
}
