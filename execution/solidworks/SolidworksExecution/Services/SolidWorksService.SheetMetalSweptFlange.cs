// sheet_metal_swept_flange: the SWEPT FLANGE (a profile swept along a path - a bracket, a channel, a flared rim - as sheet metal) through CreateDefinition(swFmSweptFlange = 117) -> ISweptFlangeFeatureData ->
// CreateFeature (probes 75 and 80; there is no Insert call). The first attempt killed SolidWorks and the next ones returned null; the INPUT that decides is the FORM of the path: only an array of
// DispatchWrapper holding the path sketch's FEATURE is recognised (GetPathType() answers 9; the feature, the ISketch, a segment or an array of segments leave it at -1 and CreateFeature returns null), the
// profile is the ISketch. With that the swept flange is the FIRST feature of an empty part (it makes the Sheet-Metal feature itself from the thickness and radius given here) and builds for an L, a U, a Z and
// a single line as the profile and for a straight line, a path with a corner and an arc as the path. Measured: the profile line is the OUTER face of the bend (the material lies inside the corner) and
// ReverseDirection puts the material on the other side (the profile is then the inner face); StartOffset / EndOffset shorten the flange at the two ends of the path; FlangePosition, MaterialInside and TrimSideBends
// changed nothing on a first feature. For a profile of straight segments along a straight path the part is checked against the closed forms of the sheet-metal maths:
//     cross-section = t [ sum L - sum 2 s + sum theta (r + t/2) ]      s = (r + t) tan(theta/2) where a bend turns toward the material, r tan(theta/2) where it turns away
//     developed profile = sum L - sum 2 s + sum theta (r + K t)       volume = cross-section x path, blank = developed profile x path
// Lengths METERS.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        // the straight segments of a sketch as one open chain of points (sketch coordinates, meters), or null when the sketch is not one chain of lines
        private static List<double[]> SketchLineChain(ISketch sk)
        {
            var segs = sk.GetSketchSegments() as object[]; if (segs == null || segs.Length == 0) return null;
            var edges = new List<double[][]>();
            foreach (var o in segs)
            {
                var sg = o as ISketchSegment; if (sg == null || sg.ConstructionGeometry) continue;
                var ln = o as ISketchLine; if (ln == null || sg.GetType() != (int)swSketchSegments_e.swSketchLINE) return null;
                var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint; if (a == null || b == null) return null;
                edges.Add(new[] { new[] { a.X, a.Y }, new[] { b.X, b.Y } });
            }
            if (edges.Count == 0) return null;
            Func<double[], string> key = pt => Math.Round(pt[0] * 1e7).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(pt[1] * 1e7).ToString(CultureInfo.InvariantCulture);
            var adj = new Dictionary<string, List<int>>(); var pos = new Dictionary<string, double[]>();
            for (int i = 0; i < edges.Count; i++) foreach (var e in edges[i]) { string k = key(e); List<int> l; if (!adj.TryGetValue(k, out l)) { l = new List<int>(); adj[k] = l; pos[k] = e; } l.Add(i); }
            var ends = adj.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList(); if (ends.Count != 2 || adj.Any(kv => kv.Value.Count > 2)) return null;
            var chain = new List<double[]> { pos[ends[0]] }; string cur = ends[0]; int prev = -1;
            for (int step = 0; step < edges.Count; step++)
            {
                int next = -1; foreach (int i in adj[cur]) if (i != prev) { next = i; break; }
                if (next < 0) return null;
                var e = edges[next]; string k0 = key(e[0]), k1 = key(e[1]); cur = k0 == cur ? k1 : k0; chain.Add(pos[cur]); prev = next;
            }
            return chain.Count == edges.Count + 1 ? chain : null;
        }

        public ExecutionResponse SheetMetalSweptFlange(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_swept_flange needs an active part.");
                string profName = p.Value<string>("profile_sketch"), pathName = p.Value<string>("path_sketch");
                if (string.IsNullOrWhiteSpace(profName) || string.IsNullOrWhiteSpace(pathName)) throw new ToolFailure("MISSING_PARAMETER", "profile_sketch and path_sketch: the names of the profile sketch (an open profile at the START of the path, on a plane perpendicular to it) and of the path sketch.");
                if (string.Equals(profName, pathName, StringComparison.OrdinalIgnoreCase)) throw new ToolFailure("INVALID_PARAMETER", "profile_sketch and path_sketch must be two different sketches.");
                double t = p.Value<double?>("thickness") ?? 0.0; if (t <= 0 || t > 0.05) throw new ToolFailure("MISSING_PARAMETER", "thickness (meters, > 0 and at most 50 mm) is required: the sheet thickness.");
                double r = p.Value<double?>("bend_radius") ?? t; if (r <= 0 || r > 0.1) throw new ToolFailure("INVALID_PARAMETER", "bend_radius is the INSIDE radius in METERS, > 0 and below 100 mm (default: the thickness).");
                bool reverse = p.Value<bool?>("reverse") ?? false; double so = p.Value<double?>("start_offset") ?? 0.0, eo = p.Value<double?>("end_offset") ?? 0.0; bool? trim = p.Value<bool?>("trim_side_bends");
                if (so < 0 || eo < 0) throw new ToolFailure("INVALID_PARAMETER", "start_offset and end_offset are lengths in METERS and cannot be negative.");
                try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
                var profFeat = FindFeatureByName(doc, profName, null); var pathFeat = FindFeatureByName(doc, pathName, null);
                if (profFeat == null || profFeat.GetTypeName2() != "ProfileFeature") throw new ToolFailure("SKETCH_NOT_FOUND", "No sketch named '" + profName + "'.");
                if (pathFeat == null || pathFeat.GetTypeName2() != "ProfileFeature") throw new ToolFailure("SKETCH_NOT_FOUND", "No sketch named '" + pathName + "'.");
                var profSk = profFeat.GetSpecificFeature2() as ISketch; var pathSk = pathFeat.GetSpecificFeature2() as ISketch; if (profSk == null || pathSk == null) throw new ToolFailure("SKETCH_NOT_FOUND", "The sketches could not be read.");
                var profChain = SketchLineChain(profSk); var pathChain = SketchLineChain(pathSk);
                double pathLength = pathChain != null && pathChain.Count == 2 ? Math.Sqrt(Math.Pow(pathChain[1][0] - pathChain[0][0], 2) + Math.Pow(pathChain[1][1] - pathChain[0][1], 2)) : 0.0;

                var names0 = TopNames(doc); int bodies0 = (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? new object[0]).Length; double vol0 = 0; try { vol0 = ExactVolume(doc); } catch (Exception) { }
                var def = doc.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmSweptFlange) as ISweptFlangeFeatureData;
                if (def == null) throw new ToolFailure("SWEPT_FLANGE_FAILED", "CreateDefinition(swFmSweptFlange) returned nothing.");
                def.OverrideDefaultSheetMetalParameters = true; def.Thickness = t; def.UseDefaultRadius = false; def.BendRadius = r;
                def.Path = new[] { new DispatchWrapper(pathFeat) };      // ONLY this form is recognised as a path (GetPathType = 9)
                def.Profile = profSk;
                if (reverse) def.ReverseDirection = true; if (so > 0) def.StartOffset = so; if (eo > 0) def.EndOffset = eo; if (trim.HasValue) def.TrimSideBends = trim.Value;
                if (def.GetPathType() < 0) throw new ToolFailure("SWEPT_FLANGE_FAILED", "SolidWorks did not recognise the path sketch '" + pathName + "'.");
                doc.ClearSelection2(true);
                var feat = doc.FeatureManager.CreateFeature(def) as IFeature; doc.ClearSelection2(true);
                if (feat == null)
                    throw new ToolFailure("SWEPT_FLANGE_FAILED", "SolidWorks built no swept flange. Check: the profile is an OPEN chain at the START of the path on a plane perpendicular to it, with no sharp doubling back; the path is one continuous chain; the radius and thickness fit the profile's segments.");
                doc.EditRebuild3(); int err = FeatureError(feat);
                if (err != 0) { DeleteFeatureQuietly(doc, feat.Name); throw new ToolFailure("SWEPT_FLANGE_FAILED", "The swept flange was built but reports rebuild error " + err + " and was taken out again (a radius or thickness that does not fit the profile is the usual cause)."); }

                var res = new JObject { ["feature"] = feat.Name, ["thickness"] = t, ["bend_radius"] = r, ["reverse"] = reverse };
                if (so > 0) res["start_offset"] = so; if (eo > 0) res["end_offset"] = eo;
                var problems = new JArray(); var skipped = new JArray();
                res["new_features"] = new JArray(TopFeatures(doc).Where(f => !names0.Contains(f.Name) && f.GetTypeName2() != "ProfileFeature").Select(f => (object)(f.Name + " [" + f.GetTypeName2() + "]")).ToArray());
                var after = BodyStats(doc); res["body_after"] = after;
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; int nBodies = bodies == null ? 0 : bodies.Length; res["bodies"] = nBodies; if (nBodies != bodies0 + 1) problems.Add(nBodies + " solid bodies now, " + (bodies0 + 1) + " expected");
                double th = SheetThickness(doc); res["sheet_thickness"] = th; if (Math.Abs(th - t) > 1e-7) problems.Add("the sheet thickness is " + (th * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm, " + (t * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm was asked");
                double vol1 = 0; try { vol1 = ExactVolume(doc); res["volume_added"] = vol1 - vol0; } catch (Exception) { problems.Add("the volume could not be measured"); }
                double k = 0.5; double[] blank = null; int bends = -1;
                try
                {
                    var fl = FlatStep("flat_pattern_control", new JObject(), "flat pattern"); var size = fl["flat"] == null || fl["flat"]["size"] == null ? null : fl["flat"]["size"].Select(x => x.Value<double>()).ToArray();
                    if (size == null || size.Max() <= 1e-6) problems.Add("the flat pattern could not be made or has no size"); else res["flat_size"] = new JArray(size.Select(v => (object)Math.Round(v, 9)).ToArray());
                    var sp = FlatStep("sheet_metal_params", new JObject { ["action"] = "get" }, "sheet-metal parameters"); var kt = sp.SelectToken("global.bend_allowance.k_factor"); if (kt != null) k = kt.Value<double>();
                    var cl = sp["cut_list"] as JArray; var props = cl != null && cl.Count > 0 ? cl[0]["properties"] as JObject : null;
                    if (props != null)
                    {
                        string unit = ((string)sp.SelectToken("units.length") ?? "mm").ToLowerInvariant(); double perUnit = unit == "in" ? 0.0254 : unit == "cm" ? 0.01 : unit == "m" ? 1.0 : unit == "ft" ? 0.3048 : 0.001; double v, w;
                        if (props["Bends"] != null) { int b; if (int.TryParse(props["Bends"].ToString().Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) { bends = b; res["bends"] = b; } }
                        if (props["Bounding Box Length"] != null && props["Bounding Box Width"] != null && double.TryParse(props["Bounding Box Length"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.TryParse(props["Bounding Box Width"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                        { blank = new[] { v * perUnit, w * perUnit }.OrderByDescending(x => x).ToArray(); res["blank"] = new JArray(blank.Select(x => (object)Math.Round(x, 9)).ToArray()); }
                    }
                }
                catch (ToolFailure ex) { problems.Add("the flat pattern / cut list could not be read: " + ex.Message); }

                res["k_factor"] = k;
                // the closed forms: a chain of straight segments along ONE straight path
                if (profChain == null) skipped.Add("the profile is not one open chain of straight lines");
                else if (pathLength <= 0) skipped.Add("the path is not one straight line");
                else
                {
                    double effective = pathLength - so - eo; if (effective <= 0) { problems.Add("the offsets leave no flange"); }
                    else
                    {
                        int n = profChain.Count; double sumL = 0; var turns = new List<double[]>();      // [angle, sign]
                        for (int i = 0; i + 1 < n; i++) sumL += Math.Sqrt(Math.Pow(profChain[i + 1][0] - profChain[i][0], 2) + Math.Pow(profChain[i + 1][1] - profChain[i][1], 2));
                        for (int i = 1; i + 1 < n; i++)
                        {
                            double d1x = profChain[i][0] - profChain[i - 1][0], d1y = profChain[i][1] - profChain[i - 1][1], d2x = profChain[i + 1][0] - profChain[i][0], d2y = profChain[i + 1][1] - profChain[i][1];
                            double l1 = Math.Sqrt(d1x * d1x + d1y * d1y), l2 = Math.Sqrt(d2x * d2x + d2y * d2y); d1x /= l1; d1y /= l1; d2x /= l2; d2y /= l2;
                            double cross = d1x * d2y - d1y * d2x, dot = d1x * d2x + d1y * d2y; turns.Add(new[] { Math.Atan2(Math.Abs(cross), dot), cross >= 0 ? 1.0 : -1.0 });
                        }
                        double bestErr = double.MaxValue, bestArea = 0, bestDev = 0; int bestSide = 0;
                        foreach (int side in new[] { 1, -1 })        // the material on the LEFT (+1) or the RIGHT (-1) of the profile as the chain runs from its first end
                        {
                            double cut = 0, arc = 0, arcK = 0;
                            foreach (var tn in turns) { double s = (tn[1] == side ? r + t : r) * Math.Tan(tn[0] / 2); cut += 2 * s; arc += tn[0] * (r + t / 2); arcK += tn[0] * (r + k * t); }
                            double area = t * (sumL - cut + arc), dev = sumL - cut + arcK, miss = Math.Abs(area * effective - (vol1 - vol0));
                            if (miss < bestErr) { bestErr = miss; bestSide = side; bestArea = area; bestDev = dev; }
                        }
                        double want = bestArea * effective; res["expected_volume"] = want; res["expected_developed_profile"] = bestDev; res["material_side"] = bestSide == 1 ? "left of the profile (first end of the chain = " + Pt2(profChain[0]) + ")" : "right of the profile (first end of the chain = " + Pt2(profChain[0]) + ")";
                        if (Math.Abs((vol1 - vol0) - want) > Math.Max(1e-13, want * 1e-4)) problems.Add("the volume is " + ((vol1 - vol0) * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3, the closed form of the profile gives " + (want * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3 on either side");
                        if (blank != null)
                        {
                            var wantBlank = new[] { bestDev, effective }.OrderByDescending(x => x).ToArray(); res["expected_blank"] = new JArray(wantBlank.Select(x => (object)Math.Round(x, 9)).ToArray());
                            if (Math.Abs(blank[0] - wantBlank[0]) > 3e-5 || Math.Abs(blank[1] - wantBlank[1]) > 3e-5) problems.Add("the flat blank is " + Mmf(blank[0]) + " x " + Mmf(blank[1]) + " mm, the developed profile x path is " + Mmf(wantBlank[0]) + " x " + Mmf(wantBlank[1]) + " mm");
                        }
                    }
                }
                if (profChain != null && pathLength > 0)        // a chain of n points has n - 2 corners = n - 2 bends along a straight path
                {
                    if (bends < 0) problems.Add("the bends could not be counted in the cut list");
                    else if (bends != profChain.Count - 2) problems.Add("the cut list counts " + bends + " bends, the profile has " + (profChain.Count - 2) + " corners");
                }
                if (skipped.Count > 0) res["checks_skipped"] = skipped;
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }
    }
}
