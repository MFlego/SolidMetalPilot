// move_face: MOVE FACE (offset, translate or rotate faces) through IFeatureManager.InsertMoveFace3 - the middle step of the user's closed-corner chassis (Closed Corner -> Move Face -> Corner-Trim), which was
// driven by keyboard scripts until probe 79. The first attempts returned null; the INPUT that decides is the selection MARK: the faces must be selected with Mark = 1 (SelectData.Mark), then
//     offset    InsertMoveFace3(0, reverse, 0, distance, null, null, 0, 0)        every face moves by `distance` along its normal (reverse = the other way: shrinks a free end); several faces in one feature
//     translate InsertMoveFace3(1, reverse, 0, length, {dx, dy, dz}, null, 0, 0)  TranslationParams = the displacement VECTOR in meters (the distance argument is not used: {0,0,1} moved a face by 1 m)
//     rotate    InsertMoveFace3(2, reverse, angle, 0, null, {px, py, pz, dx, dy, dz}, 0, 0)   RotationParams = a point of the axis and its direction (angle in radians)
// The feature's distance is the dimension "D1@Move Face<n>" (an equation can drive it: "D1@Move Face1" = "SheetThickness" + 0.1). Lengths METERS, angles DEGREES at this boundary.
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
        private static double[] Vec3(JToken tok, string name)
        {
            var a = AsArray(tok, name); if (a == null || a.Count != 3) throw new ToolFailure("INVALID_PARAMETER", name + " must be [x, y, z] in meters.");
            return new[] { a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>() };
        }

        public ExecutionResponse MoveFace(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "move_face needs an active part.");
                var faces = ResolveFaces(doc, p["faces"], p["points"]);
                if (faces.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "faces (indices from analyze_model(faces)) and / or points ([[x,y,z], ...] meters, ON the faces): the faces to move.");
                string type = (p.Value<string>("move_type") ?? "offset").Trim().ToLowerInvariant(); bool reverse = p.Value<bool?>("reverse") ?? false;
                double distance = p.Value<double?>("distance") ?? 0.0; object trans = null, rot = null; double angle = 0.0; int mt;
                switch (type)
                {
                    case "offset":
                        mt = 0; if (distance <= 0) throw new ToolFailure("MISSING_PARAMETER", "distance (meters, > 0): how far each face moves along its normal (reverse = true moves it the other way).");
                        break;
                    case "translate":
                        mt = 1; var tv = Vec3(p["translation"], "translation"); if (Math.Sqrt(tv[0] * tv[0] + tv[1] * tv[1] + tv[2] * tv[2]) < 1e-9) throw new ToolFailure("MISSING_PARAMETER", "translation = [dx, dy, dz] (meters, not zero): the displacement of the faces.");
                        trans = tv; distance = Math.Sqrt(tv[0] * tv[0] + tv[1] * tv[1] + tv[2] * tv[2]); break;
                    case "rotate":
                        mt = 2; var rp = p["rotation"] as JObject ?? AsObject(p["rotation"], "rotation");
                        if (rp == null || rp["axis_point"] == null || rp["axis_direction"] == null || rp["angle"] == null) throw new ToolFailure("MISSING_PARAMETER", "rotation = {axis_point: [x,y,z], axis_direction: [x,y,z], angle: degrees} (meters): the axis the faces turn about.");
                        var ap = Vec3(rp["axis_point"], "rotation.axis_point"); var ad = Vec3(rp["axis_direction"], "rotation.axis_direction");
                        if (Math.Sqrt(ad[0] * ad[0] + ad[1] * ad[1] + ad[2] * ad[2]) < 1e-9) throw new ToolFailure("INVALID_PARAMETER", "rotation.axis_direction must not be zero.");
                        rot = new[] { ap[0], ap[1], ap[2], ad[0], ad[1], ad[2] }; angle = rp.Value<double>("angle") * Math.PI / 180; if (Math.Abs(angle) < 1e-12) throw new ToolFailure("INVALID_PARAMETER", "rotation.angle (degrees) must not be zero."); break;
                    default: throw new ToolFailure("INVALID_PARAMETER", "move_type must be 'offset' (default), 'translate' or 'rotate'.");
                }

                var before = BodyStats(doc); double vol0 = 0; try { vol0 = ExactVolume(doc); } catch (Exception) { }
                var names0 = TopNames(doc);
                doc.ClearSelection2(true);
                var sd = ((ISelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = 1;          // the faces to move carry mark 1 (mark 0 returns null)
                for (int i = 0; i < faces.Count; i++)
                    if (!((IEntity)faces[i]).Select4(i > 0, sd)) { doc.ClearSelection2(true); throw new ToolFailure("SELECTION_FAILED", "Could not select face " + (i + 1) + " of " + faces.Count + " for the move."); }
                var feat = doc.FeatureManager.InsertMoveFace3(mt, reverse, angle, distance, trans, rot, 0, 0.0) as IFeature;
                doc.ClearSelection2(true);
                if (feat == null) throw new ToolFailure("MOVE_FACE_FAILED", "SolidWorks built no Move Face. Check that the faces belong to one body and can move that way (an offset that would make a face vanish, a translation that tears the body apart, or a rotation about an axis that does not touch the body all fail).");
                doc.EditRebuild3(); int err = FeatureError(feat);
                var problems = new JArray();
                var res = new JObject { ["feature"] = feat.Name, ["move_type"] = type, ["faces"] = faces.Count, ["reverse"] = reverse };
                if (type == "offset") res["distance"] = distance;
                // the definition SolidWorks kept (AccessSelections on the CREATED feature is the supported use) and the dimension that holds the distance
                try
                {
                    var d2 = feat.GetDefinition() as IMoveFaceFeatureData; bool acc = false;
                    if (d2 != null)
                    {
                        try
                        {
                            acc = d2.AccessSelections((ModelDoc2)doc, null);
                            var def = new JObject { ["move_type"] = d2.MoveType, ["distance"] = d2.Distance, ["reverse"] = d2.ReverseDirection, ["angle"] = d2.Angle, ["faces"] = d2.GetFacesCount() };
                            res["definition"] = def;
                            if (d2.MoveType != mt) problems.Add("SolidWorks kept move type " + d2.MoveType + ", " + mt + " was asked");
                            if (type == "offset" && Math.Abs(d2.Distance - distance) > 1e-9) problems.Add("SolidWorks kept a distance of " + (d2.Distance * 1000).ToString("0.######", CultureInfo.InvariantCulture) + " mm, " + (distance * 1000).ToString("0.######", CultureInfo.InvariantCulture) + " mm was asked");
                            if (d2.ReverseDirection != reverse) problems.Add("SolidWorks kept the direction flag " + d2.ReverseDirection + ", " + reverse + " was asked");
                            if (d2.GetFacesCount() != faces.Count) problems.Add("the feature holds " + d2.GetFacesCount() + " faces, " + faces.Count + " were selected");
                        }
                        finally { if (acc) d2.ReleaseSelectionAccess(); }
                    }
                }
                catch (System.Runtime.InteropServices.COMException ex) { problems.Add("the definition could not be read back: " + ex.Message); }
                var dims = new JArray(); try { var dd = feat.GetFirstDisplayDimension() as IDisplayDimension; int guard = 0; while (dd != null && guard++ < 10) { var dim = dd.GetDimension2(0); if (dim != null) dims.Add(dim.Name + "@" + feat.Name); dd = feat.GetNextDisplayDimension(dd) as IDisplayDimension; } } catch (Exception) { }
                res["dimensions"] = dims;
                var after = BodyStats(doc); res["body_before"] = before; res["body_after"] = after;
                try { double vol1 = ExactVolume(doc); res["volume_change"] = vol1 - vol0; if (Math.Abs(vol1 - vol0) < 1e-15) problems.Add("the move changed the volume by nothing"); } catch (Exception) { }
                if (err != 0) problems.Add("the feature reports rebuild error " + err);
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }
    }
}
