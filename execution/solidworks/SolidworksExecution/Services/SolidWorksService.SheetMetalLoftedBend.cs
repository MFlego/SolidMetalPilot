// sheet_metal_lofted_bend: the LOFTED BEND (a sheet-metal transition between two OPEN profile sketches on parallel planes - the round-to-square duct, the funnel) through the typed API.
// IFeatureManager.InsertSheetMetalLoftedBend2 works, but only for profiles it accepts (probes 75 and 78): the first attempts used a rectangle left open by a 0.5 mm closing segment and got null every time; an
// ARC in each sketch, a line, an L, a square U, a U of two lines and an arc (the arc running THROUGH the bottom, not over the lines) all build, a spline does not. Measured: ThickDirType 0 puts the material
// outside the first profile's curve and 1 inside (2 is refused); BFormed = true makes the smooth (formed) surface (6 faces for two arcs, 1 bend) instead of the faceted one; EFacetOption selects which of the four
// facet arguments is used: 0 chord tolerance, 1 number of bends, 2 segment length, 3 segment angle. The sheet-metal feature (thickness, bend radius) is created by the lofted bend itself. Lengths METERS, angles DEGREES.
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
        public ExecutionResponse SheetMetalLoftedBend(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_lofted_bend needs an active part.");
                var arr = AsArray(p["sketches"], "sketches");
                if (arr == null || arr.Count != 2) throw new ToolFailure("MISSING_PARAMETER", "sketches = [\"SketchA\", \"SketchB\"]: the names of the two OPEN profile sketches (on parallel planes, the same kind of chain of lines / arcs in each).");
                string n1 = (string)arr[0], n2 = (string)arr[1];
                if (string.IsNullOrWhiteSpace(n1) || string.IsNullOrWhiteSpace(n2) || string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase)) throw new ToolFailure("INVALID_PARAMETER", "sketches must name two different sketches.");
                double t = p.Value<double?>("thickness") ?? 0.0; if (t <= 0 || t > 0.05) throw new ToolFailure("MISSING_PARAMETER", "thickness (meters, > 0 and at most 50 mm) is required: the sheet thickness of the lofted bend.");
                string side = (p.Value<string>("thickness_side") ?? "side1").Trim().ToLowerInvariant(); int dir;
                if (side == "side1") dir = 0; else if (side == "side2") dir = 1; else throw new ToolFailure("INVALID_PARAMETER", "thickness_side must be 'side1' (default: the material outside the first profile's curve) or 'side2' (inside).");
                bool formed = p.Value<bool?>("formed") ?? false, referEnd = p.Value<bool?>("refer_to_end_points") ?? false; double radius = p.Value<double?>("bend_radius") ?? t;
                if (radius <= 0 || radius > 0.1) throw new ToolFailure("INVALID_PARAMETER", "bend_radius is in METERS, > 0 and below 100 mm (default: the thickness).");
                string facets = (p.Value<string>("facets") ?? "chord_tolerance").Trim().ToLowerInvariant(); int facetOpt;
                switch (facets)
                {
                    case "chord_tolerance": facetOpt = 0; break;
                    case "number_of_bends": facetOpt = 1; break;
                    case "segment_length": facetOpt = 2; break;
                    case "segment_angle": facetOpt = 3; break;
                    default: throw new ToolFailure("INVALID_PARAMETER", "facets must be 'chord_tolerance' (default), 'number_of_bends', 'segment_length' or 'segment_angle': the one facet argument that SolidWorks uses.");
                }
                double chord = p.Value<double?>("chord_tolerance") ?? 0.0001, segLen = p.Value<double?>("segment_length") ?? 0.01, segAngle = (p.Value<double?>("segment_angle") ?? 5.7296) * Math.PI / 180; int nb = p.Value<int?>("number_of_bends") ?? 4;
                if (chord <= 0 || segLen <= 0 || segAngle <= 0 || nb < 2 || nb > 200) throw new ToolFailure("INVALID_PARAMETER", "chord_tolerance and segment_length (meters) and segment_angle (degrees) must be > 0; number_of_bends between 2 and 200.");

                foreach (var nm in new[] { n1, n2 })
                {
                    var f = FindFeatureByName(doc, nm, null); if (f == null || f.GetTypeName2() != "ProfileFeature") throw new ToolFailure("SKETCH_NOT_FOUND", "No sketch named '" + nm + "'.");
                }
                try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
                var names0 = TopNames(doc); var before = BodyStats(doc); double vol0 = 0; try { vol0 = ExactVolume(doc); } catch (Exception) { }
                doc.ClearSelection2(true);
                if (!doc.Extension.SelectByID2(n1, "SKETCH", 0, 0, 0, false, 0, null, 0) || !doc.Extension.SelectByID2(n2, "SKETCH", 0, 0, 0, true, 0, null, 0)) { doc.ClearSelection2(true); throw new ToolFailure("SELECTION_FAILED", "Could not select the two sketches."); }
                var feat = doc.FeatureManager.InsertSheetMetalLoftedBend2(dir, t, formed, radius, referEnd, facetOpt, chord, nb, segLen, segAngle) as IFeature;
                doc.ClearSelection2(true);
                if (feat == null)
                    throw new ToolFailure("LOFTED_BEND_FAILED", "SolidWorks built no lofted bend. What it needs (measured): two OPEN profile sketches on parallel planes - a closed profile, a rectangle left open by a tiny segment, a spline, or a chain that doubles back over itself is refused - each a continuous chain of lines and / or arcs, the two of the same kind.");
                doc.EditRebuild3(); int err = FeatureError(feat);
                if (err != 0) { DeleteFeatureQuietly(doc, feat.Name); throw new ToolFailure("LOFTED_BEND_FAILED", "The lofted bend was built but reports rebuild error " + err + " and was taken out again."); }

                var res = new JObject { ["feature"] = feat.Name, ["thickness"] = t, ["thickness_side"] = side, ["formed"] = formed, ["bend_radius"] = radius, ["facets"] = facets };
                var after = BodyStats(doc); res["body_before"] = before; res["body_after"] = after;
                var problems = new JArray();
                try { double vol1 = ExactVolume(doc); res["volume"] = vol1; res["volume_added"] = vol1 - vol0; } catch (Exception) { problems.Add("the volume could not be measured"); }
                double th = SheetThickness(doc); res["sheet_thickness"] = th; if (Math.Abs(th - t) > 1e-7) problems.Add("the sheet thickness is " + (th * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm, " + (t * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm was asked");
                res["new_features"] = new JArray(TopFeatures(doc).Where(f => !names0.Contains(f.Name) && f.GetTypeName2() != "ProfileFeature").Select(f => (object)(f.Name + " [" + f.GetTypeName2() + "]")).ToArray());
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; int nBodies = bodies == null ? 0 : bodies.Length; res["bodies"] = nBodies; if (nBodies != 1) problems.Add(nBodies + " solid bodies, 1 expected");
                try
                {
                    var fl = FlatStep("flat_pattern_control", new JObject(), "flat pattern"); var size = fl["flat"] == null || fl["flat"]["size"] == null ? null : fl["flat"]["size"].Select(x => x.Value<double>()).ToArray();
                    if (size == null || size.Max() <= 1e-6) problems.Add("the flat pattern could not be made or has no size"); else res["flat_size"] = new JArray(size.Select(v => (object)Math.Round(v, 9)).ToArray());
                    var sp = FlatStep("sheet_metal_params", new JObject { ["action"] = "get" }, "sheet-metal parameters"); var cl = sp["cut_list"] as JArray; var props = cl != null && cl.Count > 0 ? cl[0]["properties"] as JObject : null;
                    if (props != null)
                    {
                        string unit = ((string)sp.SelectToken("units.length") ?? "mm").ToLowerInvariant(); double perUnit = unit == "in" ? 0.0254 : unit == "cm" ? 0.01 : unit == "m" ? 1.0 : unit == "ft" ? 0.3048 : 0.001; double v;
                        int bends; if (props["Bends"] != null && int.TryParse(props["Bends"].ToString().Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out bends)) { res["bends"] = bends; if (bends < 1) problems.Add("the cut list counts no bends"); }
                        if (props["Bounding Box Length"] != null && double.TryParse(props["Bounding Box Length"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) res["blank_length"] = Math.Round(v * perUnit, 9);
                        if (props["Bounding Box Width"] != null && double.TryParse(props["Bounding Box Width"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) res["blank_width"] = Math.Round(v * perUnit, 9);
                        if (props["Cutting Length-Outer"] != null && double.TryParse(props["Cutting Length-Outer"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) res["cutting_length"] = Math.Round(v * perUnit, 9);
                    }
                }
                catch (ToolFailure ex) { problems.Add("the flat pattern / cut list could not be read: " + ex.Message); }
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }
    }
}
