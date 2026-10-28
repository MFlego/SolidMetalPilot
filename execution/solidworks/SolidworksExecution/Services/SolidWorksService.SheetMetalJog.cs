// sheet_metal_jog: a JOG (an offset step in a sheet: two parallel bends of opposite direction) built from two SKETCHED BENDS through the typed API. SolidWorks' own Jog feature cannot be made from the API
// (IModelDoc2.InsertSheetMetalJog is void and builds nothing for any selection order, mark, bend or dimension position tried - probe 69) and its PropertyManager is a classic Win32 one whose OK button does not
// take a posted click. The geometry is the same two bends, and every number is closed form: for a jog of angle theta, plane-to-plane offset h (the distance between the top faces of the two flat parts), sheet
// thickness t, inside radius r and bend allowance BA = theta (r + K t) per bend, the flat distance between the two bend lines is
//     s = (h - (2 r + t) (1 - cos theta)) / sin theta + BA
// (the web between the bends is (h - (2r + t)(1 - cos theta)) / sin theta long on the straight; each bend consumes half of BA on each side). The far bend is folded first (both lines are still in the flat plane,
// as in sheet_metal_from_flat), the flat blank does not change. Lengths METERS, angles DEGREES at this boundary. The sketch plane is the Front Plane (the sheet's face lies in it).
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
        // the distinct z (rounded to a micron, meters) of the planar faces whose normal runs along z - the planes of the flat parts of the sheet
        private List<double> PlanarZs(IPartDoc part)
        {
            var zs = new SortedSet<double>();
            foreach (var f in FlattenFaces(part))
            {
                var s = f.GetSurface() as ISurface; if (s == null || !s.IsPlane()) continue; var n = f.Normal as double[]; if (n == null || Math.Abs(Math.Abs(n[2]) - 1) > 1e-6) continue;
                var bx = f.GetBox() as double[]; if (bx == null) continue; zs.Add(Math.Round(bx[2], 6));
            }
            return zs.ToList();
        }

        public ExecutionResponse SheetMetalJog(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                RequireSheetMetalPart(doc, "sheet_metal_jog"); var part = (IPartDoc)doc;
                var lineTok = AsArray(p["line"], "line"); if (lineTok == null || lineTok.Count != 2) throw new ToolFailure("MISSING_PARAMETER", "line = [[x,y,z],[x,y,z]] (meters): the FIRST bend line, a segment on the sheet's face (the Front Plane). The second bend line is drawn for you.");
                var a = ToPoint(lineTok[0], "line[0]"); var b = ToPoint(lineTok[1], "line[1]"); var fx = p["fixed_point"] == null ? null : ToPoint(AsArray(p["fixed_point"], "fixed_point"), "fixed_point");
                if (fx == null) throw new ToolFailure("MISSING_PARAMETER", "fixed_point = [x,y,z] (meters): a point ON the sheet's face on the side that stays put; the other side steps.");
                double h = p.Value<double?>("offset") ?? 0; if (h <= 0) throw new ToolFailure("MISSING_PARAMETER", "offset (meters, > 0): the plane-to-plane step between the top faces of the two flat parts.");
                double theta = (p.Value<double?>("angle") ?? 90.0); if (theta <= 0 || theta > 90) throw new ToolFailure("INVALID_PARAMETER", "angle must lie between 0 and 90 degrees (the angle of each bend of the jog).");
                string dir = (p.Value<string>("direction") ?? "up").ToLowerInvariant(); if (dir != "up" && dir != "down") throw new ToolFailure("INVALID_PARAMETER", "direction must be 'up' (the far part steps toward +Z) or 'down' (toward -Z).");
                double t = SheetThickness(doc); double r = p.Value<double?>("bend_radius") ?? SheetRadius(doc);
                double k; { var pr = FlatStep("sheet_metal_params", new JObject { ["action"] = "get" }, "sheet-metal parameters"); var kt = pr.SelectToken("global.bend_allowance.k_factor"); k = p.Value<double?>("k_factor") ?? (kt == null ? 0.5 : kt.Value<double>()); }
                double th = theta * Math.PI / 180, ba = th * (r + k * t), web = (h - (2 * r + t) * (1 - Math.Cos(th))) / Math.Sin(th);
                if (web < 0) throw new ToolFailure("JOG_TOO_SMALL", "An offset of " + (h * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm is smaller than the two bends of radius " + (r * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm on a " + (t * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm sheet need at " + theta.ToString("0.#", CultureInfo.InvariantCulture) + " degrees: at least " + ((2 * r + t) * (1 - Math.Cos(th)) * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm.");
                double s = web + ba;                      // flat distance between the two bend lines
                // the second line: the first one moved by s, in the plane, away from the fixed point
                double dx = b[0] - a[0], dy = b[1] - a[1], len = Math.Sqrt(dx * dx + dy * dy); if (len < 1e-6) throw new ToolFailure("INVALID_PARAMETER", "The bend line has no length.");
                double nx = -dy / len, ny = dx / len; if (nx * ((a[0] + b[0]) / 2 - fx[0]) + ny * ((a[1] + b[1]) / 2 - fx[1]) < 0) { nx = -nx; ny = -ny; }
                var a2 = new[] { a[0] + nx * s, a[1] + ny * s }; var b2 = new[] { b[0] + nx * s, b[1] + ny * s };
                // the fixed face is the sheet's face at the plane: its z is the plane's (0); the point is projected onto it
                double fz = 0; var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; var bbox = bodies == null || bodies.Length == 0 ? null : BoxOf((IBody2)bodies[0]);
                if (bbox == null) throw new ToolFailure("NOT_SHEET_METAL", "The part has no solid body.");
                bool nearZero = Math.Abs(bbox[5]) < 1e-5 || Math.Abs(bbox[2]) < 1e-5; if (!nearZero) throw new ToolFailure("INVALID_PARAMETER", "sheet_metal_jog draws its bend lines on the Front Plane: the sheet's face must lie in z = 0 (the sheet spans z " + (bbox[2] * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " .. " + (bbox[5] * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm).");
                var zs0 = PlanarZs(part); double blank0 = (bbox[3] - bbox[0]) * (bbox[4] - bbox[1]);

                var names0 = TopNames(doc); var steps = new JArray(); var problems = new JArray();
                try
                {
                    // far bend first (all lines still in the flat plane): the part beyond it turns back, the opposite way of the near bend
                    bool nearFlip = dir == "down";
                    foreach (var bend in new[] { new { name = "far", p1 = a2, p2 = b2, flip = !nearFlip }, new { name = "near", p1 = new[] { a[0], a[1] }, p2 = new[] { b[0], b[1] }, flip = nearFlip } })
                    {
                        FlatStep("create_sketch", new JObject { ["plane"] = "Front Plane" }, "jog sketch (" + bend.name + " bend)");
                        FlatStep("add_sketch_entity", new JObject { ["entity_type"] = "line", ["x1"] = bend.p1[0], ["y1"] = bend.p1[1], ["x2"] = bend.p2[0], ["y2"] = bend.p2[1] }, "jog line (" + bend.name + " bend)");
                        FlatStep("sheet_metal_feature", new JObject { ["feature_type"] = "sketched_bend", ["angle"] = theta, ["bend_radius"] = r, ["bend_position"] = "centerline", ["fixed_x"] = fx[0], ["fixed_y"] = fx[1], ["fixed_z"] = fz, ["flip"] = bend.flip }, "sketched bend (" + bend.name + " bend of the jog)");
                        FlatCheckNew(doc, names0, "sketched bend (" + bend.name + " bend of the jog)"); steps.Add(bend.name + " bend, " + (bend.flip ? "down" : "up"));
                    }
                    doc.EditRebuild3();
                }
                catch (Exception ex)
                {
                    int removed = FlatRollback(doc, names0); bool ours = ex is ToolFailure;
                    throw new ToolFailure(ours ? ((ToolFailure)ex).Code : "UNEXPECTED_ERROR", (ours ? ex.Message : ex.GetType().Name + ": " + ex.Message) + " [rolled back: " + removed + " features removed]");
                }

                // proof: the far flat part lies h above (or below) the near one, and the blank did not change
                var zs1 = PlanarZs(part); var res = new JObject { ["steps"] = steps, ["features"] = new JArray(TopFeatures(doc).Where(f => !names0.Contains(f.Name) && f.GetTypeName2() != "ProfileFeature").Select(f => f.Name).ToArray()), ["bend_allowance"] = ba, ["web"] = web, ["line_distance"] = s, ["offset"] = h, ["direction"] = dir, ["angle"] = theta, ["bend_radius"] = r, ["k_factor"] = k };
                res["second_line"] = new JArray(new JArray(a2[0], a2[1]), new JArray(b2[0], b2[1]));
                double want = dir == "up" ? h : -h; double near = zs0.Count > 0 ? zs0.Where(z => z > -1e-5).DefaultIfEmpty(0).Max() : 0;      // the near flat's top face is the sketch plane (z = 0)
                bool stepFound = zs1.Any(z => Math.Abs(z - (near + want)) < 2e-5); res["planes_before"] = new JArray(zs0.Select(z => (object)z).ToArray()); res["planes_after"] = new JArray(zs1.Select(z => (object)z).ToArray());
                if (!stepFound) problems.Add("no flat face lies " + (want * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm from the near flat's face: the planes are " + string.Join(", ", zs1.Select(z => (z * 1000).ToString("0.###", CultureInfo.InvariantCulture)).ToArray()) + " mm");
                var fl = FlatStep("flat_pattern_control", new JObject(), "flat pattern"); var size = fl["flat"] == null || fl["flat"]["size"] == null ? null : fl["flat"]["size"].Select(x => x.Value<double>()).ToArray();
                if (size != null)
                {
                    res["flat_size"] = new JArray(size.Select(x => (object)x).ToArray());
                    if (Math.Abs(bbox[5] - bbox[2] - t) < 2e-5)        // a flat sheet before: its blank must be the blank after (the jog keeps the flat length)
                    {
                        var want2 = new[] { bbox[3] - bbox[0], bbox[4] - bbox[1] }.OrderByDescending(x => x).ToArray(); var got = size.Take(2).OrderByDescending(x => x).ToArray();
                        double dev = Math.Max(Math.Abs(got[0] - want2[0]), Math.Abs(got[1] - want2[1])); res["flat_deviation"] = Math.Round(dev, 9);
                        if (dev > 2e-4) problems.Add("the flat blank changed: " + (got[0] * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " x " + (got[1] * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm, it was " + (want2[0] * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " x " + (want2[1] * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm");
                    }
                }
                if (problems.Count > 0) res["problems"] = problems; res["verified"] = problems.Count == 0; return res;
            });
        }
    }
}
