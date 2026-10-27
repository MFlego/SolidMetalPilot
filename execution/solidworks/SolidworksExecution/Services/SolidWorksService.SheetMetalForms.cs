// Sheet-metal FORMING features through the typed API (found and proven by probes 40-41, see SolidPilotBridge/SHEETMETAL-FINDINGS.txt): hems, break corners (fillet / chamfer) and the mitre flange.
// Edges and faces are identified by the stable indices of analyze_model(edges|faces) (or by a point ON them) and are selected as OBJECTS with Select4: SelectByID2(x, y, z) picks through the current view
// and is not reliable. Lengths are METERS, angles DEGREES at this boundary (like the other tools).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        // ---- shared helpers -----------------------------------------------------------------------------------------------------------------------------------------------------------
        private static void RequireSheetMetalPart(IModelDoc2 doc, string tool)
        {
            if (!(doc is IPartDoc)) throw new ToolFailure("WRONG_DOCUMENT_TYPE", tool + " needs an active part.");
            if (FirstOfType(doc, "SheetMetal") == null) throw new ToolFailure("NOT_SHEET_METAL", "The active part has no Sheet-Metal feature.");
        }

        private static double[] ToPoint(JToken t, string what)
        {
            var a = t as JArray; if (a == null || a.Count < 3) throw new ToolFailure("INVALID_PARAMETER", what + " must be [x, y, z] in meters.");
            return new[] { a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>() };
        }

        // edges from stable indices (analyze_model(edges)) and/or points on them
        private List<IEdge> ResolveEdges(IModelDoc2 doc, JToken indices, JToken points)
        {
            var list = new List<IEdge>(); var all = FlattenEdges(doc as IPartDoc);
            var idx = AsArray(indices, "edges");
            if (idx != null)
                foreach (var t in idx)
                {
                    int i = t.Value<int>();
                    if (i < 0 || i >= all.Count) throw new ToolFailure("EDGE_NOT_FOUND", "Edge index " + i + " out of range (part has " + all.Count + " edges). Call analyze_model(edges).");
                    list.Add(all[i]);
                }
            var pts = AsArray(points, "points");
            if (pts != null)
                foreach (var t in pts)
                {
                    var pt = ToPoint(t, "each point"); var e = EdgeAtPoint(doc, pt);
                    if (e == null) throw new ToolFailure("EDGE_NOT_FOUND", "No straight edge within 0.1 mm of (" + string.Join(", ", pt.Select(v => v.ToString("G6")).ToArray()) + ") m. Prefer edges = indices from analyze_model(edges).");
                    list.Add(e);
                }
            return list;
        }

        private List<IFace2> ResolveFaces(IModelDoc2 doc, JToken indices, JToken points)
        {
            var list = new List<IFace2>(); var all = FlattenFaces(doc as IPartDoc);
            var idx = AsArray(indices, "faces");
            if (idx != null)
                foreach (var t in idx)
                {
                    int i = t.Value<int>();
                    if (i < 0 || i >= all.Count) throw new ToolFailure("FACE_NOT_FOUND", "Face index " + i + " out of range (part has " + all.Count + " faces). Call analyze_model(faces).");
                    list.Add(all[i]);
                }
            var pts = AsArray(points, "points");
            if (pts != null)
                foreach (var t in pts)
                {
                    var pt = ToPoint(t, "each point"); var f = FaceAtPoint(doc, pt);
                    if (f == null) throw new ToolFailure("FACE_NOT_FOUND", "No face within 0.1 mm of (" + string.Join(", ", pt.Select(v => v.ToString("G6")).ToArray()) + ") m. Prefer faces = indices from analyze_model(faces).");
                    list.Add(f);
                }
            return list;
        }

        private static void SelectObjects(IModelDoc2 doc, IEnumerable<IEntity> items, string what)
        {
            doc.ClearSelection2(true); bool append = false;
            foreach (var it in items)
            {
                if (it == null || !it.Select4(append, null)) throw new ToolFailure("SELECTION_FAILED", "Could not select one of the " + what + ".");
                append = true;
            }
        }

        private static HashSet<string> TopNames(IModelDoc2 doc) { return new HashSet<string>(TopFeatures(doc).Select(f => f.Name)); }

        // current values of a feature's definition (COM objects like Edges are left out), for the report
        private JObject DefinitionValues(IFeature f, IModelDoc2 doc)
        {
            object def = null; try { def = f.GetDefinition(); } catch (COMException) { }
            if (def == null) return null;
            var iface = DefinitionInterface(f, def); if (iface == null) return null;
            bool acc = AccessSelections(iface, def, doc);
            try
            {
                var o = new JObject();
                foreach (var pr in iface.GetProperties().OrderBy(x => x.Name))
                {
                    if (!pr.CanRead || pr.GetIndexParameters().Length > 0) continue;
                    try { var v = pr.GetValue(def, null); if (v != null && v.GetType().IsCOMObject) continue; o[pr.Name] = ValueToJson(v); } catch (Exception) { }
                }
                return o;
            }
            finally { if (acc) ReleaseSelections(iface, def); }
        }

        private double SheetRadius(IModelDoc2 doc)
        {
            var sm = FirstOfType(doc, "SheetMetal"); if (sm == null) return 0.001;
            var d = sm.GetDefinition() as ISheetMetalFeatureData; if (d == null) return 0.001;
            bool acc = false; try { acc = d.AccessSelections(doc, null); return d.BendRadius; } finally { if (acc) d.ReleaseSelectionAccess(); }
        }

        private CustomBendAllowance KFactorAllowance(IModelDoc2 doc, double? k)
        {
            if (!k.HasValue) return null;
            var cba = (CustomBendAllowance)doc.FeatureManager.CreateCustomBendAllowance(); cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor; cba.KFactor = k.Value; return cba;
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sheet_metal_hem
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse SheetMetalHem(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                RequireSheetMetalPart(doc, "sheet_metal_hem");
                var edges = ResolveEdges(doc, p["edges"], p["points"]);
                if (edges.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "edges (indices from analyze_model(edges)) and/or points ([[x,y,z], ...] on the free edge, meters) are required: the edge(s) to hem.");
                string kind = (p.Value<string>("hem_type") ?? "closed").ToLowerInvariant(); int type; double defaultAngle;
                switch (kind)
                {
                    case "open": type = (int)swHemTypes_e.swHemTypeOpen; defaultAngle = 90; break;
                    case "closed": type = (int)swHemTypes_e.swHemTypeClosed; defaultAngle = 180; break;
                    case "tear_drop": case "teardrop": kind = "tear_drop"; type = (int)swHemTypes_e.swHemTypeTearDrop; defaultAngle = 225; break;
                    case "rolled": type = (int)swHemTypes_e.swHemTypeRolled; defaultAngle = 180; break;
                    case "double": type = (int)swHemTypes_e.swHemTypeDouble; defaultAngle = 180; break;
                    default: throw new ToolFailure("INVALID_PARAMETER", "hem_type must be open, closed, tear_drop, rolled or double.");
                }
                string posName = (p.Value<string>("bend_position") ?? "material_inside").ToLowerInvariant(); int pos;
                if (posName == "material_inside") pos = (int)swHemPositionTypes_e.swHemPositionTypeInside; else if (posName == "bend_outside") pos = (int)swHemPositionTypes_e.swHemPositionTypeOutside;
                else throw new ToolFailure("INVALID_PARAMETER", "bend_position must be material_inside or bend_outside.");
                double length = p.Value<double?>("length") ?? 0.0;
                if (length <= 0 && (kind == "open" || kind == "closed" || kind == "double")) throw new ToolFailure("MISSING_PARAMETER", "length (meters, > 0) is required for open / closed / double hems.");
                if (length <= 0) length = 0.01;                                    // tear_drop and rolled are sized by radius and angle; the API still wants a value
                double gap = p.Value<double?>("gap") ?? (kind == "closed" ? 0.0001 : 0.0001524);
                double angle = (p.Value<double?>("angle") ?? defaultAngle) * Math.PI / 180.0;
                double radius = p.Value<double?>("radius") ?? SheetRadius(doc);
                double miterGap = p.Value<double?>("miter_gap") ?? 0.0005; bool reverse = p.Value<bool?>("reverse") ?? false;

                var before = BodyStats(doc); var allowance = KFactorAllowance(doc, p.Value<double?>("k_factor"));      // every argument is computed BEFORE selecting: reading a definition (AccessSelections) clears the selection
                SelectObjects(doc, edges.Cast<IEntity>(), "edges");
                var feat = doc.FeatureManager.InsertSheetMetalHem2(type, pos, reverse, length, gap, angle, radius, miterGap, allowance, true, 1, 1, true, 0.5, 0.0, 0.0) as IFeature;
                doc.ClearSelection2(true);
                if (feat == null)
                    throw new ToolFailure("HEM_FAILED", "SolidWorks built no " + kind + " hem. Check: the edge is a free edge of a flange wall (not a bend or a sheet face edge); " +
                        (kind == "tear_drop" ? "a tear-drop hem needs 180 < angle < 270 degrees (225 works); " : kind == "double" ? "a double hem refused every length tried (6, 10, 20 mm) - try closed; " : "") + "the length leaves room for the hem.");
                int err = FeatureError(feat); var after = BodyStats(doc);
                var res = new JObject { ["feature"] = feat.Name, ["hem_type"] = kind, ["edges"] = edges.Count, ["definition"] = DefinitionValues(feat, doc), ["body_before"] = before, ["body_after"] = after, ["feature_error"] = err };
                res["verified"] = err == 0 && after.Value<int?>("faces") > before.Value<int?>("faces");
                return res;
            });
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sheet_metal_break_corner
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse SheetMetalBreakCorner(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                RequireSheetMetalPart(doc, "sheet_metal_break_corner");
                var faces = ResolveFaces(doc, p["faces"], p["points"]);
                if (faces.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "faces (indices from analyze_model(faces)) and/or points (on the face, meters) are required: the wall / flange face(s) whose corners are broken.");
                string kind = (p.Value<string>("corner_type") ?? "fillet").ToLowerInvariant(); int type;
                if (kind == "fillet") type = (int)swBreakCornerTypes_e.swBreakCornerTypeFillet; else if (kind == "chamfer") type = (int)swBreakCornerTypes_e.swBreakCornerTypeChamfer;
                else throw new ToolFailure("INVALID_PARAMETER", "corner_type must be fillet or chamfer.");
                double dist = p.Value<double?>("distance") ?? 0.0; if (dist <= 0) throw new ToolFailure("MISSING_PARAMETER", "distance (meters, > 0): the fillet radius / chamfer distance.");

                var before = BodyStats(doc); var names = TopNames(doc);
                SelectObjects(doc, faces.Cast<IEntity>(), "faces");
                doc.InsertSheetMetalBreakCorner(type, dist);
                doc.ClearSelection2(true);
                var feat = TopFeatures(doc).FirstOrDefault(f => f.GetTypeName2() == "BreakCorner" && !names.Contains(f.Name));
                if (feat == null) throw new ToolFailure("BREAK_CORNER_FAILED", "No Break-Corner feature was created. It needs the planar FACE of a sheet-metal wall or flange (its corners are broken); an edge, a wall's end face or the base-flange face next to flanges does not work.");

                // the option flags are not creation arguments: set them on the definition and read back
                bool? internalOnly = p.Value<bool?>("internal_corners_only"), centered = p.Value<bool?>("centered_on_bend_lines"); var problems = new JArray();
                if (internalOnly.HasValue || centered.HasValue)
                {
                    var d = feat.GetDefinition() as IBreakCornerFeatureData; bool acc = d.AccessSelections(doc, null);
                    try { if (internalOnly.HasValue) d.InternalCornersOnly = internalOnly.Value; if (centered.HasValue) d.CenteredOnBendLines = centered.Value; if (!feat.ModifyDefinition(d, doc, null)) problems.Add("ModifyDefinition returned false for the option flags"); }
                    finally { if (acc) d.ReleaseSelectionAccess(); }
                    var back = feat.GetDefinition() as IBreakCornerFeatureData;
                    if (internalOnly.HasValue && back.InternalCornersOnly != internalOnly.Value) problems.Add("internal_corners_only did not take effect");
                    if (centered.HasValue && back.CenteredOnBendLines != centered.Value) problems.Add("centered_on_bend_lines did not take effect");
                }
                int err = FeatureError(feat); var after = BodyStats(doc);
                var res = new JObject { ["feature"] = feat.Name, ["corner_type"] = kind, ["faces"] = faces.Count, ["definition"] = DefinitionValues(feat, doc), ["body_before"] = before, ["body_after"] = after, ["feature_error"] = err };
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = err == 0 && problems.Count == 0 && after.Value<int?>("faces") > before.Value<int?>("faces");
                return res;
            });
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sheet_metal_miter_flange - one feature along a chain of edges, with the profile sketch generated at the start of the chain
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static double[] V(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        private static double[] Unit(double[] a) { double l = Math.Sqrt(Dot(a, a)); return l < 1e-15 ? a : new[] { a[0] / l, a[1] / l, a[2] / l }; }
        private static double[] Along(double[] p, double[] u, double s) { return new[] { p[0] + u[0] * s, p[1] + u[1] * s, p[2] + u[2] * s }; }
        private static bool Same(double[] a, double[] b) { return Math.Abs(a[0] - b[0]) < 1e-6 && Math.Abs(a[1] - b[1]) < 1e-6 && Math.Abs(a[2] - b[2]) < 1e-6; }

        // Draws the miter flange's profile: a line from the start of the chain, rising from the sheet face by `height` at `angle` (90 = perpendicular to the sheet) on the planar end face that is
        // perpendicular to the first edge. Returns the sketch name and what it drew (model space, meters).
        private string MakeMiterProfile(IModelDoc2 doc, List<IEdge> edges, double height, double angleRad, out JObject drawn)
        {
            var first = edges[0]; var second = edges.Count > 1 ? edges[1] : null;
            var a = ((IVertex)first.GetStartVertex()).GetPoint() as double[]; var b = ((IVertex)first.GetEndVertex()).GetPoint() as double[];
            if (a == null || b == null) throw new ToolFailure("EDGE_NOT_FOUND", "The first edge has no end vertices (a closed curve?): the chain must start on a straight edge.");
            double[] p0 = a, p1 = b;                                       // p0: where the profile goes (the end of the first edge that does not continue into the second edge)
            if (second != null)
            {
                var sa = ((IVertex)second.GetStartVertex()).GetPoint() as double[]; var sb = ((IVertex)second.GetEndVertex()).GetPoint() as double[];
                if (sa != null && sb != null && (Same(a, sa) || Same(a, sb))) { p0 = b; p1 = a; }
            }
            var d = Unit(V(p1, p0));                                        // along the first edge, away from the profile

            // the sheet face next to the edge: the larger planar one
            var adj = first.GetTwoAdjacentFaces2() as object[]; IFace2 sheet = null; double best = -1;
            if (adj != null) foreach (var o in adj) { var f = o as IFace2; if (f == null) continue; var s = f.GetSurface() as ISurface; if (s == null || !s.IsPlane()) continue; double ar = f.GetArea(); if (ar > best) { best = ar; sheet = f; } }
            if (sheet == null) throw new ToolFailure("MITER_PROFILE_FAILED", "The first edge borders no planar sheet face.");
            var n = Unit(sheet.Normal as double[]);                          // outward from the material
            var onFace = PointOnFace(sheet); var inward = V(onFace, p0); inward = Unit(V(inward, new[] { d[0] * Dot(inward, d), d[1] * Dot(inward, d), d[2] * Dot(inward, d) }));    // across the sheet, toward its interior
            var dir = Unit(new[] { n[0] * Math.Sin(angleRad) - inward[0] * Math.Cos(angleRad), n[1] * Math.Sin(angleRad) - inward[1] * Math.Cos(angleRad), n[2] * Math.Sin(angleRad) - inward[2] * Math.Cos(angleRad) });
            var q = Along(p0, dir, height);

            // the planar face perpendicular to the edge that holds p0 (the sheet's end face): the sketch plane
            IFace2 plane = null;
            foreach (var f in FlattenFaces(doc as IPartDoc))
            {
                if (f == sheet) continue;
                var s = f.GetSurface() as ISurface; if (s == null || !s.IsPlane()) continue;
                var fn = f.Normal as double[]; if (fn == null || Math.Abs(Dot(Unit(fn), d)) < 0.9999) continue;
                var cp = f.GetClosestPointOn(p0[0], p0[1], p0[2]) as double[]; if (cp == null || Math.Sqrt(Dot(V(cp, p0), V(cp, p0))) > 1e-6) continue;
                plane = f; break;
            }
            if (plane == null) throw new ToolFailure("MITER_PROFILE_FAILED", "No planar face perpendicular to the first edge touches its start point, so the profile cannot be sketched automatically. Draw the profile yourself (create_sketch on a plane through the edge end) and pass profile_sketch.");

            doc.ClearSelection2(true);
            if (!((IEntity)plane).Select4(false, null)) throw new ToolFailure("SELECTION_FAILED", "Could not select the end face for the profile sketch.");
            doc.SketchManager.InsertSketch(true);
            var sk = doc.SketchManager.ActiveSketch as ISketch; if (sk == null) throw new ToolFailure("MITER_PROFILE_FAILED", "InsertSketch did not open a sketch on the end face.");
            string name = ((IFeature)sk).Name;
            try
            {
                var mu = _solidWorks.GetMathUtility() as IMathUtility; var xf = sk.ModelToSketchTransform as IMathTransform;
                Func<double[], double[]> toSketch = pt => (double[])((IMathPoint)((IMathPoint)mu.CreatePoint(pt)).MultiplyTransform(xf)).ArrayData;
                var sp = toSketch(p0); var ep = toSketch(q);
                if (doc.SketchManager.CreateLine(sp[0], sp[1], 0.0, ep[0], ep[1], 0.0) == null) throw new ToolFailure("MITER_PROFILE_FAILED", "CreateLine failed in the profile sketch.");
            }
            finally { doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); }
            drawn = new JObject { ["sketch"] = name, ["from"] = new JArray(p0), ["to"] = new JArray(q), ["direction"] = new JArray(dir), ["sheet_normal"] = new JArray(n) };
            return name;
        }

        public ExecutionResponse SheetMetalMiterFlange(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                RequireSheetMetalPart(doc, "sheet_metal_miter_flange");
                var edges = ResolveEdges(doc, p["edges"], p["points"]);
                if (edges.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "edges (indices from analyze_model(edges), in order along the chain) and/or points are required: the edges the flange runs along.");
                string profile = p.Value<string>("profile_sketch"); double height = p.Value<double?>("height") ?? 0.0;
                if (string.IsNullOrEmpty(profile) && height <= 0) throw new ToolFailure("MISSING_PARAMETER", "height (meters: the flange's height off the sheet) or profile_sketch (the name of a sketch you drew) is required.");
                double angle = (p.Value<double?>("angle") ?? 90.0) * Math.PI / 180.0;
                string posName = (p.Value<string>("position") ?? "material_inside").ToLowerInvariant(); int pos;
                switch (posName)
                {
                    case "material_inside": pos = (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialInside; break;
                    case "material_outside": pos = (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialOutside; break;
                    case "bend_outside": pos = (int)swFlangePositionTypes_e.swFlangePositionTypeBendOutside; break;
                    case "bend_centerline": pos = (int)swFlangePositionTypes_e.swFlangePositionTypeBendCenterLine; break;
                    case "bend_sharp": pos = (int)swFlangePositionTypes_e.swFlangePositionTypeBendSharp; break;
                    case "bend_tangent": pos = (int)swFlangePositionTypes_e.swFlangePositionTypeBendTangent; break;
                    default: throw new ToolFailure("INVALID_PARAMETER", "position must be material_inside, material_outside, bend_outside, bend_centerline, bend_sharp or bend_tangent.");
                }
                double? radiusArg = p.Value<double?>("bend_radius"); double ripGap = p.Value<double?>("rip_gap") ?? 0.0000762; bool trim = p.Value<bool?>("trim_side_bends") ?? true;
                if (ripGap < 0 || ripGap > 0.005) throw new ToolFailure("INVALID_PARAMETER", "rip_gap is the gap at the mitres in METERS (default 0.0000762 = 0.003 in); " + ripGap + " m is not a gap - SolidWorks would accept it and spread the flanges apart.");
                if (radiusArg.HasValue && (radiusArg.Value <= 0 || radiusArg.Value > 0.1)) throw new ToolFailure("INVALID_PARAMETER", "bend_radius is in METERS and must be > 0 (and below 100 mm).");
                double startOffset = p.Value<double?>("start_offset") ?? 0.0, endOffset = p.Value<double?>("end_offset") ?? 0.0; double reliefRatio = p.Value<double?>("relief_ratio") ?? 0.5;

                // every argument is computed BEFORE selecting: reading a definition (AccessSelections, in SheetRadius) clears the selection - which is exactly how the first version of this tool failed
                var before = BodyStats(doc); string created = null; JObject drawn = null; double radius = radiusArg ?? SheetRadius(doc); var allowance = KFactorAllowance(doc, p.Value<double?>("k_factor"));
                try
                {
                    if (string.IsNullOrEmpty(profile)) { profile = created = MakeMiterProfile(doc, edges, height, angle, out drawn); }
                    if (FindFeatureByName(doc, profile, null) == null) throw new ToolFailure("FEATURE_NOT_FOUND", "No sketch named '" + profile + "'.");
                    doc.ClearSelection2(true);
                    // the profile is selected BY NAME as a SKETCH (the selection type the command expects; Feature.Select2 selects it as a generic feature)
                    if (!doc.Extension.SelectByID2(profile, "SKETCH", 0, 0, 0, false, 0, null, 0)) throw new ToolFailure("SELECTION_FAILED", "Could not select the profile sketch '" + profile + "'.");
                    foreach (var e in edges) if (!((IEntity)e).Select4(true, null)) throw new ToolFailure("SELECTION_FAILED", "Could not select one of the edges.");

                    var feat = doc.FeatureManager.InsertSheetMetalMiterFlange(!radiusArg.HasValue, radius, ripGap, true, true, reliefRatio, 0.0, 0.0, 1, trim, pos, startOffset, endOffset, allowance) as IFeature;
                    doc.ClearSelection2(true);
                    if (feat == null)
                        throw new ToolFailure("MITER_FLANGE_FAILED", "SolidWorks built no mitre flange. Check: the edges form a chain on one sheet face (a closed loop works), in order; the profile touches the start of the first edge; height / angle leave room for the neighbouring flanges." +
                            (drawn != null ? " Profile drawn: " + drawn.ToString(Newtonsoft.Json.Formatting.None) : ""));
                    created = null;                                          // the sketch is now owned by the feature
                    int err = FeatureError(feat); var after = BodyStats(doc);
                    var res = new JObject { ["feature"] = feat.Name, ["edges"] = edges.Count, ["profile_sketch"] = profile, ["definition"] = DefinitionValues(feat, doc), ["body_before"] = before, ["body_after"] = after, ["feature_error"] = err };
                    if (drawn != null) res["profile_drawn"] = drawn;
                    res["verified"] = err == 0 && after.Value<int?>("faces") > before.Value<int?>("faces");
                    return res;
                }
                finally
                {
                    if (created != null)                                     // the call failed after the sketch was drawn: do not leave it behind
                    {
                        try { var s = FindFeatureByName(doc, created, null); if (s != null) { doc.ClearSelection2(true); s.Select2(false, 0); doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed); doc.ClearSelection2(true); } }
                        catch (COMException) { }
                    }
                }
            });
        }
    }
}
