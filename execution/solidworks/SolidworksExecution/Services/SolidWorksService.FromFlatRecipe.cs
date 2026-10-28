// sheet_metal_from_flat, the second route: the FLANGE RECIPE for a flat whose corners are CLOSED.
// A sketched bend folds along the whole line, so a flat whose wall edges reach past the neighbouring bend line (a mitre flange, Convert, closed-corner flanges) cannot be folded with sketched bends - the plan
// refuses it. One family of those flats can be rebuilt from FLANGES, exactly: the box with ONE profile all round (a wall, a rim, a lip ...), made in SolidWorks by a base flange and ONE mitre flange along the
// four edges of the plate. Its flat is recognisable without any region analysis: the bend lines are NESTED RECTANGLES (one ring of four lines per bend, symmetric about the plate's centre, the same strip width
// on every side), and everything the build needs follows from them and from the sheet (thickness, radius, K):
//     plate (outer sharp)  W = 2 (a1 - BA/2 + r + t)           a1 = half span of the innermost ring, BA = pi/2 (r + K t)
//     strip i (flat)       f_i = a_(i+1) - a_i - BA            the last one f_n = (the outline beyond the last ring) - BA/2
//     profile segment i    P_i = f_i + sb_i + sb_(i+1) (- t for the first: the profile starts on the plate's TOP face)
//                          sb = setback of the bend on the face the profile follows: r + t when the bend folds up, r when it folds down
//     profile points       the walk from (0, 0): 'up' first, a bend that folds up turns toward the plate's interior, one that folds down turns away
// (measured on the Path B cabinet: nested rings at 5.9226 / 9.8277 / 10.4829 in -> plate 12 x 18 in, profile [[0, 4], [0.75, 4], [0.75, 4.5]] in = the numbers the part was made from). The proof is the part's
// own flat pattern: its blank, its bends and its outer cutting length must be the DXF's. What the recipe does not cover is said in RECIPE_NOT_APPLICABLE (strips that differ from side to side, a bend line
// that is not parallel to an axis, a first bend that folds down ...). Lengths are METERS.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        private sealed class MiterRecipe
        {
            public double Cx, Cy;                                                             // centre of the nested rectangles of bend lines (DXF coordinates)
            public List<double> HalfX = new List<double>(), HalfY = new List<double>();       // half spans of the rings (= bend line positions from the centre), innermost first
            public List<string> Dirs = new List<string>();                                    // direction of each ring of bends, innermost first
            public double Free;                                                               // width of the strip beyond the last ring, measured from its bend line to the outline
            public double Edge = double.NaN;                                                  // how far the wall edges stand beyond the first ring's bend lines (the rip gap shows here)
            public double RipGap = double.NaN;                                                // the gap at the mitres that puts the wall edges there (needs the sheet)
            public bool HasSheet; public double T, R, K, Ba, PlateW, PlateL;                  // the sheet, and what follows from it
            public List<double> Flat = new List<double>(), Segments = new List<double>(); public List<double[]> Points = new List<double[]>();
        }

        private static bool RecipeCandidate(FlatPlan plan) { return plan != null && plan.Outer != null && plan.Bends != null && plan.Bends.Count >= 4; }

        // The plan of the sketched-bend route or of the recipe. strategy: 'sketched_bends' (default: the recipe is never built, but a refusal says when the flat is a mitre-flange box) | 'auto' (sketched bends when the flat
        // folds that way, else the recipe when the flat fits it) | 'miter_flange' (the recipe, whether or not the sketched bends could fold the flat). `refusal` is why the sketched bends cannot fold it (null when they can).
        private FlatPlan PlanRoute(JObject p, out MiterRecipe recipe, out string route, out ToolFailure refusal)
        {
            string strategy = (p.Value<string>("strategy") ?? "sketched_bends").Trim().ToLowerInvariant();
            if (strategy != "auto" && strategy != "sketched_bends" && strategy != "miter_flange") throw new ToolFailure("INVALID_PARAMETER", "strategy must be 'sketched_bends' (default), 'auto' or 'miter_flange'.");
            recipe = null; refusal = null; route = "sketched_bends"; var plan = new FlatPlan();
            try { PlanFlat(p, plan); }
            catch (ToolFailure f)
            {
                if (!RecipeCandidate(plan)) throw;
                refusal = f;
            }
            if (refusal == null && strategy != "miter_flange") return plan;
            if (strategy == "sketched_bends")
            {
                string hint = null;
                try { PlanMiterRecipe(plan, p); hint = " The bend lines are nested rectangles, the flat of a box made by ONE mitre flange: strategy = 'miter_flange' (or 'auto') builds it from a base flange and a mitre flange instead."; } catch (ToolFailure) { }
                throw hint == null ? refusal : new ToolFailure(refusal.Code, refusal.Message + hint);
            }
            try { recipe = PlanMiterRecipe(plan, p); route = "miter_flange"; return plan; }
            catch (ToolFailure f2)
            {
                if (f2.Code != "RECIPE_NOT_APPLICABLE") throw;
                if (strategy == "miter_flange") throw refusal == null ? f2 : new ToolFailure(f2.Code, f2.Message + " (The sketched bends refuse this flat as well - " + refusal.Code + ": " + refusal.Message + ")");
                throw new ToolFailure(refusal.Code, refusal.Message + " The flange recipe (a base flange and one mitre flange) does not fit this flat either: " + f2.Message);       // auto: the sketched bends' refusal stays the answer
            }
        }

        private MiterRecipe PlanMiterRecipe(FlatPlan plan, JObject p)
        {
            Func<string, ToolFailure> no = why => new ToolFailure("RECIPE_NOT_APPLICABLE", why);
            int count = plan.Bends == null ? 0 : plan.Bends.Count;
            if (count < 4 || count % 4 != 0) throw no("the flat of a mitre flange has its bend lines in rings of four (one line on each side of the plate per bend); this flat has " + count + " bend lines");
            const double ta = 2e-5, ct = 5e-5, tolSym = 1e-4, tolStrip = 1e-4;
            var vs = new List<KeyValuePair<double, FlatBend>>(); var hs = new List<KeyValuePair<double, FlatBend>>();
            foreach (var b in plan.Bends)
            {
                if (Math.Abs(b.A[0] - b.B[0]) <= ta) vs.Add(new KeyValuePair<double, FlatBend>((b.A[0] + b.B[0]) / 2, b));
                else if (Math.Abs(b.A[1] - b.B[1]) <= ta) hs.Add(new KeyValuePair<double, FlatBend>((b.A[1] + b.B[1]) / 2, b));
                else throw no("the bend line " + Pt2(b.A) + " -> " + Pt2(b.B) + " is not parallel to an axis (the recipe knows nested rectangles of bend lines)");
            }
            vs = vs.OrderBy(kv => kv.Key).ToList(); hs = hs.OrderBy(kv => kv.Key).ToList();
            if (vs.Count != hs.Count || vs.Count % 2 != 0 || vs.Count < 2) throw no("the bend lines are not rings of four sides: " + vs.Count + " run across x and " + hs.Count + " across y");
            for (int i = 0; i + 1 < vs.Count; i++) if (vs[i + 1].Key - vs[i].Key <= ct) throw no("two bend lines lie on the same vertical line (x = " + Mmf(vs[i].Key) + " mm)");
            for (int i = 0; i + 1 < hs.Count; i++) if (hs[i + 1].Key - hs[i].Key <= ct) throw no("two bend lines lie on the same horizontal line (y = " + Mmf(hs[i].Key) + " mm)");
            int n = vs.Count / 2;
            var rc = new MiterRecipe { Cx = (vs[0].Key + vs[2 * n - 1].Key) / 2, Cy = (hs[0].Key + hs[2 * n - 1].Key) / 2 }; var ringHidden = new List<bool>();
            for (int k = 0; k < n; k++)
            {
                var l = vs[n - 1 - k]; var rt = vs[n + k]; var bt = hs[n - 1 - k]; var tp = hs[n + k];
                if (Math.Abs(l.Key + rt.Key - 2 * rc.Cx) > tolSym || Math.Abs(bt.Key + tp.Key - 2 * rc.Cy) > tolSym)
                    throw no("ring " + (k + 1) + " of the bend lines is not symmetrical about the plate's centre (x = " + Mmf(l.Key) + " and " + Mmf(rt.Key) + " mm, y = " + Mmf(bt.Key) + " and " + Mmf(tp.Key) + " mm)");
                bool hid = l.Value.Hidden;
                if (rt.Value.Hidden != hid || bt.Value.Hidden != hid || tp.Value.Hidden != hid) throw no("the four sides of ring " + (k + 1) + " are drawn in different linetypes");
                if (Math.Abs(l.Value.Length - rt.Value.Length) > tolSym || Math.Abs(bt.Value.Length - tp.Value.Length) > tolSym) throw no("opposite sides of ring " + (k + 1) + " have different lengths");
                rc.HalfX.Add((rt.Key - l.Key) / 2); rc.HalfY.Add((tp.Key - bt.Key) / 2); ringHidden.Add(hid);
            }
            // one profile all round: every strip is as wide across x as across y, and so is the free strip beyond the last ring
            for (int k = 0; k + 1 < n; k++)
            {
                double wx = rc.HalfX[k + 1] - rc.HalfX[k], wy = rc.HalfY[k + 1] - rc.HalfY[k];
                if (Math.Abs(wx - wy) > tolStrip) throw no("strip " + (k + 1) + " is " + Mmf(wx) + " mm wide on the left and right sides and " + Mmf(wy) + " mm on the top and bottom: a mitre flange has ONE profile all round (differential flange sizes need the Convert pipeline or separate flanges)");
            }
            var ex = new[] { plan.Box[2] - rc.Cx - rc.HalfX[n - 1], rc.Cx - plan.Box[0] - rc.HalfX[n - 1], plan.Box[3] - rc.Cy - rc.HalfY[n - 1], rc.Cy - plan.Box[1] - rc.HalfY[n - 1] };
            if (ex.Max() - ex.Min() > tolStrip) throw no("the strip beyond the last ring is " + string.Join(" / ", ex.Select(Mmf).ToArray()) + " mm on the four sides: a mitre flange has ONE profile all round");
            rc.Free = ex.Average(); if (rc.Free <= 1e-5) throw no("nothing lies beyond the last ring of bend lines (the outline ends at it)");
            double bestLen = -1;       // the wall edges: the longest vertical outline line within 5 mm of the first ring (where the rip gap shows; it stands inside the bend line for a wide gap, outside for a narrow one)
            foreach (var e in plan.Outer.Ents)
                if (e.Kind == "line" && Math.Abs(e.A[0] - e.B[0]) <= ta)
                {
                    double d = Math.Abs(e.A[0] - rc.Cx) - rc.HalfX[0]; double len = Dist2(e.A, e.B);
                    if (Math.Abs(d) < 0.005 && len > bestLen) { bestLen = len; rc.Edge = d; }
                }

            // directions, as the plan of the sketched bends reads them: from the caller, else the two linetypes, else all up
            string hiddenDir = (p.Value<string>("hidden_direction") ?? "up").ToLowerInvariant(); if (hiddenDir != "up" && hiddenDir != "down") throw new ToolFailure("INVALID_PARAMETER", "hidden_direction must be 'up' or 'down'.");
            var dirsTok = p["directions"]; JArray dirList = null; JObject dirMap = null;
            if (dirsTok != null && dirsTok.Type != JTokenType.Null)
            {
                if (dirsTok.Type == JTokenType.Object) dirMap = (JObject)dirsTok;
                else if (dirsTok.Type == JTokenType.String && ((string)dirsTok).TrimStart().StartsWith("{", StringComparison.Ordinal)) dirMap = AsObject(dirsTok, "directions");
                else dirList = AsArray(dirsTok, "directions");
            }
            bool mixed = ringHidden.Any(h => h) && ringHidden.Any(h => !h);
            for (int k = 0; k < n; k++)
            {
                string d; string key = (k + 1).ToString(CultureInfo.InvariantCulture);
                if (dirList != null && dirList.Count > 0) d = (string)dirList[Math.Min(k, dirList.Count - 1)];
                else if (dirMap != null && dirMap[key] != null) d = (string)dirMap[key];
                else if (mixed) d = ringHidden[k] ? hiddenDir : (hiddenDir == "up" ? "down" : "up");
                else d = (hiddenDir == "down" && ringHidden[k]) ? "down" : "up";
                d = (d ?? "up").Trim().ToLowerInvariant(); if (d != "up" && d != "down") throw new ToolFailure("INVALID_PARAMETER", "A direction must be 'up' or 'down', not '" + d + "'.");
                rc.Dirs.Add(d);
            }

            // the sheet: without it the structure is reported, with it the plate and the profile
            double? t = p.Value<double?>("thickness"), r = p.Value<double?>("bend_radius"); double k0 = p.Value<double?>("k_factor") ?? 0.5;
            if (t.HasValue && t.Value > 0 && r.HasValue && r.Value > 0)
            {
                if (Math.Abs((p.Value<double?>("angle") ?? 90.0) - 90.0) > 1e-9) throw no("the recipe's bends are 90 degrees (angle = " + p.Value<double?>("angle") + ")");
                if (rc.Dirs[0] != "up") throw no("the first ring of bends folds DOWN: the recipe builds the walls up from the plate's top face (pass directions with 'up' first)");
                if (k0 <= 0 || k0 >= 1) throw new ToolFailure("INVALID_PARAMETER", "k_factor must lie between 0 and 1.");
                rc.HasSheet = true; rc.T = t.Value; rc.R = r.Value; rc.K = k0; rc.Ba = Math.PI / 2 * (rc.R + rc.K * rc.T);
                rc.PlateW = 2 * (rc.HalfX[0] - rc.Ba / 2 + rc.R + rc.T); rc.PlateL = 2 * (rc.HalfY[0] - rc.Ba / 2 + rc.R + rc.T);
                // the rip gap g: the mitre end face of a wall stands g/2 from the 45 degree mitre plane through the plate's outer corner, so on the wall's INNER face the wall edge of the flat lies at
                //     x_edge = X - t - g / sqrt(2)      (X = the plate's half width, the outer plane)       -> g = sqrt(2) (X - t - x_edge)       (Path B: 5.932929 = 6 - 0.06 - 0.01 / sqrt(2), exact)
                if (!double.IsNaN(rc.Edge))
                {
                    double g = Math.Sqrt(2.0) * (rc.PlateW / 2 - rc.T - (rc.HalfX[0] + rc.Edge));
                    if (g > -1e-7 && g < 0.005) rc.RipGap = Math.Max(0.0, g);
                }
                Func<int, double> setback = i => rc.Dirs[i] == "up" ? rc.R + rc.T : rc.R;      // on the face the profile follows: the outside of a bend that folds up, the inside of one that folds down
                double ang = 90.0, x = 0, y = 0;
                for (int i = 0; i < n; i++)
                {
                    double flat = i + 1 < n ? rc.HalfX[i + 1] - rc.HalfX[i] - rc.Ba : rc.Free - rc.Ba / 2;
                    if (flat <= 1e-5) throw no("strip " + (i + 1) + " is only " + Mmf(flat) + " mm flat between its bend zones (the bend allowance is " + Mmf(rc.Ba) + " mm): are thickness, bend_radius and k_factor right?");
                    double len = flat + setback(i) + (i + 1 < n ? setback(i + 1) : 0.0) - (i == 0 ? rc.T : 0.0);
                    if (i > 0) ang += rc.Dirs[i] == "up" ? -90.0 : 90.0;
                    x += len * Math.Cos(ang * Math.PI / 180); y += len * Math.Sin(ang * Math.PI / 180);
                    rc.Flat.Add(flat); rc.Segments.Add(len); rc.Points.Add(new[] { Math.Round(x, 9), Math.Round(y, 9) });
                }
            }
            return rc;
        }

        private static JObject MiterRecipeJson(MiterRecipe rc)
        {
            var j = new JObject
            {
                ["kind"] = "miter_flange", ["rings"] = rc.HalfX.Count, ["centre"] = new JArray(rc.Cx, rc.Cy),
                ["bend_half_spans_x"] = new JArray(rc.HalfX.Select(v => (object)Math.Round(v, 9)).ToArray()), ["bend_half_spans_y"] = new JArray(rc.HalfY.Select(v => (object)Math.Round(v, 9)).ToArray()),
                ["directions"] = new JArray(rc.Dirs.ToArray()), ["free_strip"] = Math.Round(rc.Free, 9)
            };
            if (!double.IsNaN(rc.Edge)) j["wall_edge_offset"] = Math.Round(rc.Edge, 9);
            if (rc.HasSheet)
            {
                j["bend_allowance"] = rc.Ba; j["plate"] = new JArray(Math.Round(rc.PlateW, 9), Math.Round(rc.PlateL, 9));
                if (!double.IsNaN(rc.RipGap)) j["rip_gap"] = Math.Round(rc.RipGap, 9);
                j["flat_strips"] = new JArray(rc.Flat.Select(v => (object)Math.Round(v, 9)).ToArray()); j["profile_segments"] = new JArray(rc.Segments.Select(v => (object)Math.Round(v, 9)).ToArray());
                j["profile_points"] = new JArray(rc.Points.Select(q => (object)new JArray(q[0], q[1])).ToArray());
            }
            else j["note"] = "pass thickness, bend_radius (and k_factor) to get the plate rectangle and the profile points";
            return j;
        }

        // what a plan_only call answers on the recipe route: the same facts as the plan of the sketched bends, minus the regions (the sketched bends cannot cut this flat)
        private static JObject RecipePlanJson(FlatPlan plan, MiterRecipe rc, ToolFailure refusal)
        {
            var seen = new JObject(); foreach (var kv in plan.Seen) seen[kv.Key] = kv.Value;
            var bends = new JArray();
            for (int i = 0; i < plan.Bends.Count; i++) { var b = plan.Bends[i]; bends.Add(new JObject { ["index"] = i, ["from"] = new JArray(b.A[0], b.A[1]), ["to"] = new JArray(b.B[0], b.B[1]), ["length"] = Math.Round(b.Length, 9), ["linetype"] = b.Hidden ? "hidden" : "continuous" }); }
            var res = new JObject
            {
                ["dxf_path"] = plan.Dxf, ["units"] = plan.Units, ["scale_to_meters"] = plan.Scale, ["entities"] = seen,
                ["outline"] = new JObject { ["pieces"] = plan.Outer.Ents.Count, ["holes"] = plan.Holes.Count, ["box"] = new JArray(plan.Box[0], plan.Box[1], plan.Box[2], plan.Box[3]), ["size"] = new JArray(plan.Box[2] - plan.Box[0], plan.Box[3] - plan.Box[1]) },
                ["bends"] = bends, ["recipe"] = MiterRecipeJson(rc), ["warnings"] = new JArray(plan.Warnings.ToArray())
            };
            if (refusal != null) res["sketched_bends_refusal"] = new JObject { ["code"] = refusal.Code, ["message"] = refusal.Message };
            return res;
        }

        private static double PolyLength(List<double[]> poly) { double s = 0; for (int i = 0; i + 1 < poly.Count; i++) s += Dist2(poly[i], poly[i + 1]); if (poly.Count > 2) s += Dist2(poly[poly.Count - 1], poly[0]); return s; }

        private JObject BuildMiterRecipe(IModelDoc2 doc, IPartDoc part, JObject p, HashSet<string> names0, FlatPlan plan, MiterRecipe rc, ToolFailure refusal)
        {
            if (!rc.HasSheet) throw new ToolFailure("MISSING_PARAMETER", "thickness and bend_radius are required (meters, > 0): the DXF does not say them.");
            var bodies0 = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
            if ((bodies0 != null && bodies0.Length > 0) || TopFeatures(doc).Any(f => f.GetTypeName2() == "ProfileFeature")) throw new ToolFailure("NOT_EMPTY_PART", "The active part is not empty: open a new part for the flat (open_new_part).");
            double ripGap = p.Value<double?>("rip_gap") ?? (double.IsNaN(rc.RipGap) ? 0.0000762 : rc.RipGap);       // the caller's, else the one the wall edges of the DXF show, else SolidWorks' default
            if (ripGap < 0 || ripGap > 0.005) throw new ToolFailure("INVALID_PARAMETER", "rip_gap is the gap at the mitres in METERS (default 0.0000762 = 0.003 in).");
            var steps = new JArray(); var problems = new JArray();
            var rect = FlatStep("sketch_center_rectangle", new JObject { ["plane"] = "Front Plane", ["width"] = rc.PlateW, ["height"] = rc.PlateL }, "plate sketch");
            if (rect.Value<bool?>("verified") != true) throw new ToolFailure("BUILD_STEP_FAILED", "plate sketch: the rectangle was not drawn as asked (" + rect.ToString(Newtonsoft.Json.Formatting.None) + ").");
            FlatStep("sheet_metal_feature", new JObject { ["feature_type"] = "base_flange", ["thickness"] = rc.T, ["bend_radius"] = rc.R, ["k_factor"] = rc.K }, "base flange"); FlatCheckNew(doc, names0, "base flange");
            steps.Add("base flange: plate " + Mmf(rc.PlateW) + " x " + Mmf(rc.PlateL) + " mm");
            double hx = rc.PlateW / 2, hy = rc.PlateL / 2;       // the four edges of the plate's top face, counter-clockwise from +x: the chain of the mitre flange
            var edges = new JArray(new JArray(hx, 0.0, 0.0), new JArray(0.0, hy, 0.0), new JArray(-hx, 0.0, 0.0), new JArray(0.0, -hy, 0.0));
            var pp = new JArray(); foreach (var q in rc.Points) pp.Add(new JArray(q[0], q[1]));
            var mf = FlatStep("sheet_metal_miter_flange", new JObject { ["points"] = edges, ["profile_points"] = pp, ["rip_gap"] = ripGap, ["trim_side_bends"] = false, ["dimension_profile"] = false, ["bend_radius"] = rc.R, ["k_factor"] = rc.K }, "mitre flange");
            FlatCheckNew(doc, names0, "mitre flange"); steps.Add("mitre flange round the plate: profile " + pp.ToString(Newtonsoft.Json.Formatting.None) + ", rip gap " + Mmf(ripGap) + " mm");
            doc.EditRebuild3();

            var res = new JObject { ["route"] = "miter_flange", ["recipe"] = MiterRecipeJson(rc), ["steps"] = steps, ["thickness"] = rc.T, ["bend_radius"] = rc.R, ["k_factor"] = rc.K, ["rip_gap"] = ripGap };
            if (refusal != null) res["sketched_bends_refusal"] = new JObject { ["code"] = refusal.Code, ["message"] = refusal.Message };
            res["features"] = new JArray(TopFeatures(doc).Where(f => !names0.Contains(f.Name) && f.GetTypeName2() != "ProfileFeature").Select(f => (object)f.Name).ToArray());
            foreach (var f in TopFeatures(doc)) { int err = FeatureError(f); if (err != 0 && !names0.Contains(f.Name)) problems.Add("feature " + f.Name + " reports rebuild error " + err); }

            // ---- proof: the part's own flat pattern is the DXF's - blank, bends, outer cutting length (a mitre flange keeps its bends inside the one feature: the bends are counted in the cut list)
            res["bends_expected"] = plan.Bends.Count;
            var fl = FlatStep("flat_pattern_control", new JObject(), "flat pattern");
            var size = fl["flat"] == null || fl["flat"]["size"] == null ? null : fl["flat"]["size"].Select(x => x.Value<double>()).OrderByDescending(x => x).ToArray();
            if (size != null)
            {
                var want = new[] { plan.Box[2] - plan.Box[0], plan.Box[3] - plan.Box[1] }.OrderByDescending(x => x).ToArray();
                double dev = Math.Max(Math.Abs(size[0] - want[0]), Math.Abs(size[1] - want[1]));
                res["flat_size"] = new JArray(size[0], size[1], size[2]); res["dxf_size"] = new JArray(want[0], want[1]); res["flat_deviation"] = Math.Round(dev, 9);
                if (dev > 1e-4) problems.Add("the part's flat pattern is " + Mmf(size[0]) + " x " + Mmf(size[1]) + " mm, the DXF outline " + Mmf(want[0]) + " x " + Mmf(want[1]) + " mm");
            }
            else problems.Add("the flat pattern could not be measured");
            try
            {
                var sp = FlatStep("sheet_metal_params", new JObject { ["action"] = "get" }, "sheet-metal parameters");
                var cl = sp["cut_list"] as JArray; var props = cl != null && cl.Count > 0 ? cl[0]["properties"] as JObject : null; var cut = props == null ? null : props["Cutting Length-Outer"];
                int bendsIn; if (props != null && props["Bends"] != null && int.TryParse(props["Bends"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out bendsIn))
                {
                    res["bends"] = bendsIn; if (bendsIn != plan.Bends.Count) problems.Add("the part has " + bendsIn + " bends, the DXF has " + plan.Bends.Count + " bend lines");
                }
                else problems.Add("the part's bends could not be counted in the cut list");
                string unit = ((string)sp.SelectToken("units.length") ?? "mm").ToLowerInvariant(); double perUnit = unit == "in" ? 0.0254 : unit == "cm" ? 0.01 : unit == "m" ? 1.0 : unit == "ft" ? 0.3048 : 0.001;
                double got;
                if (cut != null && double.TryParse(cut.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out got))
                {
                    got *= perUnit; double wantCut = PolyLength(plan.Outer.Poly); double resolution = perUnit * 0.01;      // the cut list prints two decimals of the unit
                    res["cutting_length"] = Math.Round(got, 9); res["dxf_outline_length"] = Math.Round(wantCut, 9); res["cutting_length_deviation"] = Math.Round(got - wantCut, 9);
                    if (Math.Abs(got - wantCut) > Math.Max(5e-5, resolution)) problems.Add("the outer cutting length of the part's flat pattern is " + Mmf(got) + " mm, the DXF outline " + Mmf(wantCut) + " mm: the corners differ (rip_gap? this DXF may not come from a mitre flange)");
                }
                else problems.Add("the part's outer cutting length could not be read from the cut list");
            }
            catch (ToolFailure ex) { problems.Add("the cut list could not be read: " + ex.Message); }
            if (plan.Holes.Count > 0) problems.Add("the DXF has " + plan.Holes.Count + " inner loops (holes) which the flange recipe does not cut");
            var folded = SolidSize(part); if (folded != null) res["folded_size"] = new JArray(folded.Select(v => (object)Math.Round(v, 9)).ToArray());
            if (problems.Count > 0) res["problems"] = problems;
            res["verified"] = problems.Count == 0;
            return res;
        }
    }
}
