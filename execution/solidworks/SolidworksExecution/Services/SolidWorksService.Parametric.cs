// Parametric building blocks through the typed API: document units (the user's rule: 2 decimals for mm, 3 for inches), equations (global variables + dimensions linked to them) and the fully defined,
// origin-centred rectangle sketch (the "resilient master sketch": the one the user can re-size later through two dimensions or two equations). Lengths are METERS unless a value is an equation string.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // document_units
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // swLengthUnit_e as plain numbers (the swconst enum types must not be used at run time here): 0 mm, 1 cm, 2 m, 3 in, 4 ft, 5 ft-in, 6 angstrom, 7 nm, 8 micron, 9 mil, 10 uin
        private static readonly string[] LengthUnitNames = { "mm", "cm", "m", "in", "ft", "ft-in", "angstrom", "nm", "micron", "mil", "uin" };
        private static readonly double[] UnitsPerMeter = { 1000, 100, 1, 1 / 0.0254, 1 / 0.3048, 1 / 0.0254, 1e10, 1e9, 1e6, 1000 / 0.0254, 1e6 / 0.0254 };

        // A rebuild (EditRebuild3, an equation solve) can close the sketch the caller was editing - and the next tool (a base flange) then finds no profile. Put it back.
        private static void RestoreActiveSketch(IModelDoc2 doc, string name)
        {
            if (name == null || doc.SketchManager.ActiveSketch != null) return;
            doc.ClearSelection2(true);
            if (doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0)) doc.EditSketch();
            doc.ClearSelection2(true);
        }

        private static JObject ReadUnits(IModelDoc2 doc)
        {
            var x = doc.Extension;
            int u = x.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            int d = x.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            bool known = u >= 0 && u < LengthUnitNames.Length;
            return new JObject { ["length"] = known ? LengthUnitNames[u] : "unit " + u, ["length_code"] = u, ["decimals"] = d, ["units_per_meter"] = known ? UnitsPerMeter[u] : 0 };
        }

        public ExecutionResponse DocumentUnits(ToolRequest request)
        {
            var action = ((request.Params as JObject)?.Value<string>("action") ?? "get").ToLowerInvariant();
            return Typed(request, action == "set", (doc, p) =>
            {
                if (action == "get") return ReadUnits(doc);
                if (action != "set") throw new ToolFailure("INVALID_PARAMETER", "action must be get or set.");
                string len = p.Value<string>("length")?.Trim().ToLowerInvariant(); int? dec = p.Value<int?>("decimals");
                if (len == null && !dec.HasValue) throw new ToolFailure("MISSING_PARAMETER", "give length (mm | cm | m | in | ft) and/or decimals (0..8).");
                int code = -1;
                if (len != null)
                {
                    code = Array.IndexOf(LengthUnitNames, len); if (code < 0 || code > 4) throw new ToolFailure("INVALID_PARAMETER", "length must be mm, cm, m, in or ft.");
                    if (!dec.HasValue) dec = code == 3 || code == 4 ? 3 : 2;           // the user's standing rule: 2 decimals for metric, 3 for inches
                }
                if (dec.HasValue && (dec.Value < 0 || dec.Value > 8)) throw new ToolFailure("INVALID_PARAMETER", "decimals must be 0..8.");
                var x = doc.Extension; var none = (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified; string activeSketch = (doc.SketchManager.ActiveSketch as IFeature)?.Name;
                if (code >= 0) x.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, none, code);
                if (dec.HasValue) x.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, none, dec.Value);
                doc.EditRebuild3(); RestoreActiveSketch(doc, activeSketch);
                var back = ReadUnits(doc);
                back["verified"] = (len == null || back.Value<string>("length") == len) && (!dec.HasValue || back.Value<int>("decimals") == dec.Value);
                return back;
            });
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // equations
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static string EquationName(string equation)
        {
            if (equation == null) return null; int a = equation.IndexOf('"'); if (a < 0) return null; int b = equation.IndexOf('"', a + 1); return b < 0 ? null : equation.Substring(a + 1, b - a - 1);
        }

        private static JArray ListEquations(IEquationMgr mgr)
        {
            var arr = new JArray(); int n = mgr.GetCount();
            for (int i = 0; i < n; i++)
            {
                string t = mgr.get_Equation(i);
                arr.Add(new JObject { ["index"] = i, ["name"] = EquationName(t), ["text"] = t, ["value"] = mgr.get_Value(i), ["global_variable"] = mgr.get_GlobalVariable(i), ["disabled"] = mgr.get_Disabled(i) });
            }
            return arr;
        }

        private static string ValueText(JToken v, string what)
        {
            if (v == null || v.Type == JTokenType.Null) throw new ToolFailure("INVALID_PARAMETER", what + " has no value.");
            if (v.Type == JTokenType.String) return ((string)v).Trim();
            if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) return v.Value<double>().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            throw new ToolFailure("INVALID_PARAMETER", what + " must be a number or an equation string.");
        }

        public ExecutionResponse Equations(ToolRequest request)
        {
            var action = ((request.Params as JObject)?.Value<string>("action") ?? "list").ToLowerInvariant();
            return Typed(request, action != "list", (doc, p) =>
            {
                var mgr = doc.GetEquationMgr(); var unit = ReadUnits(doc).Value<string>("length"); string activeSketch = (doc.SketchManager.ActiveSketch as IFeature)?.Name;
                if (action == "list") return new JObject { ["unit"] = unit, ["count"] = mgr.GetCount(), ["equations"] = ListEquations(mgr) };
                if (action != "set" && action != "delete") throw new ToolFailure("INVALID_PARAMETER", "action must be list, set or delete.");

                var problems = new JArray(); var requested = new List<string>();
                // delete by name
                var del = AsArray(p["delete"], "delete");
                if (action == "delete" && del == null) throw new ToolFailure("MISSING_PARAMETER", "action=delete needs delete: the names of the equations / variables to remove (a JSON array or comma-separated).");
                if (del != null)
                    foreach (var t in del)
                    {
                        string nm = (string)t; bool done = false;
                        for (int i = mgr.GetCount() - 1; i >= 0; i--) if (EquationName(mgr.get_Equation(i)) == nm) { mgr.Delete(i); done = true; }
                        if (!done) problems.Add("no equation named '" + nm + "' to delete");
                    }

                var variables = AsObject(p["variables"], "variables"); var links = AsObject(p["links"], "links"); var raw = AsArray(p["equations"], "equations");
                var texts = new List<string>();
                var known = new HashSet<string>(); for (int i = 0; i < mgr.GetCount(); i++) if (mgr.get_GlobalVariable(i)) known.Add(EquationName(mgr.get_Equation(i)));
                if (variables != null)                                                  // global variables first: a link may refer to one defined in the same call
                    foreach (var kv in variables) { texts.Add("\"" + kv.Key + "\"= " + ValueText(kv.Value, "variable " + kv.Key)); known.Add(kv.Key); }
                if (links != null)
                    foreach (var kv in links)
                    {
                        string rhs = ValueText(kv.Value, "link " + kv.Key);
                        if (known.Contains(rhs) || (Regex.IsMatch(rhs, @"^[A-Za-z_][A-Za-z0-9_ ]*$") && !rhs.StartsWith("\""))) rhs = "\"" + rhs + "\"";       // a bare variable name -> quoted reference
                        texts.Add("\"" + kv.Key + "\"= " + rhs);
                    }
                if (raw != null) foreach (var t in raw) texts.Add((string)t);
                if (texts.Count == 0 && del == null) throw new ToolFailure("MISSING_PARAMETER", "set needs variables {name: value}, links {dimension: variable or expression} and/or equations [raw equation strings].");

                foreach (var text in texts)
                {
                    string nm = EquationName(text); if (nm == null) { problems.Add("not an equation (needs a quoted name): " + text); continue; }
                    int found = -1; for (int i = 0; i < mgr.GetCount(); i++) if (EquationName(mgr.get_Equation(i)) == nm) { found = i; break; }
                    if (found >= 0) { mgr.set_Equation(found, text); requested.Add(nm); }
                    else { int idx = mgr.Add2(-1, text, true); if (idx < 0) problems.Add("SolidWorks refused: " + text); else requested.Add(nm); }
                }
                mgr.EvaluateAll(); doc.EditRebuild3(); RestoreActiveSketch(doc, activeSketch);

                // verify: every requested equation exists and evaluates (a refused / broken one is reported with its text)
                var all = ListEquations(mgr); var byName = all.OfType<JObject>().Where(o => o.Value<string>("name") != null).GroupBy(o => o.Value<string>("name")).ToDictionary(g => g.Key, g => g.Last());
                foreach (var nm in requested.Distinct()) if (!byName.ContainsKey(nm)) problems.Add("equation '" + nm + "' is not in the list after the call");
                var res = new JObject { ["unit"] = unit, ["count"] = mgr.GetCount(), ["equations"] = all, ["requested"] = new JArray(requested.Distinct().ToArray()) };
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0;
                return res;
            });
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sketch_center_rectangle
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static string ConstrainedName(int code)
        {
            switch (code) { case 1: return "unknown"; case 2: return "under_defined"; case 3: return "fully_defined"; case 4: return "over_defined"; case 5: return "no_solution"; case 6: return "invalid_solution"; case 7: return "autosolve_off"; default: return "code " + code; }
        }

        // a dimension on ONE selected sketch segment (its length), created without the input box; returns the dimension's name ("D1@Sketch1") or null
        private string DimensionSegment(IModelDoc2 doc, ISketchSegment seg, double value, double labelX, double labelY, string sketchName)
        {
            doc.ClearSelection2(true);
            if (!seg.Select4(false, null)) return null;
            bool prev = _solidWorks.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
            try { return SetDimensionValue(doc.AddDimension2(labelX, labelY, 0) as IDisplayDimension, value); }
            finally { _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, prev); doc.ClearSelection2(true); }
        }

        // a horizontal / vertical dimension between the sketch ORIGIN and a sketch point (the centre of an off-origin rectangle), created without the input box; returns its name or null
        private string DimensionOriginToPoint(IModelDoc2 doc, ISketchPoint pt, bool horizontal, double value, double labelX, double labelY)
        {
            doc.ClearSelection2(true);
            bool a = pt.Select4(false, null), b = doc.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, true, 0, null, 0);
            if (!a || !b) { doc.ClearSelection2(true); return null; }
            bool prev = _solidWorks.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
            try { return SetDimensionValue((horizontal ? doc.AddHorizontalDimension2(labelX, labelY, 0) : doc.AddVerticalDimension2(labelX, labelY, 0)) as IDisplayDimension, value); }
            finally { _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, prev); doc.ClearSelection2(true); }
        }

        // sets a freshly created dimension to `value` and returns its name ("D1@Sketch1"), or null when it is a driven (reference) dimension or SolidWorks refused the value
        private static string SetDimensionValue(IDisplayDimension dd, double value)
        {
            var d = dd?.GetDimension2(0) as IDimension; if (d == null) return null;
            d.SystemValue = value;
            if (Math.Abs(d.SystemValue - value) > 1e-6) return null;                      // do not report it as a dimension that defines the sketch
            string full = d.FullName ?? ""; int at = full.IndexOf('@');
            return at > 0 && full.IndexOf('@', at + 1) > 0 ? full.Substring(0, full.IndexOf('@', at + 1)) : full;       // "D1@Sketch1@part.Part" -> "D1@Sketch1"
        }

        // width x height (meters) of a sketch's real (non-construction) lines, measured on the lines themselves
        private static double[] SketchExtent(ISketch sk)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
            {
                var ln = o as ISketchLine; if (ln == null || ((ISketchSegment)ln).ConstructionGeometry) continue;
                var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint; if (a == null || b == null) continue;
                x0 = Math.Min(x0, Math.Min(a.X, b.X)); x1 = Math.Max(x1, Math.Max(a.X, b.X)); y0 = Math.Min(y0, Math.Min(a.Y, b.Y)); y1 = Math.Max(y1, Math.Max(a.Y, b.Y));
            }
            return new[] { x1 - x0, y1 - y0 };
        }

        public ExecutionResponse SketchCenterRectangle(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                if (!(doc is IPartDoc)) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sketch_center_rectangle needs an active part.");
                if (doc.SketchManager.ActiveSketch != null) throw new ToolFailure("SKETCH_ALREADY_ACTIVE", "A sketch is already active: finish it (any feature exits it) before starting another.");
                double w = p.Value<double?>("width") ?? 0, h = p.Value<double?>("height") ?? 0;
                if (w <= 0 || h <= 0) throw new ToolFailure("MISSING_PARAMETER", "width and height (meters, > 0) are required: the rectangle's X and Y extent, centred on the sketch origin (or on center_x / center_y).");
                double cx = p.Value<double?>("center_x") ?? 0, cy = p.Value<double?>("center_y") ?? 0;      // an off-origin centre: two more driving dimensions from the origin (D3 / D4) instead of the coincident relation
                string plane = p.Value<string>("plane") ?? "Front Plane";

                doc.ClearSelection2(true);
                if (!doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0)) throw new ToolFailure("PLANE_NOT_FOUND", "No plane named '" + plane + "'.");     // by NAME: no view involved
                doc.SketchManager.InsertSketch(true);
                var sk = doc.SketchManager.ActiveSketch as ISketch; if (sk == null) throw new ToolFailure("SKETCH_FAILED", "InsertSketch did not open a sketch on '" + plane + "'.");
                string sketchName = ((IFeature)sk).Name;

                // with sketch inference ON (the SolidWorks default) the corners snap to model edges nearby and are held there: beside the cabinet a 0.30 x 0.45 m rectangle came out 0.2987 x 0.4492 and
                // already "fully defined" by those relations, so the D1 / D2 dimensions below were driven (reference) ones whose values did nothing
                var sm = doc.SketchManager; bool inference = sm.AutoInference; sm.AutoInference = false;
                try { sm.CreateCenterRectangle(cx, cy, 0, cx + w / 2, cy + h / 2, 0); } finally { sm.AutoInference = inference; }
                // the top (horizontal) and right (vertical) real lines
                ISketchLine top = null, right = null; double topY = double.MinValue, rightX = double.MinValue;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var ln = o as ISketchLine; if (ln == null || ((ISketchSegment)ln).ConstructionGeometry) continue;
                    var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint; if (a == null || b == null) continue;
                    if (Math.Abs(a.Y - b.Y) < 1e-9 && a.Y > topY) { topY = a.Y; top = ln; }
                    if (Math.Abs(a.X - b.X) < 1e-9 && a.X > rightX) { rightX = a.X; right = ln; }
                }
                if (top == null || right == null) throw new ToolFailure("SKETCH_FAILED", "The centre rectangle's lines were not found in the sketch.");
                var names = new JArray(); var problems = new JArray();
                string dw = DimensionSegment(doc, (ISketchSegment)top, w, cx, cy + h / 2 + 0.02, sketchName); if (dw != null) names.Add(dw); else problems.Add("the width dimension could not be created, or did not take the value");
                string dh = DimensionSegment(doc, (ISketchSegment)right, h, cx + w / 2 + 0.02, cy, sketchName); if (dh != null) names.Add(dh); else problems.Add("the height dimension could not be created, or did not take the value");

                // tie the centre to the sketch origin when the sketch still has freedom (the centre point is created next to the origin, not always coincident with it)
                int status = sk.GetConstrainedStatus();
                if (status != 3)
                {
                    ISketchPoint centre = null;
                    foreach (var o in (sk.GetSketchPoints2() as object[]) ?? new object[0]) { var pt = o as ISketchPoint; if (pt != null && Math.Abs(pt.X - cx) < 1e-9 && Math.Abs(pt.Y - cy) < 1e-9) { centre = pt; break; } }
                    if (centre != null)
                    {
                        bool onX = Math.Abs(cx) < 1e-12, onY = Math.Abs(cy) < 1e-12;
                        if (onX && onY) TieToOrigin(doc, sk, centre, new[] { "sgCOINCIDENT" }, problems);
                        else
                        {
                            // off the origin: a distance from the origin along each axis is a driving dimension (the next free D number, X first); an offset of 0 is a relation (centre on the Y / X axis),
                            // added LAST so that "fully defined" proves it took (the relation names for two points are the *POINTS2D ones: sgVERTICALPOINTS and the line forms are silent no-ops)
                            if (!onX) { string dx = DimensionOriginToPoint(doc, centre, true, Math.Abs(cx), cx / 2, cy + h / 2 + 0.04); if (dx != null) names.Add(dx); else problems.Add("the centre's X offset dimension could not be created"); }
                            if (!onY) { string dy = DimensionOriginToPoint(doc, centre, false, Math.Abs(cy), cx + w / 2 + 0.04, cy / 2); if (dy != null) names.Add(dy); else problems.Add("the centre's Y offset dimension could not be created"); }
                            if (onX) TieToOrigin(doc, sk, centre, new[] { "sgVERTICALPOINTS2D", "sgVERTPOINTS2D" }, problems);       // measured 2026-10-29: sgVERTICALPOINTS2D takes, sgVERTPOINTS2D is a silent no-op
                            else if (onY) TieToOrigin(doc, sk, centre, new[] { "sgHORIZONTALPOINTS2D", "sgHORIZPOINTS" }, problems);
                        }
                        doc.ClearSelection2(true); status = sk.GetConstrainedStatus();
                    }
                    else problems.Add("no centre point found to tie to the origin");
                }
                var size = SketchExtent(sk);                                      // what the lines really measure, not what was asked
                if (Math.Abs(size[0] - w) > 1e-6 || Math.Abs(size[1] - h) > 1e-6) problems.Add("the rectangle measures " + size[0].ToString("0.######") + " x " + size[1].ToString("0.######") + " m, not " + w.ToString("0.######") + " x " + h.ToString("0.######") + " m: SolidWorks related it to other geometry");
                var res = new JObject { ["sketch"] = sketchName, ["plane"] = plane, ["width"] = w, ["height"] = h, ["size"] = new JArray(size[0], size[1]), ["dimensions"] = names, ["constrained_status"] = status, ["constrained"] = ConstrainedName(status) };
                if (cx != 0 || cy != 0) res["center"] = new JArray(cx, cy);
                if (status != 3) problems.Add("the sketch is " + ConstrainedName(status) + ", not fully defined");
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = status == 3 && problems.Count == 0;
                return res;
            });
        }

        // a relation between a sketch point and the sketch origin: the first of `relations` after which the sketch reads fully defined (or, for the coincident tie, simply applied) is kept;
        // SketchAddConstraints answers nothing, so a name SolidWorks does not know is only seen as "no change" - a failure is reported as a problem
        private static void TieToOrigin(IModelDoc2 doc, ISketch sk, ISketchPoint pt, string[] relations, JArray problems)
        {
            foreach (var relation in relations)
            {
                doc.ClearSelection2(true);
                bool a = pt.Select4(false, null), b = doc.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, true, 0, null, 0);
                if (!a || !b) { problems.Add("the centre could not be tied to the origin (" + relation + "; selected " + a + " / " + b + ")"); break; }
                doc.SketchAddConstraints(relation);
                doc.ClearSelection2(true);
                if (relations.Length == 1 || sk.GetConstrainedStatus() == 3) return;
            }
            if (relations.Length > 1) problems.Add("the centre could not be put on the axis (tried " + string.Join(", ", relations) + ")");
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sketch_fully_define - EVERY sketch fully defined (user rule 2026-10-29: "even random hole features"; master sketches, hole sketches, cut sketches alike)
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // ISketchManager.FullyDefineSketch exists but stops half way (measured: with the origin as datum it adds 8 of the 10 dimensions an off-origin rectangle with two circles needs, status stays
        // under-defined, a second call changes nothing), so the tool defines the sketch itself, from the ORIGIN: every arc / circle centre and every vertex gets a horizontal and a vertical
        // dimension to the origin (a point ON an axis gets the relation instead), every circle / arc gets its diameter / radius. SolidWorks is the oracle for redundancy: with the prompt for
        // over-defining dimensions off and "set driven by default" on, a dimension that would over-define comes out DRIVEN and is deleted again; a relation that over-defines is undone.
        public ExecutionResponse SketchFullyDefine(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                if (!(doc is IPartDoc)) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sketch_fully_define needs an active part.");
                string name = p.Value<string>("sketch");
                // RELATIONS FIRST (user rule 2026-11-02, master sketch: "use sketch relationships such as horizontal, vertical in order to cut down on the amount of dimensions"):
                //   relations = none (default: origin dimensions only, as before) | first (geometric relations, then origin dimensions for what is left) | only (relations only: add the design-intent dimensions yourself, then call again)
                //   relate    = explicit relations resolved by coordinates (sketch metres) - see ApplyExplicitRelations
                string relMode = (p.Value<string>("relations") ?? "none").Trim().ToLowerInvariant();
                if (relMode != "none" && relMode != "first" && relMode != "only") throw new ToolFailure("INVALID_PARAMETER", "relations must be none, first or only.");
                var relate = p["relate"] as JArray;
                var relApplied = new JArray(); var relSkipped = new JArray();
                var sm = doc.SketchManager;
                var sk = sm.ActiveSketch as ISketch;
                if (sk != null && !string.IsNullOrEmpty(name) && !string.Equals(((IFeature)sk).Name, name, StringComparison.OrdinalIgnoreCase))
                    throw new ToolFailure("SKETCH_ALREADY_ACTIVE", "Sketch '" + ((IFeature)sk).Name + "' is active, not '" + name + "': finish it first (any feature exits it), or omit `sketch`.");
                if (sk == null)
                {
                    if (string.IsNullOrEmpty(name)) throw new ToolFailure("NO_ACTIVE_SKETCH", "No sketch is active: call it right after create_sketch / add_sketch_entities, or pass `sketch` (the sketch's name) to edit that one.");
                    // the profile sketch of an EDGE FLANGE is not ours to define: measured 2026-10-30, fully defining one deleted dimensions inside the flange's own sketch, the flange failed to rebuild
                    // (66 faces -> 22) and the next call killed SolidWorks. Shorten a profile with sheet_metal_feature profile_limits instead.
                    var named = ((IPartDoc)doc).FeatureByName(name) as IFeature; var owner = named == null ? null : named.GetOwnerFeature() as IFeature;
                    if (owner != null && owner.GetTypeName2() == "EdgeFlange")
                        throw new ToolFailure("SKETCH_OWNED_BY_FLANGE", "Sketch '" + name + "' is the generated profile of " + owner.Name + ": fully defining it breaks the flange. Use sheet_metal_feature profile_limits to shorten a profile.");
                    doc.ClearSelection2(true);
                    if (!doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0)) throw new ToolFailure("SKETCH_NOT_FOUND", "No sketch named '" + name + "'.");
                    doc.EditSketch(); doc.ClearSelection2(true);
                    sk = sm.ActiveSketch as ISketch;
                    if (sk == null) throw new ToolFailure("SKETCH_FAILED", "Sketch '" + name + "' could not be opened for editing.");
                }
                string sketchName = ((IFeature)sk).Name;
                int before = sk.GetConstrainedStatus();
                var dims = new JArray(); var relations = new JArray(); var problems = new JArray(); int removed = 0;

                bool pPrompt = _solidWorks.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState);
                bool pDriven = _solidWorks.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsSetDrivenByDefault);
                bool pInput = _solidWorks.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
                // sketch inference would snap a relation / dimension target to model geometry nearby: off while we work (restored below)
                bool inference = sm.AutoInference;
                try
                {
                    _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false);
                    _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsSetDrivenByDefault, true);
                    _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                    sm.AutoInference = false;

                    // 0. relations first: symmetric pairs about a centre line on the Y axis, horizontal / vertical lines, then the caller's explicit relations
                    if (relMode != "none") AddGeometricRelations(doc, sk, sm, relApplied, relSkipped, problems);
                    if (relate != null && relate.Count > 0) ApplyExplicitRelations(doc, sk, relate, relApplied, relSkipped);

                    // 1. the entities: arc / circle centres first, then every other point (line ends, arc ends, loose points)
                    var centers = new List<ISketchPoint>(); var arcs = new List<ISketchArc>(); var verts = new List<ISketchPoint>(); var seen = new HashSet<string>();
                    Func<ISketchPoint, string> key = pt => Math.Round(pt.X * 1e7).ToString("0") + "," + Math.Round(pt.Y * 1e7).ToString("0");
                    foreach (var so in (sk.GetSketchSegments() as object[]) ?? new object[0])
                    {
                        var arc = so as ISketchArc; var ln = so as ISketchLine;
                        if (arc != null)
                        {
                            var c = arc.GetCenterPoint2() as ISketchPoint; if (c != null && seen.Add(key(c))) centers.Add(c);
                            arcs.Add(arc);
                            if (arc.IsCircle() == 0) foreach (var ep in new[] { arc.GetStartPoint2() as ISketchPoint, arc.GetEndPoint2() as ISketchPoint }) if (ep != null && seen.Add(key(ep))) verts.Add(ep);
                        }
                        else if (ln != null)
                        {
                            foreach (var ep in new[] { ln.GetStartPoint2() as ISketchPoint, ln.GetEndPoint2() as ISketchPoint }) if (ep != null && seen.Add(key(ep))) verts.Add(ep);
                        }
                        else problems.Add("a " + ((ISketchSegment)so).GetType() + " segment (spline, ellipse ...) is not handled: its points are not dimensioned");
                    }
                    foreach (var po in (sk.GetSketchPoints2() as object[]) ?? new object[0]) { var pt = po as ISketchPoint; if (pt != null && seen.Add(key(pt))) verts.Add(pt); }
                    var all = new List<ISketchPoint>(); all.AddRange(centers); all.AddRange(verts);
                    ExecLog.Write($"sketch_fully_define: {sketchName} status {before}, {centers.Count} centre(s), {arcs.Count} arc/circle(s), {verts.Count} other point(s)");

                    if (relMode != "only")
                    {
                    // 2. the ordinates of every point off the axes (a redundant one is deleted again)
                    foreach (var pt in all)
                    {
                        if (sk.GetConstrainedStatus() == 3) break;
                        if (Math.Abs(pt.X) > 1e-9) KeepOrDropDimension(doc, OriginOrdinate(doc, pt, true, pt.X / 2, pt.Y + 0.01), dims, ref removed);
                        if (sk.GetConstrainedStatus() == 3) break;
                        if (Math.Abs(pt.Y) > 1e-9) KeepOrDropDimension(doc, OriginOrdinate(doc, pt, false, pt.X + 0.01, pt.Y / 2), dims, ref removed);
                    }
                    // 2b. a point ON an axis: a relation to the origin (only while the sketch still has freedom; one that over-defines is undone)
                    foreach (var pt in all)
                    {
                        if (sk.GetConstrainedStatus() == 3) break;
                        bool onX = Math.Abs(pt.X) <= 1e-9, onY = Math.Abs(pt.Y) <= 1e-9;
                        string[] names = onX && onY ? new[] { "sgCOINCIDENT" } : onX ? new[] { "sgVERTICALPOINTS2D" } : onY ? new[] { "sgHORIZONTALPOINTS2D" } : null;
                        if (names == null) continue;
                        TieOnAxis(doc, sk, pt, names[0], relations, problems);
                    }
                    // 3. the sizes (a diameter for a circle, a radius for an arc)
                    foreach (var arc in arcs)
                    {
                        if (sk.GetConstrainedStatus() == 3) break;
                        doc.ClearSelection2(true);
                        if (!((ISketchSegment)arc).Select4(false, null)) continue;
                        var c = arc.GetCenterPoint2() as ISketchPoint;
                        KeepOrDropDimension(doc, doc.AddDimension2((c?.X ?? 0) + arc.GetRadius() + 0.01, (c?.Y ?? 0) + 0.01, 0) as IDisplayDimension, dims, ref removed);
                        doc.ClearSelection2(true);
                    }
                    }
                }
                finally
                {
                    doc.ClearSelection2(true);
                    _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, pPrompt);
                    _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsSetDrivenByDefault, pDriven);
                    _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, pInput);
                    sm.AutoInference = inference;
                }
                int status = sk.GetConstrainedStatus();
                if (status != 3 && relMode != "only") problems.Add("the sketch is " + ConstrainedName(status) + ", not fully defined");
                var res = new JObject
                {
                    ["sketch"] = sketchName, ["status_before"] = ConstrainedName(before), ["constrained_status"] = status, ["constrained"] = ConstrainedName(status),
                    ["dimensions"] = dims, ["relations"] = relations, ["redundant_dimensions_removed"] = removed
                };
                if (relApplied.Count > 0 || relSkipped.Count > 0) { res["geometric_relations"] = relApplied; if (relSkipped.Count > 0) res["relations_skipped"] = relSkipped; }
                res["relation_count"] = RelationCount(sk);
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = status == 3;
                return res;
            });
        }

        // a horizontal / vertical dimension between the sketch origin and a point, created without the input box; the value stays what the geometry measures
        private IDisplayDimension OriginOrdinate(IModelDoc2 doc, ISketchPoint pt, bool horizontal, double labelX, double labelY)
        {
            doc.ClearSelection2(true);
            bool a = pt.Select4(false, null), b = doc.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, true, 0, null, 0);
            if (!a || !b) { doc.ClearSelection2(true); return null; }
            var dd = (horizontal ? doc.AddHorizontalDimension2(labelX, labelY, 0) : doc.AddVerticalDimension2(labelX, labelY, 0)) as IDisplayDimension;
            doc.ClearSelection2(true);
            return dd;
        }

        // keeps a freshly made dimension (its name and value go to `dims`) or, when SolidWorks made it DRIVEN (redundant), deletes it again
        private void KeepOrDropDimension(IModelDoc2 doc, IDisplayDimension dd, JArray dims, ref int removed)
        {
            var d = dd?.GetDimension2(0) as IDimension; if (d == null) return;
            if (d.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven)
            {
                doc.ClearSelection2(true);
                var ann = dd.GetAnnotation() as IAnnotation;
                if (ann != null && ann.Select3(false, null)) doc.EditDelete();
                doc.ClearSelection2(true);
                removed++;
                return;
            }
            string full = d.FullName ?? ""; int at = full.IndexOf('@'); int at2 = at > 0 ? full.IndexOf('@', at + 1) : -1;
            dims.Add(new JObject { ["name"] = at2 > 0 ? full.Substring(0, at2) : full, ["value"] = Math.Round(d.SystemValue, 9) });
        }

        // a point on a sketch axis: one relation to the origin; an over-defining one is undone
        private void TieOnAxis(IModelDoc2 doc, ISketch sk, ISketchPoint pt, string relation, JArray relations, JArray problems)
        {
            doc.ClearSelection2(true);
            bool a = pt.Select4(false, null), b = doc.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, true, 0, null, 0);
            if (!a || !b) { doc.ClearSelection2(true); problems.Add("a point on an axis could not be selected for its relation (" + relation + ")"); return; }
            int count0 = RelationCount(sk);
            doc.SketchAddConstraints(relation);
            doc.ClearSelection2(true);
            int status = sk.GetConstrainedStatus();
            if (status == 4 || status == 5 || status == 6) { doc.EditUndo2(1); return; }                       // redundant: it would over-define the sketch
            if (RelationCount(sk) > count0) relations.Add(relation + " (" + (pt.X * 1000).ToString("0.###") + ", " + (pt.Y * 1000).ToString("0.###") + " mm) -> origin");
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // relations first (master sketch, user rule 2026-11-02): every relation removes a dimension; SolidWorks is the oracle for redundancy (a relation that over-defines is undone and reported)
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static List<ISketchPoint> AllSketchPoints(ISketch sk)
        {
            var list = new List<ISketchPoint>(); var seen = new HashSet<string>();
            Action<ISketchPoint> add = pt => { if (pt == null) return; string k = Math.Round(pt.X * 1e7).ToString("0") + "," + Math.Round(pt.Y * 1e7).ToString("0"); if (seen.Add(k)) list.Add(pt); };
            foreach (var so in (sk.GetSketchSegments() as object[]) ?? new object[0])
            {
                var ln = so as ISketchLine; var arc = so as ISketchArc;
                if (ln != null) { add(ln.GetStartPoint2() as ISketchPoint); add(ln.GetEndPoint2() as ISketchPoint); }
                else if (arc != null) { add(arc.GetCenterPoint2() as ISketchPoint); if (arc.IsCircle() == 0) { add(arc.GetStartPoint2() as ISketchPoint); add(arc.GetEndPoint2() as ISketchPoint); } }
            }
            foreach (var po in (sk.GetSketchPoints2() as object[]) ?? new object[0]) add(po as ISketchPoint);
            return list;
        }

        private static ISketchPoint NearestSketchPoint(ISketch sk, double x, double y, double tol)
        {
            ISketchPoint best = null; double bd = double.MaxValue;
            foreach (var pt in AllSketchPoints(sk)) { double d = Math.Sqrt((pt.X - x) * (pt.X - x) + (pt.Y - y) * (pt.Y - y)); if (d < bd) { bd = d; best = pt; } }
            return bd <= tol ? best : null;
        }

        private static ISketchSegment NearestSketchSegment(ISketch sk, double x, double y, double tol)
        {
            ISketchSegment best = null; double bd = double.MaxValue;
            foreach (var so in (sk.GetSketchSegments() as object[]) ?? new object[0])
            {
                double d = double.MaxValue; var ln = so as ISketchLine; var arc = so as ISketchArc;
                if (ln != null)
                {
                    var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint; if (a == null || b == null) continue;
                    double vx = b.X - a.X, vy = b.Y - a.Y, l2 = vx * vx + vy * vy;
                    double t = l2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((x - a.X) * vx + (y - a.Y) * vy) / l2));
                    double px = a.X + t * vx - x, py = a.Y + t * vy - y; d = Math.Sqrt(px * px + py * py);
                }
                else if (arc != null) { var c = arc.GetCenterPoint2() as ISketchPoint; if (c == null) continue; d = Math.Abs(Math.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y)) - arc.GetRadius()); }
                if (d < bd) { bd = d; best = so as ISketchSegment; }
            }
            return bd <= tol ? best : null;
        }

        // ONE relation on already resolved entities (ISketchPoint / ISketchSegment); a relation that over-defines the sketch is undone, one SolidWorks silently ignores is reported
        private bool TryRelation(IModelDoc2 doc, ISketch sk, string relation, string what, object[] ents, JArray applied, JArray skipped)
        {
            doc.ClearSelection2(true);
            for (int i = 0; i < ents.Length; i++)
            {
                bool sel = ents[i] is ISketchPoint ? ((ISketchPoint)ents[i]).Select4(i > 0, null) : ents[i] is ISketchSegment && ((ISketchSegment)ents[i]).Select4(i > 0, null);
                if (!sel) { doc.ClearSelection2(true); skipped.Add(what + ": an entity could not be selected"); return false; }
            }
            int c0 = RelationCount(sk);
            doc.SketchAddConstraints(relation);
            doc.ClearSelection2(true);
            int st = sk.GetConstrainedStatus();
            if (st == 4 || st == 5 || st == 6) { doc.EditUndo2(1); skipped.Add(what + ": redundant or conflicting (over-defines the sketch), undone"); return false; }
            if (RelationCount(sk) <= c0) { skipped.Add(what + ": SolidWorks added no relation"); return false; }
            applied.Add(what);
            return true;
        }

        private static string Pt(ISketchPoint p) { return "(" + (p.X * 1000).ToString("0.###") + ", " + (p.Y * 1000).ToString("0.###") + ")"; }

        // symmetric pairs about a construction centre line on the Y axis (started at the origin, ended ON the top line when there is one: no extra dimension), then horizontal / vertical lines
        private void AddGeometricRelations(IModelDoc2 doc, ISketch sk, ISketchManager sm, JArray applied, JArray skipped, JArray problems)
        {
            var pts = AllSketchPoints(sk);
            var pairs = new List<ISketchPoint[]>();
            foreach (var p in pts)
            {
                if (p.X <= 1e-9) continue;
                foreach (var q in pts) if (Math.Abs(q.X + p.X) < 1e-9 && Math.Abs(q.Y - p.Y) < 1e-9) { pairs.Add(new[] { p, q }); break; }
            }
            ISketchSegment axis = null;
            foreach (var so in (sk.GetSketchSegments() as object[]) ?? new object[0])
            {
                var ln = so as ISketchLine; var seg = so as ISketchSegment; if (ln == null || seg == null || !seg.ConstructionGeometry) continue;
                var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint;
                if (a != null && b != null && Math.Abs(a.X) < 1e-9 && Math.Abs(b.X) < 1e-9) { axis = seg; break; }
            }
            if (pairs.Count > 0 && axis == null)
            {
                // the end of the centre line: the topmost horizontal line that crosses x = 0 (the point is then related to that line instead of being dimensioned)
                ISketchSegment top = null, topCon = null; double topY = double.MinValue, topConY = double.MinValue;
                foreach (var so in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var ln = so as ISketchLine; var seg = so as ISketchSegment; if (ln == null || seg == null) continue;
                    var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint; if (a == null || b == null) continue;
                    if (!(Math.Abs(a.Y - b.Y) < 1e-9 && Math.Min(a.X, b.X) < -1e-9 && Math.Max(a.X, b.X) > 1e-9)) continue;
                    if (seg.ConstructionGeometry) { if (a.Y > topConY) { topConY = a.Y; topCon = seg; } }
                    else if (a.Y > topY) { topY = a.Y; top = seg; }
                }
                // a real top line is preferred; a sketch whose only horizontals across x = 0 are construction lines (an outline rectangle) ends the centre line on the topmost of those
                if (top == null && topCon != null) { top = topCon; topY = topConY; }
                double endY = top != null ? topY : 0.01;
                axis = sm.CreateCenterLine(0, 0, 0, 0, endY, 0) as ISketchSegment;
                if (axis == null) problems.Add("the symmetry centre line could not be created");
                else
                {
                    var cl = (ISketchLine)axis; var s0 = cl.GetStartPoint2() as ISketchPoint; var e0 = cl.GetEndPoint2() as ISketchPoint;
                    TryRelation(doc, sk, "sgVERTICAL2D", "centre line vertical", new object[] { axis }, applied, skipped);
                    if (s0 != null) TieToOrigin(doc, sk, s0, new[] { "sgCOINCIDENT" }, problems);
                    if (top != null && e0 != null) TryRelation(doc, sk, "sgCOINCIDENT", "centre line end on the top line", new object[] { e0, top }, applied, skipped);
                    applied.Add("centre line on the Y axis from the origin (symmetry axis, construction)");
                }
            }
            if (axis != null)
                foreach (var pr in pairs) TryRelation(doc, sk, "sgSYMMETRIC", "symmetric " + Pt(pr[0]) + " / " + Pt(pr[1]) + " about the Y axis", new object[] { pr[0], pr[1], axis }, applied, skipped);
            foreach (var so in (sk.GetSketchSegments() as object[]) ?? new object[0])
            {
                var ln = so as ISketchLine; var seg = so as ISketchSegment; if (ln == null || seg == null || (axis != null && seg == axis)) continue;
                var a = ln.GetStartPoint2() as ISketchPoint; var b = ln.GetEndPoint2() as ISketchPoint; if (a == null || b == null) continue;
                double dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
                if (dy < 1e-9 && dx > 1e-9) TryRelation(doc, sk, "sgHORIZONTAL2D", "horizontal " + Pt(a) + " - " + Pt(b), new object[] { seg }, applied, skipped);
                else if (dx < 1e-9 && dy > 1e-9) TryRelation(doc, sk, "sgVERTICAL2D", "vertical " + Pt(a) + " - " + Pt(b), new object[] { seg }, applied, skipped);
            }
        }

        // the caller's relations: [{"type": ..., "a": [x, y], "b": [x, y], "axis": [x, y]}] in sketch metres; a point is the sketch point nearest to [x, y] (within 20 um), a line / arc the segment nearest to it (within 10 um)
        //   horizontal / vertical (a = line) | fixed (a = point or line) | coincident (a, b points) | on_line (a point, b line) | vertical_points / horizontal_points (a, b points) | midpoint (a point, b line)
        //   equal / collinear / parallel / perpendicular / tangent (a, b segments) | symmetric (a, b points + axis line)
        private void ApplyExplicitRelations(IModelDoc2 doc, ISketch sk, JArray list, JArray applied, JArray skipped)
        {
            foreach (var item in list.OfType<JObject>())
            {
                string type = (item.Value<string>("type") ?? "").Trim().ToLowerInvariant();
                Func<string, ISketchPoint> P = k => { var t = item[k] as JArray; return t != null && t.Count >= 2 ? NearestSketchPoint(sk, t[0].Value<double>(), t[1].Value<double>(), 2e-5) : null; };
                Func<string, ISketchSegment> S = k => { var t = item[k] as JArray; return t != null && t.Count >= 2 ? NearestSketchSegment(sk, t[0].Value<double>(), t[1].Value<double>(), 1e-5) : null; };
                string relation = null; object[] ents = null;
                switch (type)
                {
                    case "horizontal": relation = "sgHORIZONTAL2D"; ents = new object[] { S("a") }; break;
                    case "vertical": relation = "sgVERTICAL2D"; ents = new object[] { S("a") }; break;
                    case "fixed": { object e = (object)P("a") ?? S("a"); relation = "sgFIXED"; ents = new[] { e }; break; }
                    case "coincident": relation = "sgCOINCIDENT"; ents = new object[] { P("a"), P("b") }; break;
                    case "on_line": relation = "sgCOINCIDENT"; ents = new object[] { P("a"), S("b") }; break;
                    case "vertical_points": relation = "sgVERTICALPOINTS2D"; ents = new object[] { P("a"), P("b") }; break;      // measured 2026-10-29: sgVERTICALPOINTS2D takes, sgVERTPOINTS2D is a silent no-op
                    case "horizontal_points": relation = "sgHORIZONTALPOINTS2D"; ents = new object[] { P("a"), P("b") }; break;
                    case "midpoint": relation = "sgATMIDDLE"; ents = new object[] { P("a"), S("b") }; break;
                    case "equal": relation = "sgSAMELENGTH"; ents = new object[] { S("a"), S("b") }; break;
                    case "collinear": relation = "sgCOLINEAR"; ents = new object[] { S("a"), S("b") }; break;
                    case "parallel": relation = "sgPARALLEL"; ents = new object[] { S("a"), S("b") }; break;
                    case "perpendicular": relation = "sgPERPENDICULAR"; ents = new object[] { S("a"), S("b") }; break;
                    case "tangent": relation = "sgTANGENT"; ents = new object[] { S("a"), S("b") }; break;
                    case "symmetric": relation = "sgSYMMETRIC"; ents = new object[] { P("a"), P("b"), S("axis") }; break;
                }
                if (relation == null) { skipped.Add("unknown relation type '" + type + "'"); continue; }
                if (ents.Any(x => x == null)) { skipped.Add(type + " " + item.ToString(Newtonsoft.Json.Formatting.None) + ": an entity was not found at the given coordinates"); continue; }
                TryRelation(doc, sk, relation, type + " " + item.ToString(Newtonsoft.Json.Formatting.None), ents, applied, skipped);
            }
        }

        // the edge (line, arc, circle or spline) nearest to the point over ALL solid and surface (sheet) bodies of the part (analyze_model(edges) and EdgeAtPoint see solid bodies only)
        private IEdge EdgeAtPointAnyBody(IModelDoc2 doc, double[] pt)
        {
            IEdge best = null; double bd = double.MaxValue;
            var part = doc as IPartDoc; if (part == null) return null;
            foreach (int kind in new[] { (int)swBodyType_e.swSolidBody, (int)swBodyType_e.swSheetBody })
            {
                foreach (var bo in (part.GetBodies2(kind, true) as object[]) ?? new object[0])
                    foreach (var eo in ((bo as IBody2)?.GetEdges() as object[]) ?? new object[0])
                    {
                        var e = eo as IEdge; if (e == null) continue;
                        try
                        {
                            var c = e.GetCurve() as ICurve; if (c == null) continue;
                            double d;
                            if (c.IsLine())
                            {
                                var a = ((IVertex)e.GetStartVertex()).GetPoint() as double[]; var b = ((IVertex)e.GetEndVertex()).GetPoint() as double[]; if (a == null || b == null) continue;
                                double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2], l2 = dx * dx + dy * dy + dz * dz; if (l2 < 1e-18) continue;
                                double t = Math.Max(0, Math.Min(1, ((pt[0] - a[0]) * dx + (pt[1] - a[1]) * dy + (pt[2] - a[2]) * dz) / l2));
                                double px = a[0] + t * dx - pt[0], py = a[1] + t * dy - pt[1], pz = a[2] + t * dz - pt[2]; d = Math.Sqrt(px * px + py * py + pz * pz);
                            }
                            else
                            {
                                // an arc, circle or spline: the edge's own closest point (the chord says nothing about where an arc lies; measured 2026-11-05: the old 'straight edges only' rule made every arc EDGE_NOT_FOUND)
                                var cp = e.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; if (cp == null || cp.Length < 3) continue;
                                double px = cp[0] - pt[0], py = cp[1] - pt[1], pz = cp[2] - pt[2]; d = Math.Sqrt(px * px + py * py + pz * pz);
                            }
                            if (d < bd) { bd = d; best = e; }
                        }
                        catch (System.Runtime.InteropServices.COMException) { }
                    }
            }
            return bd < PickTolerance ? best : null;
        }

        private static int RelationCount(ISketch sk)
        {
            try { var rm = sk.RelationManager; var ra = rm?.GetRelations(0) as object[]; return ra?.Length ?? 0; } catch { return 0; }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sketch_convert_entities - the MASTER SHAPE drives the sketch (user rule 2026-10-29: master sketches and the primary driving dimensions come from the SURFACE construction, they are not retyped)
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Convert Entities: the listed edges of the master body (the LOD <= 100 base shape: a solid or a surface) become sketch geometry of the ACTIVE sketch, on-edge related to the master, so a later change of the
        // master's dimensions moves the base flange's profile with it. The converted geometry is defined by the master (no free dimensions): the sketch needs no sketch_fully_define.
        public ExecutionResponse SketchConvertEntities(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                if (!(doc is IPartDoc)) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sketch_convert_entities needs an active part.");
                var sm = doc.SketchManager;
                var sk = sm.ActiveSketch as ISketch;
                if (sk == null) throw new ToolFailure("NO_ACTIVE_SKETCH", "No sketch is active: open the target sketch first (create_sketch on the plane the profile belongs on), then convert the master's edges into it.");
                string sketchName = ((IFeature)sk).Name;
                // `edges` = indices of analyze_model(edges), which lists SOLID bodies only; `points` = a point ON each straight edge, found on solid AND surface bodies (the master is usually a surface)
                var edges = ResolveEdges(doc, p["edges"], null);
                var ptList = AsArray(p["points"], "points");
                if (ptList != null)
                    foreach (var t in ptList)
                    {
                        var pt = ToPoint(t, "each point"); var e = EdgeAtPointAnyBody(doc, pt);
                        if (e == null) throw new ToolFailure("EDGE_NOT_FOUND", "No edge (solid or surface body) within 0.1 mm of (" + string.Join(", ", pt.Select(v => v.ToString("G6")).ToArray()) + ") m.");
                        edges.Add(e);
                    }
                if (edges.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "edges (indices from analyze_model(edges), solid bodies) and / or points ([[x,y,z], ...], a point ON each edge - straight or curved - solid or surface) are required: the master edges to convert.");
                bool chain = p.Value<bool?>("chain") ?? false;                       // chain = true also takes every tangent-connected edge; the caller lists the edges it wants
                int before = ((sk.GetSketchSegments() as object[]) ?? new object[0]).Length;
                var problems = new JArray();

                // inference would snap the converted geometry's neighbours to model edges: off while it runs (restored below)
                bool inference = sm.AutoInference; sm.AutoInference = false;
                bool done = false;
                try
                {
                    doc.ClearSelection2(true);
                    bool all = true, first = true;
                    foreach (var e in edges) { all &= ((IEntity)e).Select4(!first, null); first = false; }
                    if (!all) problems.Add("not every edge could be selected");
                    done = doc.SketchUseEdge2(chain);
                    ExecLog.Write($"sketch_convert_entities: {edges.Count} edge(s) into {sketchName}, chain={chain} -> {done}");
                }
                finally { doc.ClearSelection2(true); sm.AutoInference = inference; }

                var segs = (sk.GetSketchSegments() as object[]) ?? new object[0];
                int created = segs.Length - before;
                var size = SketchExtent(sk);
                int status = sk.GetConstrainedStatus();
                if (!done || created <= 0) problems.Add("SolidWorks converted nothing (SketchUseEdge2 -> " + done + "): check that the edges exist in this part and that the sketch plane faces them");
                var res = new JObject { ["sketch"] = sketchName, ["edges"] = edges.Count, ["created_segments"] = created, ["size"] = new JArray(size[0], size[1]), ["constrained_status"] = status, ["constrained"] = ConstrainedName(status) };
                if (status != 3) problems.Add("the sketch is " + ConstrainedName(status) + ", not fully defined (the converted geometry is defined by the master; whatever else the sketch holds is not)");
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = done && created > 0 && status == 3;
                return res;
            });
        }
    }
}
