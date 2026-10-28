// sheet_metal_gusset: the sheet-metal GUSSET (SolidWorks' "Rib" - rounded pressed rib or flat triangular - in the inside corner of a bend) through the typed API. The calls that look natural do not work (probe 69:
// IFeatureManager.InsertSheetMetalGussetFeature(2|3) returns null for every selection tried); the modern route does: CreateDefinition(swFmSMGusset) -> ISMGussetFeatureData, SupportingFaces = the two faces the
// gusset joins (a DispatchWrapper array, the sheet and the flange beside the bend), the dimensions, CreateFeature. Proven on a plate + one flange: the gusset stands at the START vertex of the bend line plus `offset`
// (SolidWorks chooses that vertex as the reference point; setting ReferencePoint before CreateFeature returns null, so the offset is always measured from it), its inner faces are `indent_width` apart and the
// triangle's legs follow the indent depth (a 4 mm depth gives 6.54 mm legs on a 1.5 mm sheet, 6 mm gives 9.36 mm). Lengths METERS, angles DEGREES at this boundary.
// THE REFERENCE POINT IS NOT STABLE (2026-10-28): once a gusset has split the bend edge, the vertex SolidWorks measures the next offset from is the start of ONE of the segments - which one changed between
// sessions (the first start in one, the last in another: a second gusset with offset 70 mm stood at +20 mm or was refused because it ran off the part at +45 mm) - and with the two faces in the other order it is
// the opposite END of the bend. Reading ReferencePoint on a new definition returns null. So `offset` stays what it always was (measured from the reference SolidWorks chooses, reported as reference_point) and
// `position` is new: the distance from the START of the bend (the end with the lowest coordinate along it) to the gusset's CENTRE. It is built by MEASURING: create at the offset the single-segment convention
// gives, read the reference point back, and when the centre is not where it was asked, take the feature out and recreate it at the offset that is right for the reference SolidWorks really used (or, when the first
// try builds nothing, learn the reference from a small probe gusset that is taken out again); the other face order is tried when the position lies behind the reference.
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
                double thickness = p.Value<double?>("thickness") ?? SheetThickness(doc); double offset = p.Value<double?>("offset") ?? 0.0; double? position = p.Value<double?>("position");
                if (position.HasValue && p["offset"] != null) throw new ToolFailure("INVALID_PARAMETER", "give position (the gusset's centre, measured from the START of the bend: always where you say) OR offset (measured from the reference vertex SolidWorks chooses, which is the bend's start only for the first gusset), not both.");
                if (position.HasValue ? position.Value <= 0 : offset <= 0) throw new ToolFailure("MISSING_PARAMETER", "position (meters, > 0): where the gusset's CENTRE stands - its distance along the bend from the bend's START (the end with the lowest coordinate along it) - or, as before, offset (the distance from the reference vertex SolidWorks chooses; 0 or none is refused).");
                double depth = p.Value<double?>("indent_depth") ?? 0, width = p.Value<double?>("indent_width") ?? 0, plen = p.Value<double?>("profile_length") ?? 0, pheight = p.Value<double?>("profile_height") ?? 0, pangle = p.Value<double?>("profile_angle") ?? 0;
                if (width <= 0) throw new ToolFailure("MISSING_PARAMETER", "indent_width (meters, > 0) is required: the width of the rib (the distance between its inner side faces), in both dimension schemes.");
                if (scheme == "indent_depth" && depth <= 0) throw new ToolFailure("MISSING_PARAMETER", "indent_depth (meters, > 0) is required for dimension_scheme 'indent_depth'.");
                if (scheme == "profile" && (plen <= 0 || (pheight <= 0 && pangle <= 0))) throw new ToolFailure("MISSING_PARAMETER", "profile_length and profile_height (or profile_angle in degrees) are required for dimension_scheme 'profile'.");
                bool draft = p.Value<bool?>("draft") ?? false, filletEdges = p.Value<bool?>("fillet_edges") ?? false, filletInner = p.Value<bool?>("fillet_inner_corners") ?? false, filletOuter = p.Value<bool?>("fillet_outer_corners") ?? false;
                double draftAngle = (p.Value<double?>("draft_angle") ?? 5.0) * Math.PI / 180, rEdge = p.Value<double?>("edge_fillet_radius") ?? 0.001, rInner = p.Value<double?>("inner_corner_fillet_radius") ?? 0.001, rOuter = p.Value<double?>("outer_corner_fillet_radius") ?? 0.001;
                bool flipSides = p.Value<bool?>("flip_sides") ?? false;

                // everything about the faces is read BEFORE the first feature (the face objects go stale on rebuild)
                var n1 = faces[0].Normal as double[]; var n2 = faces[1].Normal as double[]; var fbox = faces[0].GetBox() as double[]; var fbox2 = faces[1].GetBox() as double[];
                var names0 = TopNames(doc); var before = BodyStats(doc); double vol0 = ExactVolume(doc);
                // the bend runs along n1 x n2; its canonical direction makes the largest component positive, so "the start of the bend" does not depend on the order of the faces
                double[] uc = Unit(Cross(n1, n2)); { int k = 0; for (int q = 1; q < 3; q++) if (Math.Abs(uc[q]) > Math.Abs(uc[k])) k = q; if (uc[k] < 0) uc = new[] { -uc[0], -uc[1], -uc[2] }; }
                var mid1 = new[] { (fbox[0] + fbox[3]) / 2, (fbox[1] + fbox[4]) / 2, (fbox[2] + fbox[5]) / 2 };
                double[] ra = BoxRange(fbox, uc), rb = BoxRange(fbox2, uc); double bendStart = Math.Max(ra[0], rb[0]), bendEnd = Math.Min(ra[1], rb[1]);
                var attempts = new JArray();

                // one fresh definition per try: the faces found again by their points (in the given order or swapped), the dimensions, the offset; probe = a small flat gusset used only to learn the reference
                Func<bool, double, bool, IFeature> make = (swap, off, probe) =>
                {
                    var fs = ResolveFaces(doc, p["faces"], p["points"]); if (fs.Count != 2) throw new ToolFailure("FACE_NOT_FOUND", "The two faces could not be found again.");
                    if (swap) fs.Reverse();
                    var def = doc.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmSMGusset) as ISMGussetFeatureData;
                    if (def == null) throw new ToolFailure("GUSSET_FAILED", "CreateDefinition(swFmSMGusset) returned nothing.");
                    def.GussetType = probe ? (int)swSheetMetalRibGussetType_e.swSheetMetalRibGussetType_Flat : type; def.ProfileType = (int)swSheetMetalGussetProfileType_e.swSheetMetalGussetProfileType_Rib; def.GussetThickness = thickness;
                    def.UseOffset = true; def.OffsetDistance = off; def.FlipDimSides = !probe && flipSides;
                    // the order of the assignments matters to SolidWorks (each one is checked against the others set so far): depth, then width, then the scheme (probe 69's order)
                    if (scheme == "indent_depth") { def.IndentDepth = depth; def.IndentWidth = width; def.ProfileDimensionScheme = (int)swSheetMetalGussetProfileDimType_e.swSheetMetalGussetProfileDimType_IndentDepth; }
                    else
                    {
                        def.IndentWidth = width; def.ProfileDimensionScheme = (int)swSheetMetalGussetProfileDimType_e.swSheetMetalGussetProfileDimType_ProfileDimensions; def.ProfileLengthDim = plen;
                        if (pangle > 0) { def.UseAngleDimForProfile = true; def.ProfileAngleDim = pangle * Math.PI / 180; } else { def.UseAngleDimForProfile = false; def.ProfileHeightDim = pheight; }
                    }
                    def.DraftSideFaces = !probe && draft; if (!probe && draft) def.DraftAngle = draftAngle;
                    def.FilletGussetEdges = !probe && filletEdges; if (!probe && filletEdges) def.EdgeFilletRadius = rEdge;
                    def.FilletInnerCorners = !probe && filletInner; if (!probe && filletInner) def.InnerCornerFilletRadius = rInner;
                    def.FilletOuterCorners = !probe && filletOuter; if (!probe && filletOuter) def.OuterCornerFilletRadius = rOuter;
                    def.SupportingFaces = fs.Select(f => new DispatchWrapper(f)).ToArray();
                    doc.ClearSelection2(true);
                    return doc.FeatureManager.CreateFeature(def) as IFeature;
                };
                // the reference vertex SolidWorks used (null when it cannot be read)
                Func<IFeature, double[]> referenceOf = f0 =>
                {
                    var dd = f0.GetDefinition() as ISMGussetFeatureData; if (dd == null) return null; bool a0 = false; double[] pt = null;
                    try { a0 = dd.AccessSelections((ModelDoc2)doc, null); var rv = dd.ReferencePoint as IVertex; pt = rv == null ? null : rv.GetPoint() as double[]; }
                    catch (COMException) { }
                    finally { if (a0) { try { dd.ReleaseSelectionAccess(); } catch (COMException) { } } }
                    return pt;
                };
                Action<string> takeOut = nm =>
                {
                    DeleteFeatureQuietly(doc, nm); doc.EditRebuild3();
                    if (Math.Abs(ExactVolume(doc) - vol0) > 1e-13) throw new ToolFailure("GUSSET_FAILED", "A trial gusset ('" + nm + "') could not be taken out cleanly; delete it with edit_feature before trying again.");
                };

                IFeature feat = null; double usedOffset = offset; bool usedSwap = false;
                if (!position.HasValue)
                {
                    feat = make(false, offset, false);
                    if (feat == null)
                        throw new ToolFailure("GUSSET_FAILED", "SolidWorks built no gusset. Check: the two faces are the planar faces on either side of ONE bend (the sheet and the flange, inside the corner); the offset leaves room along the bend (it is measured from the reference vertex SolidWorks chooses - the bend's start only for the first gusset: use position instead - and 0 is refused); the profile fits on both faces.");
                }
                else
                {
                    double centreAlong = bendStart + position.Value;
                    if (centreAlong > bendEnd - width / 2 + 1e-9) throw new ToolFailure("GUSSET_FAILED", "position " + Mmf(position.Value) + " mm puts the gusset (" + Mmf(width) + " mm wide) off the end of the bend, which is " + Mmf(bendEnd - bendStart) + " mm long.");
                    if (position.Value < width / 2 - 1e-9) throw new ToolFailure("GUSSET_FAILED", "position " + Mmf(position.Value) + " mm puts the gusset (" + Mmf(width) + " mm wide) off the start of the bend.");
                    for (int ord = 0; ord < 2 && feat == null; ord++)
                    {
                        bool swap = ord == 1; double guess = swap ? bendEnd - centreAlong : position.Value;      // the single-segment convention: the reference at the bend's start (the other order: at its end)
                        IFeature f = guess > 1e-9 ? make(swap, guess, false) : null; double[] rpt = null; var at = new JObject { ["faces"] = swap ? "swapped" : "as given", ["offset"] = Math.Round(guess, 9), ["built"] = f != null };
                        if (f == null)
                        {
                            // nothing at that offset: learn the reference with a small probe, take the probe out again
                            IFeature pf = make(swap, width / 2 + 0.001, true); at["probe_built"] = pf != null;
                            if (pf != null) { rpt = referenceOf(pf); takeOut(pf.Name); }
                        }
                        else rpt = referenceOf(f);
                        if (rpt == null) { if (f != null) takeOut(f.Name); attempts.Add(at); continue; }
                        double refAlong = Dot(rpt, uc), sgn = Dot(uc, V(mid1, rpt)) >= 0 ? 1.0 : -1.0; at["reference_along_bend"] = Math.Round(refAlong - bendStart, 9);
                        if (f != null)
                        {
                            if (Math.Abs(refAlong + sgn * guess - centreAlong) < 1e-6) { feat = f; usedOffset = guess; usedSwap = swap; attempts.Add(at); break; }      // the reference was where the convention says
                            takeOut(f.Name);                                                                                                                  // it is elsewhere: take the feature out and measure from where it is
                        }
                        double need = (centreAlong - refAlong) * sgn; at["corrected_offset"] = Math.Round(need, 9); attempts.Add(at);
                        if (need <= 1e-9) continue;                                                                                                            // the position lies behind this reference: the other face order puts it at the other end
                        IFeature g = make(swap, need, false);
                        if (g != null) { feat = g; usedOffset = need; usedSwap = swap; }
                    }
                    if (feat == null)
                        throw new ToolFailure("GUSSET_FAILED", "SolidWorks built no gusset at position " + Mmf(position.Value) + " mm (the bend is " + Mmf(bendEnd - bendStart) + " mm long). Check: the two faces are the planar faces on either side of ONE bend (the sheet and the flange, inside the corner); the gusset fits between the existing ones and the ends of the bend; the profile fits on both faces. Tried: " + attempts.ToString(Newtonsoft.Json.Formatting.None));
                }
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
                    if (d2.GussetType != type) problems.Add("gusset_type did not take effect"); chk("thickness", d2.GussetThickness, thickness, 1e-9); chk("offset", d2.OffsetDistance, usedOffset, 1e-9);
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
                    if (rpt != null && u != null)
                    {
                        var c = Along(rpt, u, usedOffset); res["gusset_centre"] = new JArray(Math.Round(c[0], 9), Math.Round(c[1], 9), Math.Round(c[2], 9)); res["along_bend"] = new JArray(u[0], u[1], u[2]);
                        double at = Dot(c, uc) - bendStart; res["position"] = Math.Round(at, 9);                       // where the centre stands, measured from the START of the bend
                        if (position.HasValue && Math.Abs(at - position.Value) > 1e-6) problems.Add("the gusset's centre stands " + Mmf(at) + " mm from the bend's start, " + Mmf(position.Value) + " mm was asked");
                    }
                }
                finally { if (acc) { try { d2.ReleaseSelectionAccess(); } catch (COMException) { } } }
                var after = BodyStats(doc); double vol1 = ExactVolume(doc);
                res["body_before"] = before; res["body_after"] = after; res["volume_added"] = vol1 - vol0; res["feature_error"] = err;
                res["bend_direction"] = new JArray(uc); res["bend_start_along"] = Math.Round(bendStart, 9); res["bend_length"] = Math.Round(bendEnd - bendStart, 9); res["faces_swapped"] = usedSwap; if (attempts.Count > 0) res["attempts"] = attempts;
                if (!(after.Value<int?>("faces") > before.Value<int?>("faces"))) problems.Add("the gusset added no face");
                if (kind == "flat" && !(vol1 > vol0)) problems.Add("a flat gusset must add material");
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }

        // the range [min, max] the corners of an axis-aligned box cover along the unit vector u
        private static double[] BoxRange(double[] box, double[] u)
        {
            double mn = double.MaxValue, mx = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new[] { (i & 1) == 0 ? box[0] : box[3], (i & 2) == 0 ? box[1] : box[4], (i & 4) == 0 ? box[2] : box[5] };
                double d = Dot(corner, u); if (d < mn) mn = d; if (d > mx) mx = d;
            }
            return new[] { mn, mx };
        }

        private static double Dist3(double[] a, double[] b) { double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2]; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    }
}
