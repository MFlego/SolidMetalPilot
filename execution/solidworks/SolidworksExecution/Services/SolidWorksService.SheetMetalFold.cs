// Unfold and Fold of sheet-metal bends through the typed API: IModelDoc2.InsertSheetMetalUnfold / InsertSheetMetalFold read the selection the PropertyManager would have collected - the FIXED face (mark 1)
// and the bends to flatten / fold (their OneBend sub-features, mark 2). Proven on the user's reference cabinet (probes 08, 12, 16: Unfold -> multi-radius fillet -> Fold gave back the same body).
// The fixed face is identified by a POINT ON it (meters) or its index from analyze_model(faces), and selected as an object: SelectByID2 would pick through the current view. Bends default to ALL the bends
// of the part. Lengths are METERS.
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
        // every bend of the part: the OneBend sub-features of any top-level feature (Convert's SharpBend1.., a flange's EdgeBend.., a mitre flange's bends ...), in tree order
        private static List<IFeature> OneBendFeatures(IModelDoc2 doc)
        {
            var l = new List<IFeature>();
            foreach (var f in TopFeatures(doc))
            {
                var s = f.GetFirstSubFeature() as IFeature; int guard = 0;
                while (s != null && guard++ < 2000) { if (s.GetTypeName2() == "OneBend") l.Add(s); s = s.GetNextSubFeature() as IFeature; }
            }
            return l;
        }

        // the planar bounding sizes of the (single) solid body: x, y, z extents in meters; null without a solid body
        private static double[] SolidSize(IPartDoc part)
        {
            var bs = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs == null || bs.Length == 0) return null;
            var b = BoxOf((IBody2)bs[0]); return b == null ? null : new[] { b[3] - b[0], b[4] - b[1], b[5] - b[2] };
        }

        // sheet_metal_fold: action = unfold | fold
        public ExecutionResponse SheetMetalFold(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_fold needs an active part.");
                string action = (p.Value<string>("action") ?? "").ToLowerInvariant();
                if (action != "unfold" && action != "fold") throw new ToolFailure("INVALID_PARAMETER", "action must be 'unfold' (flatten the bends) or 'fold' (bend them back).");
                if (FirstOfType(doc, "SheetMetal") == null) throw new ToolFailure("NOT_SHEET_METAL", "The active part has no Sheet-Metal feature (convert or build the sheet metal first).");

                // the fixed face: the face that stays put - a point ON it, or an index from analyze_model(faces)
                var ftok = p["fixed_face"]; if (ftok == null) throw new ToolFailure("MISSING_PARAMETER", "fixed_face is required: [x, y, z] (meters, a point ON the face that stays put) or its index from analyze_model(faces).");
                if (ftok.Type == JTokenType.String) { var s = ((string)ftok).Trim(); int idx; if (s.StartsWith("[")) ftok = AsArray(ftok, "fixed_face"); else if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) ftok = new JValue(idx); }
                IFace2 fixedFace;
                if (ftok.Type == JTokenType.Integer)
                {
                    var all = FlattenFaces(part); int i = ftok.Value<int>();
                    if (i < 0 || i >= all.Count) throw new ToolFailure("FACE_NOT_FOUND", "Face index " + i + " out of range (part has " + all.Count + " faces). Call analyze_model(faces).");
                    fixedFace = all[i];
                }
                else
                {
                    var pt = ToPoint(ftok, "fixed_face"); fixedFace = FaceAtPoint(doc, pt);
                    if (fixedFace == null) throw new ToolFailure("FACE_NOT_FOUND", "No face of the solid body within 0.1 mm of (" + string.Join(", ", pt.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)).ToArray()) + ") m.");
                }
                var fbox = fixedFace.GetBox() as double[];

                // the bends: all of them (default) or the named OneBend sub-features
                var allBends = OneBendFeatures(doc); if (allBends.Count == 0) throw new ToolFailure("NO_BENDS", "The part has no bends (no OneBend sub-feature).");
                List<IFeature> bends;
                var btok = p["bends"]; string bstr = btok != null && btok.Type == JTokenType.String ? ((string)btok).Trim() : null;
                if (btok == null || (bstr != null && (bstr.Length == 0 || bstr.Equals("all", StringComparison.OrdinalIgnoreCase)))) bends = allBends;
                else
                {
                    var names = AsArray(btok, "bends"); bends = new List<IFeature>();
                    foreach (var nt in names)
                    {
                        string nm = (string)nt; var hit = allBends.FirstOrDefault(b => b.Name == nm);
                        if (hit == null) throw new ToolFailure("BEND_NOT_FOUND", "No bend named '" + nm + "'. The part's bends: " + string.Join(", ", allBends.Select(b => b.Name).Take(40).ToArray()) + ".");
                        bends.Add(hit);
                    }
                    if (bends.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "bends is empty: pass 'all' or a list of bend names.");
                }

                var before = SolidSize(part); var names0 = TopNames(doc); string type = action == "unfold" ? "UnFold" : "Fold";
                // selection exactly as the command reads it: fixed face = mark 1, bends = mark 2
                doc.ClearSelection2(true);
                var sd = ((ISelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = 1;
                if (!((IEntity)fixedFace).Select4(false, sd)) throw new ToolFailure("SELECTION_FAILED", "Could not select the fixed face.");
                int picked = 0; foreach (var b in bends) if (b.Select2(true, 2)) picked++;
                if (picked != bends.Count) { doc.ClearSelection2(true); throw new ToolFailure("SELECTION_FAILED", "Only " + picked + " of " + bends.Count + " bends could be selected."); }
                // InsertSheetMetalUnfold / Fold live on the OLD document interface: late-bound through IDispatch (like InsertDeleteFace)
                try { ((object)doc).GetType().InvokeMember(action == "unfold" ? "InsertSheetMetalUnfold" : "InsertSheetMetalFold", BindingFlags.InvokeMethod, null, doc, null); }
                catch (TargetInvocationException ex) { doc.ClearSelection2(true); throw new ToolFailure("FOLD_FAILED", (action == "unfold" ? "InsertSheetMetalUnfold" : "InsertSheetMetalFold") + " raised " + (ex.InnerException == null ? ex.Message : ex.InnerException.Message)); }
                doc.ClearSelection2(true); doc.EditRebuild3();
                var created = TopFeatures(doc).FirstOrDefault(f => !names0.Contains(f.Name) && f.GetTypeName2() == type);
                if (created == null) throw new ToolFailure("FOLD_FAILED", "SolidWorks built no " + type + " feature (the fixed face must belong to the sheet-metal body and the bends must be in the opposite state: folded to unfold, unfolded to fold).");

                var problems = new JArray(); int err = FeatureError(created);
                if (err != 0) problems.Add("the " + type + " feature reports rebuild error " + err);
                // read the feature back: the fixed face it kept and the number of bends it holds
                var res = new JObject { ["feature"] = created.Name, ["action"] = action, ["bends_selected"] = bends.Count, ["bends_in_part"] = allBends.Count };
                var def = created.GetDefinition() as IFoldsFeatureData;
                if (def != null)
                {
                    bool acc = false; try { acc = def.AccessSelections(doc, null); } catch (COMException) { }
                    try
                    {
                        var held = def.Bends as object[]; res["bends_in_feature"] = held == null ? 0 : held.Length;
                        if (held == null || held.Length != bends.Count) problems.Add("the feature holds " + (held == null ? 0 : held.Length) + " bends, not " + bends.Count);
                        var kept = def.FixedFace as IFace2; var kb = kept == null ? null : kept.GetBox() as double[];
                        if (kb != null && fbox != null) { double dev = 0; for (int i = 0; i < 6; i++) dev = Math.Max(dev, Math.Abs(kb[i] - fbox[i])); res["fixed_face_deviation"] = Math.Round(dev, 9); if (dev > 1e-6) problems.Add("the feature's fixed face is not the one asked for (its box differs by " + dev.ToString("0.######", CultureInfo.InvariantCulture) + " m)"); }
                    }
                    finally { if (acc) { try { def.ReleaseSelectionAccess(); } catch (COMException) { } } }
                }
                var after = SolidSize(part);
                if (before != null) res["size_before"] = new JArray(before.Select(v => (object)Math.Round(v, 9)).ToArray());
                if (after != null) res["size_after"] = new JArray(after.Select(v => (object)Math.Round(v, 9)).ToArray());
                // when every bend is unfolded the body is flat: its smallest extent is the sheet thickness. The extent comes from the body's bounding box, which is conservative by a few microns wherever
                // bend surfaces reach the outside (1.608 for a 1.6 mm sheet, measured): anything within 5 % of the thickness (and at least 0.02 mm) counts as flat - a folded wall is millimetres off.
                if (action == "unfold" && bends.Count == allBends.Count && after != null)
                {
                    double thin = after.Min(); var sm = FirstOfType(doc, "SheetMetal"); var smd = sm == null ? null : sm.GetDefinition() as ISheetMetalFeatureData; double t = 0;
                    if (smd != null) { bool a2 = false; try { a2 = smd.AccessSelections(doc, null); t = smd.Thickness; } finally { if (a2) smd.ReleaseSelectionAccess(); } }
                    double tol = Math.Max(2e-5, 0.05 * t); bool isFlat = t > 0 && Math.Abs(thin - t) < tol;
                    res["flat"] = isFlat; if (t > 0 && !isFlat) problems.Add("not flat: the thinnest extent is " + (thin * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm, the sheet is " + (t * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm");
                }
                if (problems.Count > 0) res["problems"] = problems;
                res["feature_error"] = err; res["verified"] = err == 0 && problems.Count == 0;
                return res;
            });
        }
    }
}
