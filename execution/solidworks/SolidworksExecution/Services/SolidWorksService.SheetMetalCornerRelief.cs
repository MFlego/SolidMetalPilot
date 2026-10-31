// sheet_metal_corner_relief: the CORNER RELIEF feature (swFmCornerRelief = 120) - the relief SolidWorks cuts where the bends of two walls meet at a corner - through CreateDefinition -> ICornerReliefFeatureData ->
// SetBodyScope(the body) -> Initialize(0 = two-bend corners) -> CollectAllCorners(relief type) -> the ISMCornerReliefData of every corner -> CreateFeature (probe 88). What decided, measured on a four-wall tray (1.5 mm sheet):
//   - SetBodyScope is REQUIRED: without it every CollectAllCorners answers error 2 (InvalidBody); with it the tray gives 4 two-bend corners (corner indices are 1-BASED) and no three-bend ones (error 6);
//   - CreateFeature returns null for dimensions that do not clear the bend: a circular relief needs a width (its diameter) of at least 3 mm here (2.9 builds nothing, 3 does: 2 x thickness), an obround one a slot about twice
//     as long as it is wide (6 x 3 and 8 x 4 build, 3 x 4 and 5 x 5 do not); the dimensions as SolidWorks collects them (length 0, width = the sheet) build nothing;
//   - what each type reads: circular = SlotWidth (diameter 3 / 4 / 6 mm remove 128.87 / 226.68 / 510.01 mm3 from the four corners); square and rectangular = SlotLength (3 / 4 / 5 / 6 mm: 60.41 / 75.23 / 113.69 / 162.64 mm3,
//     the width makes no difference); tear = fixed by the geometry (33.0 mm3 whatever the dimensions); obround = both. SQUARE AND RECTANGULAR ARE ONE CUT: the same length gives the same volume and faces, and
//     a square relief reads back from the feature as relief type 9 (rectangular) - that is SolidWorks storing it, not a relief that was lost;
//   - the options: center_on_bend_lines changes the cut, tangent_to_bend is kept without a visible effect, add_filleted_corners (+ fillet_diameter) is taken by square / rectangular reliefs and ignored by circular ones (reported as a problem),
//     ratio_to_thickness built nothing for every dimension tried (not offered);
//   - SOLIDWORKS DIED TWICE while this was found (scratch trays): (1) objects of corners that had been REMOVED (RemoveCorner) are dangling - touching them kills it; (2) CollectAllCorners answered error 4
//     (InvalidReliefType) for relief 2 (bend waist, a THREE-bend relief) yet returned an array, and setting ReliefType = 2 on those two-bend corners killed it. So this tool touches the corners only when
//     CollectAllCorners answered 0, offers only the types that were built and measured, and never removes a corner;
//   - THREE-BEND corners (swCornerReliefBendType_ThreeBend = 1, Initialize(1)): the corner where a flange runs round a bend and meets the neighbouring flange (the mitred front-bottom corner of a tray with end flanges and a lip). A part with such
//     corners was measured: CollectAllCorners(9) on the right body answers 0 with the corners, on a body without them it answers error 6; the rectangular relief (9) builds, reads back as relief 9 with the length given, and the feature reads back
//     CornerType = 1. Only the rectangular relief is offered there; the bend waist / constant width / mixed / full round / suit case reliefs are still untested;
//   - a part with SEVERAL solid bodies: SetBodyScope takes the body (`body` = its name); the feature then holds only that body's corners.
// All corners of the part get the relief (a subset needs RemoveCorner; not offered). Lengths METERS.
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
        public ExecutionResponse SheetMetalCornerRelief(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                RequireSheetMetalPart(doc, "sheet_metal_corner_relief");
                var part = (IPartDoc)doc;
                string rel = (p.Value<string>("relief") ?? "circular").Trim().ToLowerInvariant(); int rt;
                switch (rel)
                {
                    case "circular": rt = 0; break;
                    case "square": rt = 1; break;
                    case "tear": rt = 3; break;
                    case "obround": rt = 5; break;
                    case "rectangular": rt = 9; break;
                    default: throw new ToolFailure("INVALID_PARAMETER", "relief must be 'circular' (default), 'square', 'tear', 'obround' or 'rectangular' - the five types built and measured on two-bend corners (bend waist, constant width, mixed, full round and suit case are three-bend reliefs and killed SolidWorks on two-bend corners).");
                }
                string ctype = (p.Value<string>("corner_type") ?? "two_bend").Trim().ToLowerInvariant();
                if (ctype != "two_bend" && ctype != "three_bend") throw new ToolFailure("INVALID_PARAMETER", "corner_type must be 'two_bend' (the corner where the bends of two walls meet) or 'three_bend' (three bends meet: a flange running round a bend, the mitred front corner of a tray).");
                bool three = ctype == "three_bend";
                if (three && rt != 9) throw new ToolFailure("INVALID_PARAMETER", "A three_bend corner takes the 'rectangular' relief only - the one measured on three-bend corners (it reads back as type 9). Bend waist, constant width, mixed, full round and suit case are not offered: a relief type the corners did not accept killed SolidWorks twice.");
                double width = p.Value<double?>("width") ?? 0.0, length = p.Value<double?>("length") ?? 0.0, fillet = p.Value<double?>("fillet_diameter") ?? 0.0; double thickness = SheetThickness(doc);
                if (width < 0 || length < 0 || fillet < 0) throw new ToolFailure("INVALID_PARAMETER", "width, length and fillet_diameter are lengths in METERS and cannot be negative.");
                if ((rt == 1 || rt == 9) && length <= 0) throw new ToolFailure("MISSING_PARAMETER", "length (meters, > 0): the side of a " + rel + " relief (the width makes no difference).");
                if (rt == 5 && (length <= 0 || width <= 0)) throw new ToolFailure("MISSING_PARAMETER", "length and width (meters, > 0): an obround relief is a slot about twice as long as it is wide (6 x 3 mm and 8 x 4 mm build on a 1.5 mm sheet).");
                // SolidWorks wants BOTH SlotLength and SlotWidth above zero (width alone, or the proposed 0 x sheet, builds nothing) even where one of them makes no difference (circular reads the width, tear neither):
                // the one that is not given is 2 x the thickness - the smallest that was measured to build
                double useLength = length > 0 ? length : 2 * thickness, useWidth = width > 0 ? width : 2 * thickness;
                if (p["ratio_to_thickness"] != null) throw new ToolFailure("INVALID_PARAMETER", "ratio_to_thickness is not offered: with it on, CreateFeature built nothing for every dimension tried (as meters or as multiples of the thickness).");
                bool? tangent = p.Value<bool?>("tangent_to_bend"), centre = p.Value<bool?>("center_on_bend_lines"), filleted = p.Value<bool?>("add_filleted_corners");
                if (filleted == true && fillet <= 0) throw new ToolFailure("MISSING_PARAMETER", "fillet_diameter (meters, > 0) is required with add_filleted_corners.");

                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bodies == null || bodies.Length == 0) throw new ToolFailure("INVALID_PARAMETER", "The part has no solid body.");
                string bodyName = p.Value<string>("body"); object scopeBody = null; string names = string.Join(", ", bodies.Select(b => "'" + ((Body2)b).Name + "'").ToArray());
                if (!string.IsNullOrEmpty(bodyName))
                {
                    foreach (var b in bodies) if (string.Equals(((Body2)b).Name, bodyName, StringComparison.OrdinalIgnoreCase)) scopeBody = b;
                    if (scopeBody == null) throw new ToolFailure("INVALID_PARAMETER", "No solid body named '" + bodyName + "' (the part has " + names + ").");
                }
                else
                {
                    if (bodies.Length != 1) throw new ToolFailure("INVALID_PARAMETER", "The part has " + bodies.Length + " solid bodies (" + names + "): name the one to relieve with 'body'.");
                    scopeBody = bodies[0];
                }
                string scopeName = ((Body2)scopeBody).Name;          // read now: the body object is stale after the rebuild
                var before = BodyStats(doc); double vol0 = ExactVolume(doc); var names0 = TopNames(doc);
                if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);

                var def = doc.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmCornerRelief) as ICornerReliefFeatureData;
                if (def == null) throw new ToolFailure("CORNER_RELIEF_FAILED", "CreateDefinition(swFmCornerRelief) returned nothing.");
                int scope = def.SetBodyScope(scopeBody); if (scope != 0) throw new ToolFailure("CORNER_RELIEF_FAILED", "SetBodyScope answered error " + scope + " (swCornerReliefError_e).");
                if (!def.Initialize(three ? 1 : 0)) throw new ToolFailure("CORNER_RELIEF_FAILED", "Initialize(" + (three ? "three" : "two") + "-bend corners) failed.");
                object collected = null; int err = def.CollectAllCorners(rt, out collected); var corners = collected as object[];
                if (err == 6 || ((corners == null || corners.Length == 0) && err == 0)) throw new ToolFailure("NO_CORNERS", three ? "The body has no three-bend corners (three bends meeting: a flange that runs round a bend and meets the neighbouring flange)." : "The body has no two-bend corners: the walls must stand on the sheet and meet at a corner (edge flanges on adjacent edges).");
                if (err != 0 || corners == null) throw new ToolFailure("CORNER_RELIEF_FAILED", "CollectAllCorners answered error " + err + " (swCornerReliefError_e: 1 faces, 2 body, 3 corner type, 4 relief type, 5 index, 6 generic) - the corners were not touched.");

                foreach (var o in corners)
                {
                    var sd = o as ISMCornerReliefData; if (sd == null) throw new ToolFailure("CORNER_RELIEF_FAILED", "A collected corner is not an ISMCornerReliefData.");
                    sd.ReliefType = rt;
                    sd.SlotLength = useLength; sd.SlotWidth = useWidth; if (fillet > 0) sd.CornerFilletDiameter = fillet;
                    if (tangent.HasValue) sd.TangentToBend = tangent.Value; if (centre.HasValue) sd.CenterOnBendLines = centre.Value; if (filleted.HasValue) sd.AddFilletedCorners = filleted.Value;
                }
                doc.ClearSelection2(true);
                var feat = doc.FeatureManager.CreateFeature(def) as IFeature;
                if (feat == null)
                    throw new ToolFailure("CORNER_RELIEF_FAILED", "SolidWorks built no corner relief (width " + Mmf(useWidth) + " mm, length " + Mmf(useLength) + " mm). Measured on a 1.5 mm sheet: a circular relief needs a width (diameter) of at least 2 x the thickness (here " + Mmf(2 * thickness) + " mm), an obround one a slot about twice as long as it is wide, a square / rectangular one a length; the dimensions SolidWorks proposes (length 0, width = the sheet) build nothing.");
                doc.EditRebuild3(); int ferr = FeatureError(feat);
                if (ferr != 0) { DeleteFeatureQuietly(doc, feat.Name); doc.EditRebuild3(); throw new ToolFailure("CORNER_RELIEF_FAILED", "The corner relief was built but reports rebuild error " + ferr + " and was taken out again."); }

                var problems = new JArray(); var res = new JObject { ["feature"] = feat.Name, ["feature_type"] = feat.GetTypeName2(), ["relief"] = rel, ["corner_type"] = ctype, ["body"] = scopeName, ["corners_collected"] = corners.Length };
                var back = feat.GetDefinition() as ICornerReliefFeatureData; bool acc = false;
                try
                {
                    if (back != null)
                    {
                        acc = back.AccessSelections((ModelDoc2)doc, null); var def2 = new JObject { ["corner_count"] = back.CornerCount, ["corner_type"] = back.CornerType }; var list = new JArray();
                        var gc = back.GetCorners(rt) as object[];
                        if (gc != null)
                            foreach (var g in gc)
                            {
                                var sd = g as ISMCornerReliefData; if (sd == null) continue;
                                list.Add(new JObject { ["index"] = sd.CornerIndex, ["relief_type"] = sd.ReliefType, ["slot_length"] = sd.SlotLength, ["slot_width"] = sd.SlotWidth, ["center_on_bend_lines"] = sd.CenterOnBendLines, ["tangent_to_bend"] = sd.TangentToBend, ["add_filleted_corners"] = sd.AddFilletedCorners });
                                if (sd.ReliefType != rt && !(rt == 1 && sd.ReliefType == 9)) problems.Add("corner " + sd.CornerIndex + " kept relief type " + sd.ReliefType + ", " + rt + " was asked");
                                if (tangent.HasValue && sd.TangentToBend != tangent.Value) problems.Add("corner " + sd.CornerIndex + " kept tangent_to_bend = " + sd.TangentToBend);
                                if (centre.HasValue && sd.CenterOnBendLines != centre.Value) problems.Add("corner " + sd.CornerIndex + " kept center_on_bend_lines = " + sd.CenterOnBendLines);
                                if (filleted.HasValue && sd.AddFilletedCorners != filleted.Value) problems.Add("corner " + sd.CornerIndex + " kept add_filleted_corners = " + sd.AddFilletedCorners + " (a circular relief ignores it; square and rectangular reliefs take it)");
                            }
                        def2["corners"] = list; res["definition"] = def2;
                        if (back.CornerCount != corners.Length) problems.Add("the feature holds " + back.CornerCount + " corners, " + corners.Length + " were collected");
                    }
                }
                catch (COMException ex) { problems.Add("the definition could not be read back: " + ex.Message); }
                finally { if (acc) { try { back.ReleaseSelectionAccess(); } catch (COMException) { } } }
                var after = BodyStats(doc); double vol1 = ExactVolume(doc);
                res["body_before"] = before; res["body_after"] = after; res["volume_before"] = vol0; res["volume_after"] = vol1; res["volume_change"] = vol1 - vol0; res["feature_error"] = ferr;
                res["width"] = useWidth; res["length"] = useLength;
                if (rt == 1) res["note"] = "SolidWorks stores a square relief as the rectangular type (9): the cut is the same as relief 'rectangular' with this length, and the corners read back as relief_type 9.";
                if (!(vol1 < vol0 - 1e-13)) problems.Add("a relief removes material, but the volume changed by " + ((vol1 - vol0) * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3");
                if (after.Value<int?>("faces") == before.Value<int?>("faces")) problems.Add("the relief changed no face");
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }
    }
}
