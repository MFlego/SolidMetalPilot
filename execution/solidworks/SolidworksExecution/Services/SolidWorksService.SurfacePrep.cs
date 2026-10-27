// Surface preparation for sheet metal that is converted rather than built from flanges: the BASE SHAPE of a rim-and-lip enclosure is the outer skin of the final part, made as a solid (box + a lip block
// on its front) whose open front face is DELETED, and then OFFSET inward by one sheet thickness - because Convert to Sheet Metal with "reverse thickness" puts the material on the OUTSIDE of the
// surface, and the folded part must keep its outer size (user recipe, v3 -> v4: "reverse thickness: offset the base surface inward by t before converting, else the overall size is wrong").
// Both tools are plain typed API (IModelDoc.InsertDeleteFace, IModelDoc2.InsertOffsetSurface), so both hosts run them. Entities are identified by a POINT ON them (meters), never by a view pick.
// Lengths are METERS.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        // every body (solid AND surface), visible or not; the shared FlattenFaces / FlattenEdges only walk solid bodies, which is why surface-body work needs its own enumeration
        private static List<IBody2> AllBodies(IPartDoc part, bool visibleOnly)
        {
            var l = new List<IBody2>(); var bs = part.GetBodies2((int)swBodyType_e.swAllBodies, visibleOnly) as object[];
            if (bs != null) foreach (var b in bs) { var bb = b as IBody2; if (bb != null) l.Add(bb); }
            return l;
        }

        // the face (of any body) nearest to a point, and how far it is
        private static IFace2 FaceNearest(IPartDoc part, double[] pt, out double dist)
        {
            IFace2 best = null; dist = double.MaxValue;
            foreach (var b in AllBodies(part, false))
            {
                var fs = b.GetFaces() as object[]; if (fs == null) continue;
                foreach (var o in fs)
                {
                    var f = o as IFace2; if (f == null) continue;
                    var q = f.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; if (q == null || q.Length < 3) continue;
                    double d = Math.Sqrt((q[0] - pt[0]) * (q[0] - pt[0]) + (q[1] - pt[1]) * (q[1] - pt[1]) + (q[2] - pt[2]) * (q[2] - pt[2])); if (d < dist) { dist = d; best = f; }
                }
            }
            return best;
        }

        private static JObject BodyCounts(IPartDoc part)
        {
            Func<swBodyType_e, int> n = t => { var bs = part.GetBodies2((int)t, false) as object[]; return bs == null ? 0 : bs.Length; };
            return new JObject { ["solid_bodies"] = n(swBodyType_e.swSolidBody), ["surface_bodies"] = n(swBodyType_e.swSheetBody) };
        }

        private static double[] BoxOf(IBody2 b) { var x = b.GetBodyBox() as double[]; return x == null ? null : new[] { x[0], x[1], x[2], x[3], x[4], x[5] }; }

        private static JArray SizeOf(double[] box) { return box == null ? null : new JArray(Math.Round(box[3] - box[0], 9), Math.Round(box[4] - box[1], 9), Math.Round(box[5] - box[2], 9)); }

        // ---- delete_face ------------------------------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse DeleteFace(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "delete_face needs an active part.");
                var pts = AsArray(p["faces"], "faces"); if (pts == null || pts.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "faces is required: [[x, y, z], ...] meters, one point ON each face to delete.");
                var faces = new List<IFace2>();
                foreach (var t in pts)
                {
                    var pt = ToPoint(t, "each face point"); double d; var f = FaceNearest(part, pt, out d);
                    if (f == null || d > 1e-4) throw new ToolFailure("FACE_NOT_FOUND", "No face within 0.1 mm of (" + string.Join(", ", pt.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)).ToArray()) + ") m (nearest " + (d * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm).");
                    faces.Add(f);
                }
                var before = BodyCounts(part); var names = TopNames(doc);
                doc.ClearSelection2(true);
                bool append = false; foreach (var f in faces) { if (!((IEntity)f).Select4(append, null)) throw new ToolFailure("SELECTION_FAILED", "Could not select one of the faces."); append = true; }
                // IModelDoc.InsertDeleteFace() lives on the OLD interface only: late-bound through IDispatch (Delete; there is no patch / fill variant on this call)
                try { ((object)doc).GetType().InvokeMember("InsertDeleteFace", BindingFlags.InvokeMethod, null, doc, null); }
                catch (TargetInvocationException ex) { throw new ToolFailure("DELETE_FACE_FAILED", "InsertDeleteFace raised " + (ex.InnerException == null ? ex.Message : ex.InnerException.Message)); }
                doc.ClearSelection2(true); doc.EditRebuild3();
                var created = TopFeatures(doc).FirstOrDefault(f => !names.Contains(f.Name) && f.GetTypeName2() == "DelFace");
                if (created == null) throw new ToolFailure("DELETE_FACE_FAILED", "SolidWorks built no Delete Face feature (is the selected face part of a body the command accepts?).");
                int err = FeatureError(created); var after = BodyCounts(part);
                var res = new JObject { ["feature"] = created.Name, ["faces_deleted"] = faces.Count, ["bodies_before"] = before, ["bodies_after"] = after, ["feature_error"] = err };
                var problems = new JArray();
                if (err != 0) problems.Add("the feature reports rebuild error " + err);
                if (after.Value<int>("surface_bodies") <= before.Value<int>("surface_bodies") && after.Value<int>("solid_bodies") >= before.Value<int>("solid_bodies")) problems.Add("the body count did not change: a solid whose face was deleted becomes a surface body");
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = err == 0 && problems.Count == 0;
                return res;
            });
        }

        // ---- offset_surface ---------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Offset Surface of one surface body by `distance`; direction "inward" (default) shrinks it, "outward" grows it. The API call has a Reverse flag whose meaning follows the faces' own orientation, so the
        // RESULT is measured (bounding box inside / outside the original) and, when it went the wrong way, the feature is deleted and done again with the flag flipped - once.
        public ExecutionResponse OffsetSurface(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "offset_surface needs an active part.");
                double distance = p.Value<double?>("distance") ?? 0; if (distance <= 0 || distance > 0.1) throw new ToolFailure("INVALID_PARAMETER", "distance is in METERS (0 < d <= 100 mm).");
                string dir = (p.Value<string>("direction") ?? "inward").ToLowerInvariant(); if (dir != "inward" && dir != "outward") throw new ToolFailure("INVALID_PARAMETER", "direction must be inward or outward.");
                var tok = p["body_point"]; if (tok != null && tok.Type == JTokenType.String) tok = AsArray(tok, "body_point");
                var pt = ToPoint(tok, "body_point"); double fd; var face = FaceNearest(part, pt, out fd);
                if (face == null || fd > 1e-4) throw new ToolFailure("FACE_NOT_FOUND", "No face within 0.1 mm of body_point; give a point ON the surface body to offset (nearest " + (fd * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm).");
                var body = face.GetBody() as IBody2; if (body == null) throw new ToolFailure("FACE_NOT_FOUND", "The face belongs to no body.");
                bool isSheet = body.GetType() == (int)swBodyType_e.swSheetBody; if (!isSheet && !(p.Value<bool?>("allow_solid") ?? false)) throw new ToolFailure("NOT_A_SURFACE_BODY", "The body at body_point is a solid; offset_surface works on surface bodies (delete_face turns a solid into one).");
                bool hideOriginal = p.Value<bool?>("hide_original") ?? true;
                var box0 = BoxOf(body); var names = TopNames(doc); var res = new JObject { ["distance"] = distance, ["direction"] = dir, ["size_before"] = SizeOf(box0) };

                IFeature feat = null; double[] box1 = null; bool flipped = false; var attempts = new JArray(); res["attempts"] = attempts;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    // InsertOffsetSurface's Reverse follows the faces' own orientation: measured on a box with a deleted face, Reverse = TRUE goes INWARD (100 x 60 x 30 -> 97 x 57 x 28.5 for 1.5 mm) and
                    // false OUTWARD. That is only the first guess - the result is measured below and the call repeated with the flag flipped when it went the other way.
                    bool reverse = (dir == "inward") != (attempt == 1);
                    doc.ClearSelection2(true);
                    var faceNow = FaceNearest(part, pt, out fd); var bodyNow = faceNow == null ? null : faceNow.GetBody() as IBody2; if (bodyNow == null) throw new ToolFailure("FACE_NOT_FOUND", "The body disappeared between the attempts.");
                    if (!bodyNow.Select2(false, null)) throw new ToolFailure("SELECTION_FAILED", "Could not select the surface body.");
                    doc.InsertOffsetSurface(distance, reverse);
                    doc.ClearSelection2(true); doc.EditRebuild3();
                    feat = TopFeatures(doc).FirstOrDefault(f => !names.Contains(f.Name) && f.GetTypeName2() == "OffsetRefSurface");
                    if (feat == null) throw new ToolFailure("OFFSET_FAILED", "SolidWorks built no Offset Surface feature.");
                    // the new body: the sheet body that is not the original (by box)
                    IBody2 created = null; foreach (var b in AllBodies(part, false)) { var bx = BoxOf(b); if (b.GetType() == (int)swBodyType_e.swSheetBody && bx != null && !BoxesEqual(bx, box0)) created = b; }
                    if (created == null) { DeleteFeatureQuietly(doc, feat.Name); throw new ToolFailure("OFFSET_FAILED", "The offset produced no new surface body (the feature was removed)."); }
                    box1 = BoxOf(created); attempts.Add(new JObject { ["reverse"] = reverse, ["size"] = SizeOf(box1), ["min"] = new JArray(box1[0], box1[1], box1[2]), ["max"] = new JArray(box1[3], box1[4], box1[5]) });
                    bool inside = box1[0] >= box0[0] - 1e-9 && box1[1] >= box0[1] - 1e-9 && box1[2] >= box0[2] - 1e-9 && box1[3] <= box0[3] + 1e-9 && box1[4] <= box0[4] + 1e-9 && box1[5] <= box0[5] + 1e-9;
                    bool outside = box1[0] <= box0[0] + 1e-9 && box1[1] <= box0[1] + 1e-9 && box1[2] <= box0[2] + 1e-9 && box1[3] >= box0[3] - 1e-9 && box1[4] >= box0[4] - 1e-9 && box1[5] >= box0[5] - 1e-9;
                    if ((dir == "inward" && inside && !outside) || (dir == "outward" && outside && !inside)) break;
                    if (attempt == 1) { res["problems"] = new JArray("the offset went " + (inside ? "inward" : outside ? "outward" : "neither way") + ", not " + dir); break; }
                    flipped = true;                      // undo: delete this feature and try the flag the other way
                    DeleteFeatureQuietly(doc, feat.Name); doc.EditRebuild3();
                    names = TopNames(doc);
                }
                if (hideOriginal) { foreach (var b in AllBodies(part, true)) { var bx = BoxOf(b); if (bx != null && BoxesEqual(bx, box0)) { b.HideBody(true); res["hidden_original"] = true; } } }
                int err = FeatureError(feat);
                res["feature"] = feat.Name; res["size_after"] = SizeOf(box1); res["reverse_flag_flipped"] = flipped; res["feature_error"] = err;
                var shrink = new[] { (box0[3] - box0[0]) - (box1[3] - box1[0]), (box0[4] - box0[1]) - (box1[4] - box1[1]), (box0[5] - box0[2]) - (box1[5] - box1[2]) };
                res["size_change"] = new JArray(shrink.Select(v => (object)Math.Round(-v, 9)).ToArray());
                res["verified"] = err == 0 && res["problems"] == null;
                return res;
            });
        }

        private static bool BoxesEqual(double[] a, double[] b) { for (int i = 0; i < 6; i++) if (Math.Abs(a[i] - b[i]) > 1e-9) return false; return true; }
    }
}
