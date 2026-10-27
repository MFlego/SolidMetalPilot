// SURFACE EXTRUDE of a sketch and SURFACE CUT of the solid with that surface: the user's way of trimming the corners of an unfolded sheet-metal blank (v4: a three-point spline on a plane one thickness above
// the plate, extruded 5 mm through the sheet as a surface ribbon, then a Surface Cut with it; a surface cut - unlike a solid cut - can be mirrored). Plain typed API (probe 57, closed-form volumes):
//   IFeatureManager.FeatureExtruRefSurface3 (single-ended, blind, no draft, no cap, no knit)   - the sketch is preselected
//   IFeatureManager.InsertCutSurface(Flip, KeepPieceIndex, UseFeatScope = false, UseAutoSelect = true, null, out error) - the surface FEATURE (or its sheet body) is preselected; selecting both fails
// Flip = false removes the side the surface normal points to, Flip = true the other one: which side that is depends on the sketch, so the tool measures the volume it removed and can refuse (and take its own
// feature out again) when that exceeds `max_removed_volume` - a wrong flip removes almost the whole part. Lengths METERS, volumes m3.
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
        private static double ExactVolume(IModelDoc2 doc)
        {
            var mp = doc.Extension.CreateMassProperty() as IMassProperty; if (mp == null) return double.NaN;
            mp.UseSystemUnits = true; return mp.Volume;
        }

        private static List<IBody2> SheetBodies(IPartDoc part) { return AllBodies(part, false).Where(b => b.GetType() == (int)swBodyType_e.swSheetBody).ToList(); }

        // ---- surface_extrude ----------------------------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse SurfaceExtrude(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "surface_extrude needs an active part.");
                double depth = p.Value<double?>("depth") ?? 0; if (depth <= 0 || depth > 1) throw new ToolFailure("INVALID_PARAMETER", "depth is in METERS (0 < d <= 1000 mm).");
                bool reverse = p.Value<bool?>("reverse") ?? false, mid = p.Value<bool?>("mid_plane") ?? false;      // mid_plane: symmetric about the sketch plane, `depth` = the TOTAL width (v4's cutting ribbon is z -0.9 .. 4.1 on a plane at z = 1.6)
                string sketch = p.Value<string>("sketch");
                if (string.IsNullOrEmpty(sketch)) { var active = doc.SketchManager.ActiveSketch as IFeature; sketch = active == null ? null : active.Name; }
                if (string.IsNullOrEmpty(sketch)) throw new ToolFailure("MISSING_PARAMETER", "sketch (the name of the profile sketch) is required when no sketch is active.");
                var sf = FindFeatureByName(doc, sketch, null); if (sf == null || sf.GetTypeName2() != "ProfileFeature") throw new ToolFailure("SKETCH_NOT_FOUND", "No sketch named '" + sketch + "'.");
                if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);

                var names0 = TopNames(doc); var sheetsBefore = SheetBodies(part).Select(BoxOf).ToList();
                BeforeFlatPattern<int>(doc, () =>
                {
                    doc.ClearSelection2(true);
                    if (!doc.Extension.SelectByID2(sketch, "SKETCH", 0, 0, 0, false, 0, null, 0)) throw new ToolFailure("SELECTION_FAILED", "Could not select the sketch '" + sketch + "'.");
                    // Sd single-ended, Dir = reverse, StartCond 0 = sketch plane, offset 0, T1 blind, depth D1, no second direction, no draft, no offset reverse, no translate, no caps, keep the original face, no knit
                    doc.FeatureManager.FeatureExtruRefSurface3(true, reverse, 0, 0.0, mid ? (int)swEndConditions_e.swEndCondMidPlane : (int)swEndConditions_e.swEndCondBlind, 0, depth, 0.0, false, false, false, false, 0.0, 0.0, false, false, false, false, false, false, false, false);
                    return 0;
                });
                doc.ClearSelection2(true); doc.EditRebuild3();
                var created = TopFeatures(doc).FirstOrDefault(f => !names0.Contains(f.Name) && f.GetTypeName2() == "ExtruRefSurface");
                if (created == null) throw new ToolFailure("SURFACE_EXTRUDE_FAILED", "SolidWorks built no Surface-Extrude feature (is '" + sketch + "' an exited sketch with a profile?).");
                var problems = new JArray(); int err = FeatureError(created);
                if (err != 0) problems.Add("the feature reports rebuild error " + err);
                var sheetsAfter = SheetBodies(part); IBody2 newBody = null;
                foreach (var b in sheetsAfter) { var bx = BoxOf(b); if (bx != null && !sheetsBefore.Any(o => o != null && BoxesEqual(o, bx))) newBody = b; }
                var res = new JObject { ["feature"] = created.Name, ["sketch"] = sketch, ["depth"] = depth, ["reverse"] = reverse, ["mid_plane"] = mid, ["surface_bodies"] = sheetsAfter.Count };
                if (newBody != null) { var bx = BoxOf(newBody); res["surface_box"] = new JArray(bx.Select(v => (object)Math.Round(v, 9)).ToArray()); res["surface_size"] = SizeOf(bx); }
                else problems.Add("no new surface body appeared (the sketch profile may be empty)");
                if (problems.Count > 0) res["problems"] = problems;
                res["feature_error"] = err; res["verified"] = err == 0 && problems.Count == 0;
                return res;
            });
        }

        // ---- surface_cut ---------------------------------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse SurfaceCut(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "surface_cut needs an active part.");
                string name = p.Value<string>("surface"); if (string.IsNullOrEmpty(name)) throw new ToolFailure("MISSING_PARAMETER", "surface (the feature that made the cutting surface, e.g. 'Surface-Extrude1') is required.");
                var surf = FindFeatureByName(doc, name, null); if (surf == null) throw new ToolFailure("FEATURE_NOT_FOUND", "No feature named '" + name + "'.");
                bool flip = p.Value<bool?>("flip") ?? false; int keep = p.Value<int?>("keep_piece") ?? 0; double? maxRemoved = p.Value<double?>("max_removed_volume");
                if (maxRemoved.HasValue && maxRemoved.Value <= 0) throw new ToolFailure("INVALID_PARAMETER", "max_removed_volume is in m3 (> 0).");
                if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);

                double v0 = ExactVolume(doc); var names0 = TopNames(doc); int apiError = 0;
                IFeature cut = BeforeFlatPattern<IFeature>(doc, () =>
                {
                    doc.ClearSelection2(true);
                    if (!surf.Select2(false, 0)) throw new ToolFailure("SELECTION_FAILED", "Could not select '" + name + "'.");
                    return doc.FeatureManager.InsertCutSurface(flip, keep, false, true, null, out apiError) as IFeature;
                });
                doc.ClearSelection2(true);
                if (cut == null) throw new ToolFailure("SURFACE_CUT_FAILED", "InsertCutSurface returned null (error " + apiError + "): the surface must cross the solid and be selected alone.");
                doc.EditRebuild3();
                var created = TopFeatures(doc).FirstOrDefault(f => !names0.Contains(f.Name) && f.GetTypeName2() == "SurfCut") ?? cut;
                double v1 = ExactVolume(doc); double removed = v0 - v1; int err = FeatureError(created);
                var res = new JObject { ["feature"] = created.Name, ["surface"] = name, ["flip"] = flip, ["volume_before"] = v0, ["volume_after"] = v1, ["removed_volume"] = removed, ["feature_error"] = err };
                if (removed <= 1e-15 || (maxRemoved.HasValue && removed > maxRemoved.Value))
                {
                    string why = removed <= 1e-15 ? "the cut removed nothing (the surface does not cross the solid, or it is on the other side: flip, or extrude it the other way)"
                        : "the cut removed " + (removed * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3, more than max_removed_volume " + (maxRemoved.Value * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3 (a wrong flip removes the larger side)";
                    DeleteFeatureQuietly(doc, created.Name); doc.EditRebuild3();
                    throw new ToolFailure(removed <= 1e-15 ? "NOTHING_CUT" : "CUT_REMOVED_TOO_MUCH", why + ". The feature was taken out again.");
                }
                res["verified"] = err == 0; if (err != 0) res["problems"] = new JArray("the feature reports rebuild error " + err);
                return res;
            });
        }
    }
}
