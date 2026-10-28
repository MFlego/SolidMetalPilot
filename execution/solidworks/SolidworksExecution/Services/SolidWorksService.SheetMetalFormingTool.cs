// sheet_metal_forming_tool: FORMING TOOLS - the .sldftp files of the design library (louvers, lances, embosses, ribs, extruded flanges) - placed on a face of a sheet-metal part through
// IFeatureManager.InsertFormToolFeature(Path, Flip, RotAngle, Config, OverrideDoc, ShowPunch, ShowProfile, ShowCenter, LinkToPart) (probes 86 / 87: all 17 tools of the library place with error 0). What the probes measured:
//   - the face is selected as an OBJECT with a SelectData whose X / Y / Z are the pick point: that point is the PLACEMENT (without it SolidWorks puts the tool somewhere else), and nothing depends on the view
//     (SelectByID2 through the current view gives the same result, a few microns off);
//   - the tool pushes the sheet AWAY from the selected face: the form stands on the far side of the sheet (pick the other face of the plate to form the other way);
//   - the rotation turns the tool about the pick point, counter-clockwise looking at the face from outside (the right-hand rotation about the face's outward normal);
//   - Flip changes NOTHING (symmetric and asymmetric tools alike), so it is not offered; an unknown configuration is silently replaced by 'Default';
//   - SolidWorks accepts what it should refuse: a tool whose footprint sticks out past the sheet's edge EXTENDS the sheet (a louver at a corner grew the plate from 100 x 60 to 102 x 76.7 mm, error 0) and a tool
//     placed on a THICKNESS face (the edge of the sheet) builds a mess with error 0.
// This tool refuses a curved or thickness face before it places anything, and takes the feature out again (the part is as it was) when the result is not what was asked: a rebuild error, another configuration
// than the one asked, nothing changed, the sheet extended. The definition (ILibraryFormToolFeatureData) reads back the path, the configuration, the rotation, the PickPoint and the PlacementFace.
// Lengths METERS, angles DEGREES at this boundary.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        // ---- the library ----------------------------------------------------------------------------------------------------------------------------------------------------------------
        // the folders that hold forming tools: the palette location of the options (empty by default), '<design library>\forming tools', and the install's own ProgramData copy
        private List<string> FormingToolFolders()
        {
            var l = new List<string>();
            Action<string> add = d =>
            {
                if (string.IsNullOrWhiteSpace(d)) return;
                try { d = Path.GetFullPath(d.Trim()); } catch (Exception) { return; }
                if (Directory.Exists(d) && !l.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) l.Add(d);
            };
            try { foreach (var s in (_solidWorks.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsPaletteFormTools) ?? "").Split(';')) add(s); } catch (Exception) { }
            try { foreach (var s in (_solidWorks.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDesignLibrary) ?? "").Split(';')) if (!string.IsNullOrWhiteSpace(s)) add(Path.Combine(s.Trim(), "forming tools")); } catch (Exception) { }
            try
            {
                int major = int.Parse(_solidWorks.RevisionNumber().Split('.')[0], CultureInfo.InvariantCulture);          // 34 = SOLIDWORKS 2026
                add(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "SolidWorks", "SOLIDWORKS " + (1992 + major), "design library", "forming tools"));
            }
            catch (Exception) { }
            return l;
        }

        // a full path, a path relative to a forming tools folder ('louvers/louver'), or a bare name ('louver') that is unique in the library; the .sldftp extension is optional
        private string ResolveFormingTool(string tool)
        {
            string t = tool.Trim().Trim('"');
            if (t.IndexOfAny(new[] { '*', '?' }) >= 0 || t.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new ToolFailure("INVALID_PARAMETER", "tool must be a file name or path, without wildcards.");
            string withExt = t.EndsWith(".sldftp", StringComparison.OrdinalIgnoreCase) ? t : t + ".sldftp";
            if (Path.IsPathRooted(withExt))
            {
                if (File.Exists(withExt)) return Path.GetFullPath(withExt);
                throw new ToolFailure("FORMING_TOOL_NOT_FOUND", "There is no forming tool file '" + withExt + "'. action 'list' shows the tools of the library.");
            }
            var folders = FormingToolFolders(); string rel = withExt.Replace('/', '\\').TrimStart('\\');
            foreach (var f in folders) { string c = Path.Combine(f, rel); if (File.Exists(c)) return c; }
            if (rel.IndexOf('\\') < 0)
            {
                var hits = new List<string>();
                foreach (var f in folders) hits.AddRange(Directory.EnumerateFiles(f, rel, SearchOption.AllDirectories));
                hits = hits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (hits.Count == 1) return hits[0];
                if (hits.Count > 1) throw new ToolFailure("INVALID_PARAMETER", "'" + tool + "' is ambiguous: " + string.Join("; ", hits.ToArray()) + ". Give the path relative to the forming tools folder, e.g. 'louvers/louver'.");
            }
            string stem = Path.GetFileNameWithoutExtension(rel); var near = new List<string>();
            foreach (var f in folders) near.AddRange(Directory.EnumerateFiles(f, "*" + stem + "*.sldftp", SearchOption.AllDirectories).Select(x => x.Substring(f.Length).TrimStart('\\').Replace('\\', '/')));
            throw new ToolFailure("FORMING_TOOL_NOT_FOUND", "No forming tool '" + tool + "' " + (folders.Count == 0 ? "- no forming tools folder was found: give the full path of the .sldftp." : "under " + string.Join("; ", folders.ToArray()) + ".")
                + (near.Count > 0 ? " Similar: " + string.Join(", ", near.Take(8).ToArray()) + "." : "") + " action 'list' shows the tools of the library.");
        }

        private JObject ListFormingTools(JObject p)
        {
            var folders = FormingToolFolders(); var tools = new JArray(); string filter = (p.Value<string>("filter_text") ?? "").Trim();
            foreach (var f in folders)
                foreach (var file in Directory.EnumerateFiles(f, "*.sldftp", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = file.Substring(f.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (filter.Length > 0 && rel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    tools.Add(new JObject { ["name"] = Path.GetFileNameWithoutExtension(file), ["category"] = Path.GetDirectoryName(rel.Replace('/', '\\')) ?? "", ["relative_path"] = rel, ["path"] = file });
                }
            return new JObject { ["folders"] = new JArray(folders.Select(x => (object)x).ToArray()), ["count"] = tools.Count, ["tools"] = tools, ["verified"] = true };
        }

        // ---- measuring ------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static bool MassState(IModelDoc2 doc, out double volume, out double[] centre)
        {
            volume = double.NaN; centre = null;
            var mp = doc.Extension.CreateMassProperty() as IMassProperty; if (mp == null) return false;
            mp.UseSystemUnits = true; volume = mp.Volume; centre = mp.CenterOfMass as double[]; return centre != null && centre.Length >= 3;
        }

        // the box of all solid bodies together
        private static double[] SolidsBox(IPartDoc part)
        {
            double[] u = null; var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bodies == null) return null;
            foreach (var o in bodies)
            {
                var b = BoxOf((IBody2)o); if (b == null) continue;
                if (u == null) u = (double[])b.Clone();
                else for (int i = 0; i < 3; i++) { u[i] = Math.Min(u[i], b[i]); u[i + 3] = Math.Max(u[i + 3], b[i + 3]); }
            }
            return u;
        }

        // the length of the outer loop of a face (0 when it cannot be read)
        private static double OuterLoopLength(IFace2 f)
        {
            try
            {
                var lp = f.GetFirstLoop() as ILoop2;
                while (lp != null)
                {
                    if (lp.IsOuter())
                    {
                        var edges = lp.GetEdges() as object[]; if (edges == null) return 0; double sum = 0;
                        foreach (var o in edges)
                        {
                            var e = (IEdge)o; var cp = e.GetCurveParams2() as double[]; var c = e.GetCurve() as ICurve;
                            if (cp == null || cp.Length < 8 || c == null) return 0;
                            sum += c.GetLength3(cp[6], cp[7]);
                        }
                        return sum;
                    }
                    lp = lp.GetNext() as ILoop2;
                }
            }
            catch (COMException) { }
            return 0;
        }

        // ---- sheet_metal_forming_tool -----------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse SheetMetalFormingTool(ToolRequest request)
        {
            string action = ((request.Params as JObject)?.Value<string>("action") ?? "place").Trim().ToLowerInvariant();
            return Typed(request, action == "place", (doc, p) =>
            {
                if (action == "list") return ListFormingTools(p);
                if (action != "place") throw new ToolFailure("INVALID_PARAMETER", "action must be 'place' (default) or 'list' (the forming tools of the design library).");
                RequireSheetMetalPart(doc, "sheet_metal_forming_tool");
                var part = (IPartDoc)doc;
                string toolArg = p.Value<string>("tool"); if (string.IsNullOrWhiteSpace(toolArg)) throw new ToolFailure("MISSING_PARAMETER", "tool: the forming tool - a bare name ('louver'), a path relative to the forming tools folder ('louvers/louver') or the full path of a .sldftp (action 'list' shows the library).");
                string path = ResolveFormingTool(toolArg);
                double rotDeg = p.Value<double?>("rotation") ?? 0.0; if (double.IsNaN(rotDeg) || double.IsInfinity(rotDeg) || Math.Abs(rotDeg) > 3600) throw new ToolFailure("INVALID_PARAMETER", "rotation is in DEGREES (counter-clockwise looking at the face from outside).");
                double rot = rotDeg * Math.PI / 180; string cfg = (p.Value<string>("configuration") ?? "").Trim();
                if (p["flip"] != null && p.Value<bool?>("flip") == true) throw new ToolFailure("INVALID_PARAMETER", "flip is not offered: SolidWorks ignores it for every forming tool. The tool forms the sheet away from the face it is placed on - place it on the other face of the sheet to form the other way.");

                // the face and the pick point (the placement of the tool)
                IFace2 face = null; double[] pick = null;
                if (p["face"] != null && p["face"].Type != JTokenType.Null)
                {
                    var all = FlattenFaces(part); int fi = p["face"].Value<int>();
                    if (fi < 0 || fi >= all.Count) throw new ToolFailure("FACE_NOT_FOUND", "Face index " + fi + " out of range (part has " + all.Count + " faces). Call analyze_model(faces).");
                    face = all[fi];
                }
                JToken pt = p["point"]; if (pt != null && pt.Type == JTokenType.String) { try { pt = JToken.Parse((string)pt); } catch (Exception) { throw new ToolFailure("INVALID_PARAMETER", "point is a string but not a JSON array [x, y, z]."); } }
                if (pt != null && pt.Type != JTokenType.Null) pick = ToPoint(pt, "point");
                if (face == null && pick == null) throw new ToolFailure("MISSING_PARAMETER", "point ([x, y, z] meters, ON the face - the placement of the tool) and / or face (an index from analyze_model(faces); alone, the tool goes to a point of that face).");
                if (face == null)
                {
                    face = FaceAtPoint(doc, pick);
                    if (face == null) throw new ToolFailure("FACE_NOT_FOUND", "No face within 0.1 mm of " + P3(pick) + ": point must lie ON the face the tool is placed on.");
                }
                else if (pick == null) pick = PointOnFace(face);
                else
                {
                    var q = face.GetClosestPointOn(pick[0], pick[1], pick[2]) as double[];
                    if (q == null || Math.Sqrt(Dot(V(q, pick), V(q, pick))) > PickTolerance) throw new ToolFailure("INVALID_PARAMETER", "point " + P3(pick) + " is not on the face that was given.");
                }
                var surf = face.GetSurface() as ISurface;
                if (surf == null || !surf.IsPlane()) throw new ToolFailure("INVALID_PARAMETER", "A forming tool is placed on a FLAT face of the sheet; that face is not planar (a bend, for one).");
                double thickness = SheetThickness(doc); double area = face.GetArea(), per = OuterLoopLength(face);
                if (per > 0)       // the narrowest side of the rectangle that has this area and this outer length: the thickness of the sheet for an edge face
                {
                    double s = per / 2, disc = s * s - 4 * area, minSide = disc >= 0 ? (s - Math.Sqrt(disc)) / 2 : s / 2;
                    if (minSide <= thickness * 1.02 + 1e-7) throw new ToolFailure("INVALID_PARAMETER", "That face is a THICKNESS face (the edge of the sheet, " + Mmf(minSide) + " mm wide): a forming tool goes on one of the two flat faces of the sheet.");
                }
                var normal = Unit(face.Normal as double[]); int axis = Math.Abs(normal[0]) > 0.999999 ? 0 : Math.Abs(normal[1]) > 0.999999 ? 1 : Math.Abs(normal[2]) > 0.999999 ? 2 : -1;

                // the state before
                double vol0; double[] com0; if (!MassState(doc, out vol0, out com0)) throw new ToolFailure("MASS_PROPERTIES_FAILED", "The mass properties of the part could not be read.");
                var box0 = SolidsBox(part); var bodyBefore = BodyStats(doc); int faces0 = bodyBefore.Value<int?>("faces") ?? 0;
                if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);

                doc.ClearSelection2(true);
                var sd = ((ISelectionMgr)doc.SelectionManager).CreateSelectData(); sd.X = pick[0]; sd.Y = pick[1]; sd.Z = pick[2];       // the pick point IS the placement
                if (!((IEntity)face).Select4(false, sd)) { doc.ClearSelection2(true); throw new ToolFailure("SELECTION_FAILED", "Could not select the face for the forming tool."); }
                IFeature feat = null; string thrown = null;
                try { feat = doc.FeatureManager.InsertFormToolFeature(path, false, rot, cfg, false, false, false, false, false) as IFeature; }
                catch (COMException ex) { thrown = ex.Message; }
                doc.ClearSelection2(true);
                if (feat == null) throw new ToolFailure("FORMING_TOOL_FAILED", "SolidWorks placed no forming tool" + (thrown != null ? " (" + thrown + ")" : "") + ". Check that the face belongs to a sheet-metal part and the tool file opens (" + path + ").");
                string fname = feat.Name;
                doc.EditRebuild3(); int err = FeatureError(feat);
                double vol1; double[] com1; MassState(doc, out vol1, out com1); var box1 = SolidsBox(part); var bodyAfter = BodyStats(doc); int faces1 = bodyAfter.Value<int?>("faces") ?? 0;

                // what SolidWorks kept
                string pathBack = null, cfgBack = null; double rotBack = double.NaN; double[] pickBack = null, placeNormal = null; var problems = new JArray();
                try
                {
                    var d = feat.GetDefinition() as ILibraryFormToolFeatureData; bool acc = false;
                    if (d != null)
                    {
                        try
                        {
                            acc = d.AccessSelections((ModelDoc2)doc, null);
                            pathBack = d.FormToolPath; cfgBack = d.FormToolConfiguration; rotBack = d.RotationAngle;
                            var mpt = d.PickPoint as IMathPoint; if (mpt != null) pickBack = mpt.ArrayData as double[];
                            var pf = d.PlacementFace as IFace2; if (pf != null) placeNormal = pf.Normal as double[];
                        }
                        finally { if (acc) d.ReleaseSelectionAccess(); }
                    }
                }
                catch (COMException ex) { problems.Add("the definition could not be read back: " + ex.Message); }

                // the checks that take the feature out again: the part must be as it was or as it was asked to become
                string refuseCode = null, refuseMsg = null;
                if (err != 0) { refuseCode = "FORMING_TOOL_FAILED"; refuseMsg = "The forming tool reports rebuild error " + err + " (usually it overlaps another feature or does not fit on the sheet around the pick point)."; }
                else if (cfg.Length > 0 && cfgBack != null && !string.Equals(cfg, cfgBack, StringComparison.OrdinalIgnoreCase)) { refuseCode = "CONFIGURATION_NOT_FOUND"; refuseMsg = "SolidWorks used the configuration '" + cfgBack + "', not '" + cfg + "': the tool file has no such configuration."; }
                else if (Math.Abs(vol1 - vol0) < 1e-13 && faces1 == faces0) { refuseCode = "FORMING_TOOL_FAILED"; refuseMsg = "The forming tool changed nothing (same volume, same number of faces)."; }
                bool overhangChecked = axis >= 0 && box0 != null && box1 != null;
                if (refuseCode == null && overhangChecked)
                {
                    for (int a = 0; a < 3; a++)
                    {
                        if (a == axis) continue;       // along the normal the sheet grows by the depth of the form
                        double grow = (box1[a + 3] - box1[a]) - (box0[a + 3] - box0[a]);
                        if (grow > 1e-5) { refuseCode = "FORMING_TOOL_OVERHANG"; refuseMsg = "The tool does not fit on the face at that point: its footprint sticks out past the edge of the sheet and SolidWorks EXTENDED the sheet to hold it (the part grew by " + Mmf(grow) + " mm along " + "xyz"[a] + "). Move the point away from the edge, rotate the tool, or choose a smaller one."; break; }
                    }
                }
                if (refuseCode != null)
                {
                    DeleteFeatureQuietly(doc, fname); doc.EditRebuild3();
                    double vol2 = ExactVolume(doc); bool clean = Math.Abs(vol2 - vol0) < 1e-13;
                    throw new ToolFailure(refuseCode, refuseMsg + (clean ? " The feature was taken out again: the part is as it was." : " The feature could not be taken out cleanly (the volume is " + (vol2 * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3, was " + (vol0 * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " mm3): delete '" + fname + "' with edit_feature."));
                }

                var res = new JObject { ["feature"] = fname, ["feature_type"] = feat.GetTypeName2(), ["tool"] = Path.GetFileNameWithoutExtension(path), ["tool_path"] = path, ["configuration"] = cfgBack, ["point"] = new JArray(pick), ["pushes_toward"] = new JArray(-normal[0], -normal[1], -normal[2]) };
                if (!double.IsNaN(rotBack)) res["rotation"] = Math.Round(((rotBack * 180 / Math.PI) % 360 + 360) % 360, 6);
                if (pickBack != null) res["pick_point"] = new JArray(pickBack);
                if (placeNormal != null) res["face_normal"] = new JArray(placeNormal);
                res["volume_before"] = vol0; res["volume_after"] = vol1; res["volume_change"] = vol1 - vol0; res["body_before"] = bodyBefore; res["body_after"] = bodyAfter;
                if (com0 != null && com1 != null) { res["centre_of_mass_before"] = new JArray(com0); res["centre_of_mass_after"] = new JArray(com1); }        // the centre of mass moves with the pick point: a second placement 30 mm away moves it by 30 mm x (volume added / volume)
                res["overhang_checked"] = overhangChecked;
                if (pathBack != null && !string.Equals(Path.GetFullPath(pathBack), path, StringComparison.OrdinalIgnoreCase)) problems.Add("SolidWorks kept the tool '" + pathBack + "', '" + path + "' was asked");
                if (!double.IsNaN(rotBack)) { double dr = Math.Abs(Math.IEEERemainder(rotBack - rot, 2 * Math.PI)); if (dr > 1e-6) problems.Add("SolidWorks kept a rotation of " + (rotBack * 180 / Math.PI).ToString("0.######", CultureInfo.InvariantCulture) + " deg, " + rotDeg.ToString("0.######", CultureInfo.InvariantCulture) + " deg was asked"); }
                if (pickBack != null && Math.Sqrt(Dot(V(pickBack, pick), V(pickBack, pick))) > 1e-6) problems.Add("SolidWorks placed the tool at " + P3(pickBack) + ", " + P3(pick) + " was asked");
                var owned = feat.GetFaces() as object[]; res["feature_faces"] = owned == null ? 0 : owned.Length;
                if (owned == null || owned.Length == 0) problems.Add("the feature owns no faces");
                else
                {
                    // the pick point must lie in the box of the faces the feature made (the boxes of curved faces are generous, so this only catches a tool that stands somewhere else altogether)
                    double dmax = 0; var lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; var hi = new[] { double.MinValue, double.MinValue, double.MinValue };
                    foreach (var o in owned) { var fb = ((IFace2)o).GetBox() as double[]; if (fb == null) continue; for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], fb[i]); hi[i] = Math.Max(hi[i], fb[i + 3]); } }
                    for (int i = 0; i < 3; i++) dmax += Math.Pow(Math.Max(Math.Max(lo[i] - pick[i], pick[i] - hi[i]), 0), 2);
                    dmax = Math.Sqrt(dmax); res["pick_to_footprint"] = Math.Round(dmax, 9);
                    if (dmax > 1e-3) problems.Add("the pick point is " + Mmf(dmax) + " mm outside the faces the tool made: it stands somewhere else");
                }
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0; return res;
            });
        }
    }
}
