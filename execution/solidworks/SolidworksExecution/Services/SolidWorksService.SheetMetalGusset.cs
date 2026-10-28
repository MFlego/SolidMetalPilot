// sheet_metal_gusset: the sheet-metal GUSSET (SolidWorks' "Rib" - rounded pressed rib or flat triangular - in the inside corner of a bend) through the typed API. The calls that look natural do not work (probe 69:
// IFeatureManager.InsertSheetMetalGussetFeature(2|3) returns null for every selection tried); the modern route does: CreateDefinition(swFmSMGusset) -> ISMGussetFeatureData, SupportingFaces = the two faces the
// gusset joins (a DispatchWrapper array, the sheet and the flange beside the bend), the dimensions, CreateFeature. Proven on a plate + one flange: the gusset stands at the START vertex of the bend line plus `offset`
// (SolidWorks chooses that vertex as the reference point; setting ReferencePoint before CreateFeature returns null, so the offset is always measured from it), its inner faces are `indent_width` apart and the
// triangle's legs follow the indent depth (a 4 mm depth gives 6.54 mm legs on a 1.5 mm sheet, 6 mm gives 9.36 mm). Lengths METERS, angles DEGREES at this boundary.
using System;
using System.Collections.Generic;
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
        private double SheetThickness(IModelDoc2 doc)
        {
            var sm = FirstOfType(doc, "SheetMetal"); if (sm == null) return 0.001;
            var d = sm.GetDefinition() as ISheetMetalFeatureData; if (d == null) return 0.001;
            bool acc = false; try { acc = d.AccessSelections(doc, null); return d.Thickness; } finally { if (acc) d.ReleaseSelectionAccess(); }
        }

        private static string P3(double[] p) { return p == null ? "-" : "(" + string.Join(", ", p.Select(v => (v * 1000).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + ") mm"; }

        public ExecutionResponse SheetMetalGusset(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                RequireSheetMetalPart(doc, "sheet_metal_gusset");
                var faces = ResolveFaces(doc, p["faces"], p["points"]);
                if (faces.Count != 2) throw new ToolFailure("MISSING_PARAMETER", "faces (indices from analyze_model(faces)) and / or points ([[x,y,z], ...] ON the faces, meters): exactly TWO faces are needed - the two faces the gusset joins, the sheet on one side of the bend and the flange on the other. Got " + faces.Count + ".");
                string kind = (p.Value<string>("gusset_type") ?? "rounded").ToLowerInvariant(); int type;
                if (kind == "rounded") type = (int)swSheetMetalRibGussetType_e.swSheetMetalRibGussetType_Rounded; else if (kind == "flat") type = (int)swSheetMetalRibGussetType_e.swSheetMetalRibGussetType_Flat;
                else throw new ToolFailure("INVALID_PARAMETER", "gusset_type must be 'rounded' (the pressed rib) or 'flat'.");
                string scheme = (p.Value<string>("dimension_scheme") ?? "indent_depth").ToLowerInvariant();
                if (scheme != "indent_depth" && scheme != "profile") throw new ToolFailure("INVALID_PARAMETER", "dimension_scheme must be 'indent_depth' (indent_depth + indent_width) or 'profile' (profile_length + profile_height, or profile_angle).");
                double thickness = p.Value<double?>("thickness") ?? SheetThickness(doc); double offset = p.Value<double?>("offset") ?? 0.0;
                if (offset <= 0) throw new ToolFailure("MISSING_PARAMETER", "offset (meters, > 0): where the gusset stands - its distance along the bend from the START vertex of the bend line (SolidWorks' reference point; 0 or no offset is refused).");
                double depth = p.Value<double?>("indent_depth") ?? 0, width = p.Value<double?>("indent_width") ?? 0, plen = p.Value<double?>("profile_length") ?? 0, pheight = p.Value<double?>("profile_height") ?? 0, pangle = p.Value<double?>("profile_angle") ?? 0;
                if (width <= 0) throw new ToolFailure("MISSING_PARAMETER", "indent_width (meters, > 0) is required: the width of the rib (the distance between its inner side faces), in both dimension schemes.");
                if (scheme == "indent_depth" && depth <= 0) throw new ToolFailure("MISSING_PARAMETER", "indent_depth (meters, > 0) is required for dimension_scheme 'indent_depth'.");
                if (scheme == "profile" && (plen <= 0 || (pheight <= 0 && pangle <= 0))) throw new ToolFailure("MISSING_PARAMETER", "profile_length and profile_height (or profile_angle in degrees) are required for dimension_scheme 'profile'.");
                bool draft = p.Value<bool?>("draft") ?? false, filletEdges = p.Value<bool?>("fillet_edges") ?? false, filletInner = p.Value<bool?>("fillet_inner_corners") ?? false, filletOuter = p.Value<bool?>("fillet_outer_corners") ?? false;
                double draftAngle = (p.Value<double?>("draft_angle") ?? 5.0) * Math.PI / 180, rEdge = p.Value<double?>("edge_fillet_radius") ?? 0.001, rInner = p.Value<double?>("inner_corner_fillet_radius") ?? 0.001, rOuter = p.Value<double?>("outer_corner_fillet_radius") ?? 0.001;
                bool flipSides = p.Value<bool?>("flip_sides") ?? false;

                var def = doc.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmSMGusset) as ISMGussetFeatureData;
                if (def == null) throw new ToolFailure("GUSSET_FAILED", "CreateDefinition(swFmSMGusset) returned nothing.");
                def.GussetType = type; def.ProfileType = (int)swSheetMetalGussetProfileType_e.swSheetMetalGussetProfileType_Rib; def.GussetThickness = thickness;
                def.UseOffset = true; def.OffsetDistance = offset; def.FlipDimSides = flipSides;
                // the order of the assignments matters to SolidWorks (each one is checked against the others set so far): depth, then width, then the scheme (probe 69's order)
                if (scheme == "indent_depth") { def.IndentDepth = depth; def.IndentWidth = width; def.ProfileDimensionScheme = (int)swSheetMetalGussetProfileDimType_e.swSheetMetalGussetProfileDimType_IndentDepth; }
                else
                {
                    def.IndentWidth = width; def.ProfileDimensionScheme = (int)swSheetMetalGussetProfileDimType_e.swSheetMetalGussetProfileDimType_ProfileDimensions; def.ProfileLengthDim = plen;
                    if (pangle > 0) { def.UseAngleDimForProfile = true; def.ProfileAngleDim = pangle * Math.PI / 180; } else { def.UseAngleDimForProfile = false; def.ProfileHeightDim = pheight; }
                }
                def.DraftSideFaces = draft; if (draft) def.DraftAngle = draftAngle;
                def.FilletGussetEdges = filletEdges; if (filletEdges) def.EdgeFilletRadius = rEdge;
                def.FilletInnerCorners = filletInner; if (filletInner) def.InnerCornerFilletRadius = rInner;
                def.FilletOuterCorners = filletOuter; if (filletOuter) def.OuterCornerFilletRadius = rOuter;
                def.SupportingFaces = faces.Select(f => new DispatchWrapper(f)).ToArray();

                var n1 = faces[0].Normal as double[]; var n2 = faces[1].Normal as double[]; var fbox = faces[0].GetBox() as double[];       // read BEFORE the feature: the face objects go stale on rebuild
                var names0 = TopNames(doc); var before = BodyStats(doc); double vol0 = ExactVolume(doc);
                doc.ClearSelection2(true);
                var feat = doc.FeatureManager.CreateFeature(def) as IFeature;
                if (feat == null)
                    throw new ToolFailure("GUSSET_FAILED", "SolidWorks built no gusset. Check: the two faces are the planar faces on either side of ONE bend (the sheet and the flange, inside the corner); the offset leaves room along the bend (it is measured from the bend's start vertex, and 0 is refused); the profile fits on both faces.");
                doc.EditRebuild3(); int err = FeatureError(feat);
                var problems = new JArray();
                if (err != 0) { DeleteFeatureQuietly(doc, feat.Name); throw new ToolFailure("GUSSET_FAILED", "The gusset was built but reports rebuild error " + err + " and was taken out again (usually a profile that does not fit on the faces)."); }

                // read back: what SolidWorks kept, and where it put the gusset (reference point + offset along the bend)
                var res = new JObject { ["feature"] = feat.Name, ["gusset_type"] = kind, ["dimension_scheme"] = scheme };
                var d2 = feat.GetDefinition() as ISMGussetFeatureData; bool acc = false;
                try { acc = d2.AccessSelections((ModelDoc2)doc, null); } catch (COMException) { }
                try
                {
                    Action<string, double, double, double> chk = (name, got, want, tol) => { if (Math.Abs(got - want) > tol) problems.Add(name + " is " + got.ToString("G9") + ", asked " + want.ToString("G9")); };
                    if (d2.GussetType != type) problems.Add("gusset_type did not take effect"); chk("thickness", d2.GussetThickness, thickness, 1e-9); chk("offset", d2.OffsetDistance, offset, 1e-9);
                    chk("indent_width", d2.IndentWidth, width, 1e-9);
                    if (scheme == "indent_depth") chk("indent_depth", d2.IndentDepth, depth, 1e-9);
                    else { chk("profile_length", d2.ProfileLengthDim, plen, 1e-9); if (pangle > 0) chk("profile_angle", d2.ProfileAngleDim, pangle * Math.PI / 180, 1e-9); else chk("profile_height", d2.ProfileHeightDim, pheight, 1e-9); }
                    if (draft && d2.DraftSideFaces != true) problems.Add("draft did not take effect");
                    var rp = d2.ReferencePoint as IVertex; double[] rpt = null; try { rpt = rp == null ? null : rp.GetPoint() as double[]; } catch (COMException) { }
                    // the direction along the bend: the line where the two supporting faces meet (normal x normal), pointing from the reference vertex into the part
                    double[] u = null;
                    if (rpt != null)
                    {
                        if (n1 != null && n2 != null && fbox != null)
                        {
                            u = Unit(Cross(n1, n2)); var mid = new[] { (fbox[0] + fbox[3]) / 2, (fbox[1] + fbox[4]) / 2, (fbox[2] + fbox[5]) / 2 };
                            if (Dot(u, V(mid, rpt)) < 0) u = new[] { -u[0], -u[1], -u[2] };
                        }
                    }
                    if (rpt != null) res["reference_point"] = new JArray(rpt[0], rpt[1], rpt[2]);
                    if (rpt != null && u != null) { var c = Along(rpt, u, offset); res["gusset_centre"] = new JArray(Math.Round(c[0], 9), Math.Round(c[1], 9), Math.Round(c[2], 9)); res["along_bend"] = new JArray(u[0], u[1], u[2]); }
                }
                finally { if (acc) { try { d2.ReleaseSelectionAccess(); } catch (COMException) { } } }
                var after = BodyStats(doc); double vol1 = ExactVolume(doc);
                res["body_before"] = before; res["body_after"] = after; res["volume_added"] = vol1 - vol0; res["feature_error"] = err;
                if (!(after.Value<int?>("faces") > before.Value<int?>("faces"))) problems.Add("the gusset added no face");
                if (kind == "flat" && !(vol1 > vol0)) problems.Add("a flat gusset must add material");
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }

        private static double Dist3(double[] a, double[] b) { double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2]; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    }
}
