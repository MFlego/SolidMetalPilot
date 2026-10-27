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
            try
            {
                var dd = doc.AddDimension2(labelX, labelY, 0) as IDisplayDimension; if (dd == null) return null;
                var d = dd.GetDimension2(0) as IDimension; if (d == null) return null;
                d.SystemValue = value;
                if (Math.Abs(d.SystemValue - value) > 1e-6) return null;                  // a driven (reference) dimension, or a value SolidWorks refused: do not report it as a dimension that defines the sketch
                string full = d.FullName ?? ""; int at = full.IndexOf('@');
                return at > 0 && full.IndexOf('@', at + 1) > 0 ? full.Substring(0, full.IndexOf('@', at + 1)) : full;       // "D1@Sketch1@part.Part" -> "D1@Sketch1"
            }
            finally { _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, prev); doc.ClearSelection2(true); }
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
                if (w <= 0 || h <= 0) throw new ToolFailure("MISSING_PARAMETER", "width and height (meters, > 0) are required: the rectangle's X and Y extent, centred on the sketch origin.");
                string plane = p.Value<string>("plane") ?? "Front Plane";

                doc.ClearSelection2(true);
                if (!doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0)) throw new ToolFailure("PLANE_NOT_FOUND", "No plane named '" + plane + "'.");     // by NAME: no view involved
                doc.SketchManager.InsertSketch(true);
                var sk = doc.SketchManager.ActiveSketch as ISketch; if (sk == null) throw new ToolFailure("SKETCH_FAILED", "InsertSketch did not open a sketch on '" + plane + "'.");
                string sketchName = ((IFeature)sk).Name;

                // with sketch inference ON (the SolidWorks default) the corners snap to model edges nearby and are held there: beside the cabinet a 0.30 x 0.45 m rectangle came out 0.2987 x 0.4492 and
                // already "fully defined" by those relations, so the D1 / D2 dimensions below were driven (reference) ones whose values did nothing
                var sm = doc.SketchManager; bool inference = sm.AutoInference; sm.AutoInference = false;
                try { sm.CreateCenterRectangle(0, 0, 0, w / 2, h / 2, 0); } finally { sm.AutoInference = inference; }
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
                string dw = DimensionSegment(doc, (ISketchSegment)top, w, 0, h / 2 + 0.02, sketchName); if (dw != null) names.Add(dw); else problems.Add("the width dimension could not be created, or did not take the value");
                string dh = DimensionSegment(doc, (ISketchSegment)right, h, w / 2 + 0.02, 0, sketchName); if (dh != null) names.Add(dh); else problems.Add("the height dimension could not be created, or did not take the value");

                // tie the centre to the sketch origin when the sketch still has freedom (the centre point is created next to the origin, not always coincident with it)
                int status = sk.GetConstrainedStatus();
                if (status != 3)
                {
                    ISketchPoint centre = null;
                    foreach (var o in (sk.GetSketchPoints2() as object[]) ?? new object[0]) { var pt = o as ISketchPoint; if (pt != null && Math.Abs(pt.X) < 1e-9 && Math.Abs(pt.Y) < 1e-9) { centre = pt; break; } }
                    if (centre != null)
                    {
                        doc.ClearSelection2(true);
                        bool a = centre.Select4(false, null); bool b = doc.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, true, 0, null, 0);
                        if (a && b) doc.SketchAddConstraints("sgCOINCIDENT"); else problems.Add("the centre could not be tied to the origin (selected " + a + " / " + b + ")");
                        doc.ClearSelection2(true); status = sk.GetConstrainedStatus();
                    }
                    else problems.Add("no centre point found to tie to the origin");
                }
                var size = SketchExtent(sk);                                      // what the lines really measure, not what was asked
                if (Math.Abs(size[0] - w) > 1e-6 || Math.Abs(size[1] - h) > 1e-6) problems.Add("the rectangle measures " + size[0].ToString("0.######") + " x " + size[1].ToString("0.######") + " m, not " + w.ToString("0.######") + " x " + h.ToString("0.######") + " m: SolidWorks related it to other geometry");
                var res = new JObject { ["sketch"] = sketchName, ["plane"] = plane, ["width"] = w, ["height"] = h, ["size"] = new JArray(size[0], size[1]), ["dimensions"] = names, ["constrained_status"] = status, ["constrained"] = ConstrainedName(status) };
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = status == 3 && problems.Count == 0;
                return res;
            });
        }
    }
}
