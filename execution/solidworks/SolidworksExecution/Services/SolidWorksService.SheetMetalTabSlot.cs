// sheet_metal_tab_slot: the TAB AND SLOT feature - tabs on the edge of one body and matching slots in a face of another - through CreateDefinition(swFmTabAndSlot) -> ITabAndSlotFeatureData ->
// SelectionAddNewGroup() -> ITabAndSlotGroupData -> CreateFeature (probes 77): there is no Insert call. What it took to make CreateFeature return something instead of null (the properties were all accepted
// from the first try): TabFace must be the face of the TAB body that TOUCHES the slot face (coplanar with it, facing the other way) and the tab edge one of its edges - not the tab body's side face. What kills
// SolidWorks: AccessSelections on a NEW definition, start / end reference points set to the edge's vertices, and a second CreateFeature on the same edge right after the first. Measured: the tabs are spread
// EQUALLY between a start offset and an end offset (the first tab starts D1 from the edge's START vertex, the last ends D2 from its END vertex; one tab sits at the start), their number is
// SpacingNumberOfInstances (SpacingType / Spacing, the 'spacing length' option, changed nothing), a slot is the tab plus the clearance at BOTH ends along the edge and, across the thickness, the tab plus a fixed 0.2 mm (0.1 mm a side, whatever slot_clearance says; slot_clearance 0 cuts slots that match no tab end), the tab height is blind (the two other height
// types return null), TabEdgesType 0 / 1 / 2 = none / fillet / chamfer. The feature makes two features: "Tab and Slot-Tab<n>" on the tab body and "Tab and Slot-Slot<n>" on the slot body. Lengths METERS.
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
        private static string BodyName(IBody2 b) { try { return b.Name; } catch (Exception) { return null; } }
        private static double BodyVolume(IBody2 b) { var mp = b.GetMassProperties(1.0) as double[]; return mp != null && mp.Length >= 4 ? mp[3] : 0.0; }

        // one value of a parameter as the one-element array the resolvers take: an index, or a point given as an array or as a JSON string
        private static JToken OneOf(JToken tok)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (tok.Type == JTokenType.String && ((string)tok).TrimStart().StartsWith("[", StringComparison.Ordinal)) tok = JToken.Parse((string)tok);
            return new JArray(tok);
        }

        public ExecutionResponse SheetMetalTabSlot(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_tab_slot needs an active part.");
                var edges = ResolveEdges(doc, OneOf(p["edge"]), OneOf(p["edge_point"]));
                if (edges.Count != 1) throw new ToolFailure("MISSING_PARAMETER", "edge (an index from analyze_model(edges)) or edge_point ([x,y,z] meters, ON the edge): exactly ONE tab edge - an edge of the tab body's face that touches the slot face.");
                var slotFaces = ResolveFaces(doc, OneOf(p["slot_face"]), OneOf(p["slot_face_point"]));
                if (slotFaces.Count != 1) throw new ToolFailure("MISSING_PARAMETER", "slot_face (an index from analyze_model(faces)) or slot_face_point ([x,y,z] meters, ON the face): the face of the OTHER body in which the slots are cut.");
                var edge = edges[0]; var slotFace = slotFaces[0];
                double tabLen = p.Value<double?>("tab_length") ?? 0.0; if (tabLen <= 0) throw new ToolFailure("MISSING_PARAMETER", "tab_length (meters, > 0): the length of each tab along the edge.");
                int count = p.Value<int?>("count") ?? 3; if (count < 1 || count > 100) throw new ToolFailure("INVALID_PARAMETER", "count must lie between 1 and 100 (the number of tabs).");
                double d1 = p.Value<double?>("start_offset") ?? 0.0, d2 = p.Value<double?>("end_offset") ?? 0.0, clearance = p.Value<double?>("slot_clearance") ?? 0.0001;
                if (d1 < 0 || d2 < 0) throw new ToolFailure("INVALID_PARAMETER", "start_offset and end_offset are lengths in METERS and cannot be negative.");
                if (clearance < 1e-5 || clearance > 0.01) throw new ToolFailure("INVALID_PARAMETER", "slot_clearance is in METERS, between 0.01 mm and 10 mm (default 0.1 mm): with 0 SolidWorks cuts slots that match no tab end.");
                string treat = (p.Value<string>("edge_treatment") ?? "none").Trim().ToLowerInvariant(); double treatSize = p.Value<double?>("edge_treatment_size") ?? 0.0;
                if (treat != "none" && treat != "fillet" && treat != "chamfer") throw new ToolFailure("INVALID_PARAMETER", "edge_treatment must be 'none' (default), 'fillet' or 'chamfer' (the ends of the tabs), with edge_treatment_size in meters.");
                if (treat != "none" && treatSize <= 0) throw new ToolFailure("MISSING_PARAMETER", "edge_treatment_size (meters, > 0) is required with a fillet or chamfer.");

                // geometry read BEFORE the feature (the objects go stale once it exists)
                var sv = ((IVertex)edge.GetStartVertex()).GetPoint() as double[]; var ev = ((IVertex)edge.GetEndVertex()).GetPoint() as double[];
                if (sv == null || ev == null) throw new ToolFailure("EDGE_NOT_FOUND", "The tab edge has no end vertices (a closed curve?): it must be a straight edge.");
                double edgeLen = Math.Sqrt(Dot(V(ev, sv), V(ev, sv))); if (edgeLen < 1e-6) throw new ToolFailure("EDGE_NOT_FOUND", "The tab edge has no length.");
                var dir = Unit(V(ev, sv));      // from the start vertex toward the end vertex
                var slotSurf = slotFace.GetSurface() as ISurface; if (slotSurf == null || !slotSurf.IsPlane()) throw new ToolFailure("INVALID_PARAMETER", "slot_face must be a planar face.");
                var ns = Unit(slotFace.Normal as double[]); var ps = PointOnFace(slotFace); var slotBody = slotFace.GetBody() as IBody2;
                IFace2 tabFace = null;
                if (p["tab_face"] != null || p["tab_face_point"] != null)
                {
                    var tf = ResolveFaces(doc, OneOf(p["tab_face"]), OneOf(p["tab_face_point"]));
                    if (tf.Count != 1) throw new ToolFailure("MISSING_PARAMETER", "tab_face / tab_face_point must name exactly one face.");
                    tabFace = tf[0];
                }
                else
                {
                    var adj = edge.GetTwoAdjacentFaces2() as object[];
                    if (adj != null)
                        foreach (var o in adj)
                        {
                            var f = o as IFace2; var s = f == null ? null : f.GetSurface() as ISurface; if (s == null || !s.IsPlane()) continue;
                            var nf = Unit(f.Normal as double[]); var pf = PointOnFace(f);
                            if (Dot(nf, ns) < -0.999999 && Math.Abs(Dot(V(pf, ps), ns)) < 1e-6) { tabFace = f; break; }          // coplanar with the slot face, facing the other way: the face that touches it
                        }
                    if (tabFace == null) throw new ToolFailure("TAB_FACE_NOT_FOUND", "None of the two faces beside the tab edge touches the slot face (coplanar with it and facing the other way). The tab edge must be an edge of the tab body's face that lies ON the slot face - or pass tab_face / tab_face_point.");
                }
                var tabBody = tabFace.GetBody() as IBody2;
                if (tabBody == null || slotBody == null) throw new ToolFailure("INVALID_PARAMETER", "The tab face and the slot face must belong to solid bodies.");
                string tabBodyName = BodyName(tabBody), slotBodyName = BodyName(slotBody);
                if (tabBodyName != null && tabBodyName == slotBodyName) throw new ToolFailure("INVALID_PARAMETER", "The tab face and the slot face belong to the same body: tabs and slots join TWO bodies of the part (a multi-body part).");
                var tfBox = tabFace.GetBox() as double[]; double thin = 0; if (tfBox != null) { var ext = new[] { tfBox[3] - tfBox[0], tfBox[4] - tfBox[1], tfBox[5] - tfBox[2] }.Where(e => e > 1e-7).OrderBy(e => e).ToArray(); if (ext.Length > 0) thin = ext[0]; }
                double thickness = p.Value<double?>("tab_thickness") ?? thin; if (thickness <= 0) throw new ToolFailure("MISSING_PARAMETER", "tab_thickness (meters, > 0): the thickness of each tab.");
                double height = p.Value<double?>("tab_height") ?? 0.0;
                if (height <= 0)       // default: through the slot body = its extent along the slot face's normal (exact for an axis-aligned plate)
                {
                    var bb = slotBody.GetBodyBox() as double[]; int ax = Math.Abs(ns[0]) > 0.999999 ? 0 : Math.Abs(ns[1]) > 0.999999 ? 1 : Math.Abs(ns[2]) > 0.999999 ? 2 : -1;
                    if (ax < 0 || bb == null) throw new ToolFailure("MISSING_PARAMETER", "tab_height (meters, > 0): the slot face is not along an axis, so the depth of the slot body is not known.");
                    height = bb[3 + ax] - bb[ax];
                }
                double usable = edgeLen - d1 - d2; if (usable < tabLen - 1e-9) throw new ToolFailure("INVALID_PARAMETER", "A tab of " + Mmf(tabLen) + " mm does not fit between the offsets (" + Mmf(d1) + " + " + Mmf(d2) + " mm) on an edge of " + Mmf(edgeLen) + " mm.");
                if (count > 1 && (usable - tabLen) / (count - 1) < tabLen + 2 * clearance - 1e-9) throw new ToolFailure("INVALID_PARAMETER", count + " tabs of " + Mmf(tabLen) + " mm with a clearance of " + Mmf(clearance) + " mm on each side do not fit in " + Mmf(usable) + " mm: the slots would run into each other.");

                var tabBox0 = BoxOf(tabBody); var slotBox0 = BoxOf(slotBody);       // the bodies are found again by their boxes (the names change with the features); a body only grows by tabs and keeps its box with slots
                var names0 = TopNames(doc); var before = new JObject { ["tab_body"] = new JObject { ["faces"] = tabBody.GetFaceCount(), ["volume"] = BodyVolume(tabBody) }, ["slot_body"] = new JObject { ["faces"] = slotBody.GetFaceCount(), ["volume"] = BodyVolume(slotBody) } };
                doc.ClearSelection2(true);
                // the definition, in the order that was proven (probe 77)
                var def = doc.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmTabAndSlot); var fd = def as ITabAndSlotFeatureData;
                if (fd == null) throw new ToolFailure("TAB_SLOT_FAILED", "CreateDefinition(swFmTabAndSlot) returned nothing.");
                var g = fd.SelectionAddNewGroup() as ITabAndSlotGroupData; if (g == null) throw new ToolFailure("TAB_SLOT_FAILED", "SelectionAddNewGroup returned nothing.");
                g.SelectionTabEdge = edge; g.SelectionSlotFace = slotFace; g.TabFace = tabFace;
                g.TabLength = tabLen; g.TabThickness = thickness; g.TabHeightType = 0; g.TabHeightValue = height; g.TabOffsetValue = 0.0;
                g.SpacingType = 0; g.SpacingNumberOfInstances = count; g.Spacing = 0.02;
                g.Offset = true; g.D1OffsetFromStart = d1; g.D2OffsetFromEnd = d2; g.SlotClearance = clearance;
                g.TabEdgesType = treat == "fillet" ? 1 : treat == "chamfer" ? 2 : 0; if (treat == "fillet") g.TabFilletEdgeTreatmentValue = treatSize; if (treat == "chamfer") g.TabChamferEdgeTreatmentValue = treatSize;
                g.TabReverseDirection = false;
                var feat = doc.FeatureManager.CreateFeature(def) as IFeature; doc.ClearSelection2(true);
                if (feat == null)
                    throw new ToolFailure("TAB_SLOT_FAILED", "SolidWorks built no tab and slot. Check: the tab edge is an edge of the tab body's face that lies ON the slot face; the slot face is a face of another body; the tabs fit the edge and the slot body; the part has two bodies.");
                doc.EditRebuild3();
                var made = TopFeatures(doc).Where(f => !names0.Contains(f.Name) && f.GetTypeName2() == "TabAndSlotFeature").ToList(); var problems = new JArray();
                foreach (var f in made) { int err = FeatureError(f); if (err != 0) problems.Add("feature " + f.Name + " reports rebuild error " + err); }
                if (made.Count != 2) problems.Add(made.Count + " tab and slot features were made (a tab feature and a slot feature = 2 expected)");

                // read back by measurement: the bodies again (by name), the tabs along the edge
                IBody2 tabNow = null, slotNow = null; int tabHits = 0, slotHits = 0;
                Func<double[], double[], bool> holds = (outer, inner) => outer != null && inner != null && outer[0] <= inner[0] + 1e-6 && outer[1] <= inner[1] + 1e-6 && outer[2] <= inner[2] + 1e-6 && outer[3] >= inner[3] - 1e-6 && outer[4] >= inner[4] - 1e-6 && outer[5] >= inner[5] - 1e-6;
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bodies != null) foreach (var o in bodies) { var b = (IBody2)o; var bx = BoxOf(b); if (holds(bx, tabBox0)) { tabNow = b; tabHits++; } if (holds(bx, slotBox0)) { slotNow = b; slotHits++; } }
                if (tabHits != 1 || slotHits != 1) { tabNow = null; slotNow = null; }
                var res = new JObject { ["feature"] = feat.Name, ["features"] = new JArray(made.Select(f => (object)f.Name).ToArray()), ["tab_body"] = tabBodyName, ["slot_body"] = slotBodyName, ["count"] = count, ["tab_length"] = tabLen, ["tab_thickness"] = thickness, ["tab_height"] = height, ["start_offset"] = d1, ["end_offset"] = d2, ["slot_clearance"] = clearance, ["edge_treatment"] = treat, ["edge_length"] = edgeLen, ["edge_start"] = new JArray(sv), ["edge_end"] = new JArray(ev), ["before"] = before };
                double pitch = count > 1 ? (usable - tabLen) / (count - 1) : 0.0; var tabs = new JArray(); var want = new List<double>(); var wantSlots = new List<double>();
                for (int i = 0; i < count; i++)
                {
                    double a = d1 + i * pitch, b = a + tabLen; tabs.Add(new JObject { ["from"] = Math.Round(a, 9), ["to"] = Math.Round(b, 9) });
                    want.Add(Dot(sv, dir) + a); want.Add(Dot(sv, dir) + b); wantSlots.Add(Dot(sv, dir) + a - clearance); wantSlots.Add(Dot(sv, dir) + b + clearance);
                }
                res["tabs"] = tabs;       // distances along the edge from its START vertex
                Func<IBody2, List<double>> across = body =>      // the coordinates (along the edge) of the planar faces whose normal runs along the edge
                {
                    var l = new List<double>(); var fs = body == null ? null : body.GetFaces() as object[];
                    if (fs != null) foreach (var o in fs) { var f = (IFace2)o; var s = f.GetSurface() as ISurface; if (s == null || !s.IsPlane()) continue; var nf = Unit(f.Normal as double[]); if (Math.Abs(Dot(nf, dir)) < 0.999999) continue; l.Add(Dot(PointOnFace(f), dir)); }
                    return l;
                };
                var tabCoords = across(tabNow); var slotCoords = across(slotNow);
                foreach (double w in want) if (!tabCoords.Any(c => Math.Abs(c - w) < 2e-6)) problems.Add("no tab face at " + Mmf(w - Dot(sv, dir)) + " mm from the edge's start");
                foreach (double w in wantSlots) if (!slotCoords.Any(c => Math.Abs(c - w) < 2e-6)) problems.Add("no slot face at " + Mmf(w - Dot(sv, dir)) + " mm from the edge's start");
                if (tabNow != null && slotNow != null)
                {
                    res["after"] = new JObject { ["tab_body"] = new JObject { ["faces"] = tabNow.GetFaceCount(), ["volume"] = BodyVolume(tabNow) }, ["slot_body"] = new JObject { ["faces"] = slotNow.GetFaceCount(), ["volume"] = BodyVolume(slotNow) } };
                    double added = BodyVolume(tabNow) - before["tab_body"].Value<double>("volume"), removed = before["slot_body"].Value<double>("volume") - BodyVolume(slotNow), prism = count * tabLen * thickness * height;
                    res["tab_volume_added"] = added; res["slot_volume_removed"] = removed;
                    if (treat == "none" && Math.Abs(added - prism) > Math.Max(1e-13, prism * 1e-3)) problems.Add("the tabs add " + (added * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3, count x length x thickness x height = " + (prism * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3");
                    double slotDepth = 0; { var sb0 = slotBox0; int ax = Math.Abs(ns[0]) > 0.999999 ? 0 : Math.Abs(ns[1]) > 0.999999 ? 1 : Math.Abs(ns[2]) > 0.999999 ? 2 : -1; if (ax >= 0) slotDepth = sb0[3 + ax] - sb0[ax]; }
                    if (slotDepth > 0 && height >= slotDepth - 1e-9)       // a through slot: what it takes out is count x (length + 2 clearance) x (thickness + the clearance across) x depth; the clearance ACROSS the thickness is measured, not set
                        res["slot_clearance_across_thickness"] = Math.Round(removed / (count * (tabLen + 2 * clearance) * slotDepth) - thickness, 9);
                    if (removed < prism * (1 - 1e-3)) problems.Add("the slots remove " + (removed * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3, less than the tabs that fill them (" + (prism * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3)");
                }
                else problems.Add("the two bodies could not be told apart again after the feature (by their boxes)");
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }
    }
}
