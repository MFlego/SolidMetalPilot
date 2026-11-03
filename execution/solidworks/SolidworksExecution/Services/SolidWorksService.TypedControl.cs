// Typed-control tools: read and write the FULL definition of any feature through its *FeatureData interface (with write-verify), plus the sheet-metal operations that are a SEQUENCE of API calls
// (global K-factor / bend allowance / auto-relief overrides, Closed Corners, flat-pattern control + direct flat DXF). Everything here is plain typed SolidWorks API, so the same code runs in both
// hosts; it was found and proven by the in-process probes in SolidPilotBridge/probes (30-38).
// Lengths are METERS and angles RADIANS, exactly like the API (and like the other tools).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private sealed class ToolFailure : Exception
        {
            public readonly string Code;
            public ToolFailure(string code, string message) : base(message) { Code = code; }
        }

        // The shared prologue / epilogue of the typed tools: idempotency, state_version (mutating tools only), COM attach, active document, error mapping, response with CadState.
        private ExecutionResponse Typed(ToolRequest request, bool mutates, Func<IModelDoc2, JObject, JObject> body)
        {
            if (_guard.IsDuplicate(request.OperationId)) return _guard.GetDuplicate(request.OperationId);
            if (mutates && !_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");
            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");
            try
            {
                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (doc == null) return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");
                var result = body(doc, request.Params as JObject ?? new JObject());
                int sv = _guard.GetCurrentStateVersion() + (mutates ? 1 : 0);
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = result.Value<bool?>("verified") ?? true,
                    StateVersion = sv,
                    CadState = new CadState { StateVersion = sv, ActiveDocument = doc.GetTitle(), DocumentType = doc is IPartDoc ? "PART" : doc is IAssemblyDoc ? "ASSEMBLY" : "DRAWING", ActiveSketch = null, Features = new List<string>(), Dimensions = new List<string>() },
                    ResultGeometry = result
                };
                if (mutates) _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (ToolFailure f) { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), f.Code, f.Message); }
            catch (COMException ex) { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message); }
            catch (Exception ex) { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.GetType().Name + ": " + ex.Message); }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // helpers: features, definitions, reflection over the *FeatureData interfaces
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static readonly Dictionary<string, Type> DefinitionByFeatureType = new Dictionary<string, Type>
        {
            { "SheetMetal", typeof(ISheetMetalFeatureData) }, { "SMBaseFlange", typeof(IBaseFlangeFeatureData) }, { "EdgeFlange", typeof(IEdgeFlangeFeatureData) }, { "OneBend", typeof(IOneBendFeatureData) },
            { "FlatPattern", typeof(IFlatPatternFeatureData) }, { "SM3dBend", typeof(ISketchedBendFeatureData) }, { "CornerFeat", typeof(IClosedCornerFeatureData) }, { "SolidToSheetMetal", typeof(IConvertSolidFeatureData) },
            { "UnFold", typeof(IFoldsFeatureData) }, { "Fold", typeof(IFoldsFeatureData) },
            { "Hem", typeof(IHemFeatureData) }, { "BreakCorner", typeof(IBreakCornerFeatureData) }, { "SMMiteredFlange", typeof(IMiterFlangeFeatureData) }
        };

        private static List<IFeature> TopFeatures(IModelDoc2 doc)
        {
            var l = new List<IFeature>(); var f = doc.FirstFeature() as IFeature;
            while (f != null) { l.Add(f); f = f.GetNextFeature() as IFeature; }
            return l;
        }

        private static IFeature FirstOfType(IModelDoc2 doc, string typeName) { return TopFeatures(doc).FirstOrDefault(f => f.GetTypeName2() == typeName); }

        private static bool ImplementsCom(object com, Type iface)
        {
            IntPtr p = IntPtr.Zero;
            try { p = Marshal.GetComInterfaceForObject(com, iface); return p != IntPtr.Zero; }
            catch { return false; }
            finally { if (p != IntPtr.Zero) Marshal.Release(p); }
        }

        private static List<Type> _featureDataInterfaces;
        private static List<Type> FeatureDataInterfaces()
        {
            if (_featureDataInterfaces == null)
                _featureDataInterfaces = typeof(IFeature).Assembly.GetTypes().Where(it => it.IsInterface && it.Name.IndexOf("FeatureData", StringComparison.Ordinal) >= 0).ToList();
            return _featureDataInterfaces;
        }

        // The *FeatureData interface of a definition: the known one for the feature type, else the most specific one of the interop assembly the object answers to.
        private static Type DefinitionInterface(IFeature f, object def)
        {
            Type t;
            if (DefinitionByFeatureType.TryGetValue(f.GetTypeName2(), out t) && ImplementsCom(def, t)) return t;
            Type best = null; int bestScore = -1;
            foreach (var it in FeatureDataInterfaces())
            {
                if (!ImplementsCom(def, it)) continue;
                int score = it.GetProperties().Length + it.GetMethods().Length;
                if (score > bestScore) { best = it; bestScore = score; }
            }
            return best;
        }

        private static bool AccessSelections(Type iface, object def, IModelDoc2 doc)
        {
            var m = iface.GetMethod("AccessSelections");
            if (m == null) return false;
            try { var r = m.Invoke(def, new object[] { doc, null }); return r is bool && (bool)r; } catch { return false; }
        }

        private static void ReleaseSelections(Type iface, object def)
        {
            try { var m = iface.GetMethod("ReleaseSelectionAccess"); if (m != null) m.Invoke(def, null); } catch { }
        }

        private static JToken ValueToJson(object v)
        {
            if (v == null) return JValue.CreateNull();
            var arr = v as Array;
            if (arr != null) { var ja = new JArray(); int n = 0; foreach (var o in arr) { if (n++ >= 50) { ja.Add("..."); break; } ja.Add(ValueToJson(o)); } return ja; }
            if (v is double) { var d = (double)v; return double.IsNaN(d) || double.IsInfinity(d) ? (JToken)new JValue(d.ToString()) : new JValue(d); }
            if (v is bool || v is int || v is long || v is short || v is float || v is string) return new JValue(v);
            if (v.GetType().IsCOMObject)
            {
                var f = v as IFeature; if (f != null) return "feature:" + f.Name;
                if (v is IFace2) return "face"; if (v is IEdge) return "edge"; if (v is ISketch) return "sketch"; return "com-object";
            }
            if (v.GetType().IsEnum) return new JValue((int)v);
            return v.ToString();
        }

        private static JObject DumpProperties(Type iface, object def, bool includeReadOnly)
        {
            var o = new JObject();
            foreach (var p in iface.GetProperties().OrderBy(x => x.Name))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (!p.CanWrite && !includeReadOnly) continue;
                JToken val; try { val = ValueToJson(p.GetValue(def, null)); } catch (Exception e) { val = "!" + (e.InnerException != null ? e.InnerException.Message : e.Message); }
                o[p.Name] = new JObject { ["value"] = val, ["access"] = p.CanWrite ? "rw" : "ro" };
            }
            return o;
        }

        private static bool TryAssign(object def, PropertyInfo pi, JToken val, out string why)
        {
            try
            {
                object o; var pt = pi.PropertyType;
                bool numeric = pt == typeof(double) || pt == typeof(int) || pt == typeof(short);
                if (numeric && val.Type != JTokenType.Integer && val.Type != JTokenType.Float) { why = "expects a number (" + pt.Name + "), got " + val.Type.ToString().ToLowerInvariant() + " " + val.ToString(Newtonsoft.Json.Formatting.None); return false; }
                if (pt == typeof(bool) && val.Type != JTokenType.Boolean) { why = "expects true / false, got " + val.Type.ToString().ToLowerInvariant() + " " + val.ToString(Newtonsoft.Json.Formatting.None); return false; }
                if (pt == typeof(string) && val.Type != JTokenType.String) { why = "expects a string, got " + val.Type.ToString().ToLowerInvariant(); return false; }
                if (pt == typeof(double)) o = val.Value<double>();
                else if (pt == typeof(int)) o = val.Value<int>();
                else if (pt == typeof(bool)) o = val.Value<bool>();
                else if (pt == typeof(string)) o = val.Value<string>();
                else if (pt == typeof(short)) o = val.Value<short>();
                else { why = "unsupported property type " + pt.Name + " (objects and arrays cannot be set from JSON)"; return false; }
                pi.SetValue(def, o, null); why = null; return true;
            }
            catch (Exception ex) { why = ex.InnerException != null ? ex.InnerException.Message : ex.Message; return false; }
        }

        private static bool SameValue(object actual, JToken wanted)
        {
            try
            {
                if (actual is double) return Math.Abs((double)actual - wanted.Value<double>()) <= 1e-9 * Math.Max(1.0, Math.Abs(wanted.Value<double>()));
                if (actual is bool) return (bool)actual == wanted.Value<bool>();
                if (actual is int || actual is short) return Convert.ToInt32(actual) == wanted.Value<int>();
                if (actual is string) return (string)actual == wanted.Value<string>();
            }
            catch { }
            return false;
        }

        private static int FeatureError(IFeature f) { try { bool w; return f.GetErrorCode2(out w); } catch { return 0; } }

        // ---- VIEW-INDEPENDENT picking -------------------------------------------------------------------------------------------------------------------------------------------------
        // IModelDocExtension.SelectByID2(x, y, z) picks THROUGH THE CURRENT VIEW (measured on the plate fixture: in the default orientation a point on the +y edge selected the -x edge and every second
        // append pick failed; in a fitted isometric view it was fine, in a top view one edge was missed). Entities are therefore found by GEOMETRY and selected as objects with Select4.
        private const double PickTolerance = 1e-4;           // 0.1 mm

        private IFace2 FaceAtPoint(IModelDoc2 doc, double[] pt)
        {
            IFace2 best = null; double bd = double.MaxValue;
            foreach (var f in FlattenFaces(doc as IPartDoc))
            {
                var q = f.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; if (q == null || q.Length < 3) continue;
                double d = Math.Sqrt((q[0] - pt[0]) * (q[0] - pt[0]) + (q[1] - pt[1]) * (q[1] - pt[1]) + (q[2] - pt[2]) * (q[2] - pt[2]));
                if (d < bd) { bd = d; best = f; }
            }
            return bd < PickTolerance ? best : null;
        }

        // the straight edge nearest to the point (edges that are not lines are skipped)
        private IEdge EdgeAtPoint(IModelDoc2 doc, double[] pt)
        {
            IEdge best = null; double bd = double.MaxValue;
            foreach (var e in FlattenEdges(doc as IPartDoc))
            {
                try
                {
                    var c = e.GetCurve() as ICurve; if (c == null || !c.IsLine()) continue;
                    var a = ((IVertex)e.GetStartVertex()).GetPoint() as double[]; var b = ((IVertex)e.GetEndVertex()).GetPoint() as double[]; if (a == null || b == null) continue;
                    double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2], l2 = dx * dx + dy * dy + dz * dz; if (l2 < 1e-18) continue;
                    double t = Math.Max(0, Math.Min(1, ((pt[0] - a[0]) * dx + (pt[1] - a[1]) * dy + (pt[2] - a[2]) * dz) / l2));
                    double px = a[0] + t * dx - pt[0], py = a[1] + t * dy - pt[1], pz = a[2] + t * dz - pt[2], d = Math.Sqrt(px * px + py * py + pz * pz);
                    if (d < bd) { bd = d; best = e; }
                }
                catch (COMException) { }
            }
            return bd < PickTolerance ? best : null;
        }

        // JSON arguments arrive as real JSON from the adapter, but a caller posting to the REST facade directly may hand them over as a JSON-encoded string (the MCP boundary
        // convention): accept both, and say so when a string is not JSON instead of treating the argument as absent
        private static JObject AsObject(JToken t, string name)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) { try { return JObject.Parse((string)t); } catch (Exception) { throw new ToolFailure("INVALID_PARAMETER", name + " is a string but not a JSON object."); } }
            var o = t as JObject; if (o == null) throw new ToolFailure("INVALID_PARAMETER", name + " must be a JSON object."); return o;
        }

        private static JArray AsArray(JToken t, string name)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim(); if (s.Length == 0) return null;
                if (s[0] != '[') return new JArray(s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Cast<object>().ToArray());      // "a,b" is accepted for lists of names
                try { return JArray.Parse(s); } catch (Exception) { throw new ToolFailure("INVALID_PARAMETER", name + " is a string but not a JSON array."); }
            }
            var a = t as JArray; if (a == null) throw new ToolFailure("INVALID_PARAMETER", name + " must be a JSON array."); return a;
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // feature_data - the universal typed read / write of a feature definition, with WRITE-VERIFY
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse FeatureData(ToolRequest request)
        {
            var action = ((request.Params as JObject)?.Value<string>("action") ?? "get").ToLowerInvariant();
            return Typed(request, action == "set", (doc, p) =>
            {
                if (action == "list")
                {
                    var arr = new JArray(); bool all = p.Value<bool?>("include_all") ?? false;
                    foreach (var f in TopFeatures(doc))
                    {
                        string tn = f.GetTypeName2(); Type it; DefinitionByFeatureType.TryGetValue(tn, out it);
                        if (!all && (NoiseFeatureTypes.Contains(tn) || tn == "RefPlane" || tn == "OriginProfileFeature")) continue;     // folders / reference geometry: nothing to edit through a *FeatureData interface
                        var item = new JObject { ["name"] = f.Name, ["type"] = tn, ["suppressed"] = f.IsSuppressed(), ["definition"] = it == null ? null : it.Name };
                        int err = FeatureError(f); if (err != 0) item["error"] = err;
                        var subs = new JArray(); var s = f.GetFirstSubFeature() as IFeature;
                        while (s != null && subs.Count < 60) { subs.Add(s.Name + " [" + s.GetTypeName2() + "]"); s = s.GetNextSubFeature() as IFeature; }
                        if (subs.Count > 0) item["sub_features"] = subs;
                        arr.Add(item);
                    }
                    return new JObject { ["feature_count"] = arr.Count, ["features"] = arr };
                }

                string name = p.Value<string>("feature");
                if (string.IsNullOrEmpty(name)) throw new ToolFailure("MISSING_PARAMETER", "feature (the feature name, e.g. 'Edge-Flange1' or 'EdgeBend1') is required for action get / set.");
                var feat = FindFeatureByName(doc, name, null);
                if (feat == null) throw new ToolFailure("FEATURE_NOT_FOUND", "No feature named '" + name + "'. Use action=list for the names.");
                object def = feat.GetDefinition();
                if (def == null) throw new ToolFailure("NO_DEFINITION", "'" + name + "' has no editable definition.");
                // dumping EVERY property of a flat-pattern definition killed SolidWorks (Phase 9 retry, 2026-11-02: `feature_data get Flat-Pattern54` on a part just opened from a file; the four options below are what sheet_metal_params get / flat_pattern_control read safely): a flat pattern reads and writes only those
                var flatDef = def as IFlatPatternFeatureData;
                if (flatDef != null)
                {
                    if (action == "get")
                        return new JObject
                        {
                            ["feature"] = feat.Name, ["type"] = feat.GetTypeName2(), ["definition"] = "IFlatPatternFeatureData",
                            ["note"] = "only the four flat-pattern options are read: dumping every property of this definition crashed SolidWorks",
                            ["properties"] = new JObject
                            {
                                ["CornerTreatment"] = new JObject { ["value"] = flatDef.CornerTreatment, ["access"] = "rw" }, ["SimplifyBends"] = new JObject { ["value"] = flatDef.SimplifyBends, ["access"] = "rw" },
                                ["MergeFace"] = new JObject { ["value"] = flatDef.MergeFace, ["access"] = "rw" }, ["ShowSlitInCornerRelief"] = new JObject { ["value"] = flatDef.ShowSlitInCornerRelief, ["access"] = "rw" },
                            }
                        };
                    var flatProps = AsObject(p["properties"], "properties"); var flatCalls = AsArray(p["calls"], "calls");
                    string[] safe = { "cornertreatment", "simplifybends", "mergeface", "showslitincornerrelief" };
                    if ((flatCalls != null && flatCalls.Count > 0) || (flatProps != null && flatProps.Properties().Any(x => !safe.Contains(x.Name.ToLowerInvariant()))))
                        throw new ToolFailure("UNSAFE_FEATURE", "'" + name + "' is a flat-pattern feature: only CornerTreatment, SimplifyBends, MergeFace and ShowSlitInCornerRelief are written (other properties and method calls on this definition are not safe); use flat_pattern_control for the flat options.");
                }
                var iface = DefinitionInterface(feat, def);
                if (iface == null) throw new ToolFailure("UNKNOWN_DEFINITION", "No *FeatureData interface recognised for feature type '" + feat.GetTypeName2() + "'.");

                if (action == "get")
                {
                    bool ro = p.Value<bool?>("include_readonly") ?? true; bool acc = AccessSelections(iface, def, doc);
                    try { return new JObject { ["feature"] = feat.Name, ["type"] = feat.GetTypeName2(), ["definition"] = iface.Name, ["properties"] = DumpProperties(iface, def, ro) }; }
                    finally { if (acc) ReleaseSelections(iface, def); }
                }
                if (action != "set") throw new ToolFailure("INVALID_PARAMETER", "action must be list, get or set.");

                var props = AsObject(p["properties"], "properties"); var calls = AsArray(p["calls"], "calls"); var cba = AsObject(p["custom_bend_allowance"], "custom_bend_allowance");
                if ((props == null || !props.HasValues) && (calls == null || calls.Count == 0) && cba == null) throw new ToolFailure("MISSING_PARAMETER", "action=set needs properties {Name: value}, calls [{method, args}] and/or custom_bend_allowance.");
                bool access = AccessSelections(iface, def, doc);
                var unknown = new JArray(); var rejected = new JArray(); var requested = new List<KeyValuePair<PropertyInfo, JToken>>();
                var callLog = new JArray(); bool modified;
                try
                {
                    if (calls != null)
                        foreach (var c in calls.OfType<JObject>())
                        {
                            string mname = c.Value<string>("method"); var m = iface.GetMethod(mname ?? "");
                            if (m == null) { callLog.Add(new JObject { ["method"] = mname, ["result"] = "unknown method on " + iface.Name }); continue; }
                            var ps = m.GetParameters(); var args = new object[ps.Length]; var given = c["args"] as JArray;
                            for (int i = 0; i < ps.Length; i++) { var t = ps[i].ParameterType; var tok = given != null && i < given.Count ? given[i] : null; args[i] = tok == null || t.IsByRef ? (t.IsValueType ? Activator.CreateInstance(t.IsByRef ? t.GetElementType() : t) : null) : tok.ToObject(t); }
                            try
                            {
                                var r = m.Invoke(def, args); var entry = new JObject { ["method"] = mname, ["result"] = r == null ? null : ValueToJson(r) };
                                var outs = new JObject(); for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) outs[ps[i].Name] = ValueToJson(args[i]);
                                if (outs.HasValues) entry["out"] = outs;                // e.g. GetUseGaugeTable(out bool, out string)
                                callLog.Add(entry);
                            }
                            catch (Exception e) { callLog.Add(new JObject { ["method"] = mname, ["result"] = "threw " + (e.InnerException != null ? e.InnerException.Message : e.Message) }); }
                        }
                    if (cba != null)
                    {
                        var get = iface.GetMethod("GetCustomBendAllowance"); var set = iface.GetMethod("SetCustomBendAllowance");
                        if (get == null || set == null) throw new ToolFailure("NOT_SUPPORTED", iface.Name + " has no custom bend allowance.");
                        var cur = get.Invoke(def, null) as CustomBendAllowance ?? (CustomBendAllowance)doc.FeatureManager.CreateCustomBendAllowance();
                        ApplyBendAllowance(cur, cba); set.Invoke(def, new object[] { cur });
                    }
                    if (props != null)
                        foreach (var kv in props)
                        {
                            var pi = iface.GetProperties().FirstOrDefault(x => string.Equals(x.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                            if (pi == null) { unknown.Add(kv.Key); continue; }
                            if (!pi.CanWrite) { rejected.Add(new JObject { ["property"] = pi.Name, ["reason"] = "read-only" }); continue; }
                            string why; if (TryAssign(def, pi, kv.Value, out why)) requested.Add(new KeyValuePair<PropertyInfo, JToken>(pi, kv.Value)); else rejected.Add(new JObject { ["property"] = pi.Name, ["reason"] = why });
                        }
                    if (requested.Count == 0 && cba == null && callLog.Count == 0)
                        throw new ToolFailure("NO_VALID_CHANGES", "Nothing could be applied to '" + name + "' (" + iface.Name + "). unknown: " + unknown.ToString(Newtonsoft.Json.Formatting.None) + "  rejected: " + rejected.ToString(Newtonsoft.Json.Formatting.None) + ". action=get lists the property names and which are rw.");
                    modified = feat.ModifyDefinition(def, doc, null);
                }
                finally { if (access) ReleaseSelections(iface, def); }
                if (!modified) throw new ToolFailure("MODIFY_FAILED", "ModifyDefinition returned false for '" + name + "' (feature error " + FeatureError(feat) + ").");

                // WRITE-VERIFY: ModifyDefinition says True even when a value is silently ignored - read the definition back and compare
                var fresh = feat.GetDefinition(); bool acc2 = AccessSelections(iface, fresh, doc);
                var applied = new JArray(); var ignored = new JArray();
                try
                {
                    foreach (var rq in requested)
                    {
                        object actual; try { actual = rq.Key.GetValue(fresh, null); } catch { actual = null; }
                        if (SameValue(actual, rq.Value)) applied.Add(rq.Key.Name);
                        else ignored.Add(new JObject { ["property"] = rq.Key.Name, ["requested"] = rq.Value, ["actual"] = ValueToJson(actual) });
                    }
                }
                finally { if (acc2) ReleaseSelections(iface, fresh); }
                int ferr = FeatureError(feat);
                var res = new JObject { ["feature"] = feat.Name, ["definition"] = iface.Name, ["applied"] = applied, ["ignored"] = ignored, ["unknown"] = unknown, ["rejected"] = rejected, ["feature_error"] = ferr };
                if (callLog.Count > 0) res["calls"] = callLog;
                res["verified"] = ignored.Count == 0 && unknown.Count == 0 && rejected.Count == 0 && ferr == 0;
                if (!(bool)res["verified"])
                {
                    var reasons = new List<string>();
                    if (ignored.Count > 0) reasons.Add("ignored: ModifyDefinition accepted the call but SolidWorks kept its own value - either a precondition is missing (e.g. the sheet-metal bend-allowance / auto-relief OVERRIDE must be on first: sheet_metal_params does that sequence) or the value is fixed / derived for this feature (e.g. a closed hem's gap)");
                    if (unknown.Count > 0) reasons.Add("unknown: no such property on " + iface.Name + " (action=get lists them)");
                    if (rejected.Count > 0) reasons.Add("rejected: read-only or the wrong JSON type");
                    if (ferr != 0) reasons.Add("the feature now reports error " + ferr);
                    res["note"] = string.Join("; ", reasons.ToArray());
                }
                return res;
            });
        }

        private static void ApplyBendAllowance(CustomBendAllowance cba, JObject spec)
        {
            string type = (spec.Value<string>("type") ?? "k_factor").ToLowerInvariant();
            switch (type)
            {
                case "k_factor": case "k": cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor; if (spec["k_factor"] != null) cba.KFactor = spec.Value<double>("k_factor"); break;
                case "direct": case "bend_allowance": cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceDirect; if (spec["bend_allowance"] != null) cba.BendAllowance = spec.Value<double>("bend_allowance"); break;
                case "deduction": case "bend_deduction": cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceDeduction; if (spec["bend_deduction"] != null) cba.BendDeduction = spec.Value<double>("bend_deduction"); break;
                case "table": case "bend_table": cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceBendTable; if (spec["table_file"] != null) cba.BendTableFile = spec.Value<string>("table_file"); break;
                default: throw new ToolFailure("INVALID_PARAMETER", "bend allowance type must be k_factor, direct, deduction or table.");
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sheet_metal_params - the global sheet-metal parameters (get / set), using the sequences that actually take effect
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static string DimsOf(IFeature f)
        {
            var parts = new List<string>();
            try
            {
                var dd = f.GetFirstDisplayDimension() as IDisplayDimension;
                while (dd != null)
                {
                    var d = dd.GetDimension2(0) as IDimension; var v = d == null ? null : d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[];
                    if (d != null) parts.Add(d.FullName + "=" + (v != null && v.Length > 0 ? v[0].ToString("G6") : "?"));
                    dd = f.GetNextDisplayDimension(dd) as IDisplayDimension;
                }
            }
            catch { }
            return string.Join("; ", parts.ToArray());
        }

        // The cut-list items of the part with their SolidWorks-linked sheet-metal properties: the FLAT BLANK size (Bounding Box Length / Width) without unsuppressing the flat pattern, Sheet Metal
        // Thickness, Bend Radius, Bends, Cut Outs, Cutting Length-Outer / -Inner (laser path), Bounding Box Area (-Blank), Bend Allowance (K) ... Values are in the DOCUMENT's length unit.
        private JArray CutListItems(IModelDoc2 doc)
        {
            var items = new JArray(); var seen = new HashSet<string>();            // the item is listed twice by the tree walk (top level and under Solid Bodies)
            foreach (var f in TopFeatures(doc))
            {
                var found = new List<IFeature>();
                if (f.GetTypeName2() == "CutListFolder") found.Add(f);
                var s = f.GetFirstSubFeature() as IFeature;
                while (s != null) { if (s.GetTypeName2() == "CutListFolder") found.Add(s); s = s.GetNextSubFeature() as IFeature; }
                foreach (var cl in found)
                {
                    if (!seen.Add(cl.Name)) continue;
                    var cpm = cl.CustomPropertyManager; var names = cpm == null ? null : cpm.GetNames() as string[]; if (names == null) continue;
                    var props = new JObject();
                    foreach (var n in names)
                    {
                        string val, resolved; bool wasResolved, linkToProp; cpm.Get6(n, false, out val, out resolved, out wasResolved, out linkToProp);
                        double d; props[n] = double.TryParse(resolved, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? (JToken)d : resolved;
                    }
                    items.Add(new JObject { ["name"] = cl.Name, ["properties"] = props });
                }
            }
            return items;
        }

        // the Sheet-Metal feature called `name`, or the first one: a part with several base flanges (several sheet-metal bodies) has ONE Sheet-Metal feature per body, each with its own K-factor / thickness / radius
        private IFeature SheetMetalFeature(IModelDoc2 doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return FirstOfType(doc, "SheetMetal");
            var f = TopFeatures(doc).FirstOrDefault(x => x.GetTypeName2() == "SheetMetal" && x.Name == name);
            if (f == null) throw new ToolFailure("FEATURE_NOT_FOUND", "No Sheet-Metal feature named '" + name + "' (the part has: " + string.Join(", ", TopFeatures(doc).Where(x => x.GetTypeName2() == "SheetMetal").Select(x => x.Name)) + ").");
            return f;
        }

        private JObject SheetMetalSummary(IModelDoc2 doc, string featureName = null)
        {
            var o = new JObject(); var sm = SheetMetalFeature(doc, featureName);
            if (sm == null) { o["sheet_metal"] = false; return o; }
            o["sheet_metal"] = true; o["units"] = ReadUnits(doc);           // the cut-list values below are in THIS length unit (everything else here is meters)
            var d = (ISheetMetalFeatureData)sm.GetDefinition(); bool acc = d.AccessSelections(doc, null);
            try
            {
                var g = new JObject { ["feature"] = sm.Name, ["thickness"] = d.Thickness, ["bend_radius"] = d.BendRadius, ["auto_relief_type"] = d.AutoReliefType, ["use_auto_relief"] = d.UseAutoRelief, ["relief_ratio"] = d.ReliefRatio, ["dimensions"] = DimsOf(sm) };
                var ob = new JObject(); for (int i = 0; i <= 2; i++) { bool b; d.GetOverrideDefaultParameter2(i, out b); ob[new[] { "bend_parameters", "bend_allowance", "auto_relief" }[i]] = b; }
                g["overrides"] = ob; var cba = d.GetCustomBendAllowance();
                if (cba != null) g["bend_allowance"] = new JObject { ["type"] = cba.Type, ["k_factor"] = cba.KFactor, ["bend_allowance"] = cba.BendAllowance, ["bend_deduction"] = cba.BendDeduction };
                o["global"] = g;
            }
            finally { if (acc) d.ReleaseSelectionAccess(); }

            // every Sheet-Metal feature of the part (one per sheet-metal body): the 'global' block above is the one asked for (default: the first)
            var all = new JArray();
            foreach (var f in TopFeatures(doc).Where(x => x.GetTypeName2() == "SheetMetal"))
            {
                var fd = (ISheetMetalFeatureData)f.GetDefinition(); bool facc = fd.AccessSelections(doc, null);
                try { all.Add(new JObject { ["feature"] = f.Name, ["thickness"] = fd.Thickness, ["bend_radius"] = fd.BendRadius, ["k_factor"] = fd.KFactor, ["bend_allowance_type"] = fd.BendAllowanceType }); }
                finally { if (facc) fd.ReleaseSelectionAccess(); }
            }
            if (all.Count > 1) o["sheet_metal_features"] = all;

            var bf = FirstOfType(doc, "SMBaseFlange");
            if (bf != null) { var b = (IBaseFlangeFeatureData)bf.GetDefinition(); o["base_flange"] = new JObject { ["feature"] = bf.Name, ["thickness"] = b.Thickness, ["bend_radius"] = b.BendRadius, ["override_thickness"] = b.OverrideThickness, ["override_radius"] = b.OverrideRadius, ["use_gauge_table"] = b.UseGaugeTable, ["gauge_table"] = b.GaugeTablePath }; }
            var cv = FirstOfType(doc, "SolidToSheetMetal");
            if (cv != null) { var c = (IConvertSolidFeatureData)cv.GetDefinition(); o["convert"] = new JObject { ["feature"] = cv.Name, ["thickness"] = c.SheetThickness, ["bend_radius"] = c.BendRadius, ["rip_gap"] = c.RipGap, ["overlap_ratio"] = c.RipOverlapRatio, ["corner_defaults"] = c.CornerDefaults, ["relief_type"] = c.ReliefType, ["relief_ratio"] = c.ReliefRatio, ["reverse_thickness"] = c.ReverseThickness }; }

            var flanges = new JArray(); var bends = new JArray(); var corners = new JArray();
            foreach (var f in TopFeatures(doc))
            {
                string tn = f.GetTypeName2();
                if (tn == "EdgeFlange") { var e = (IEdgeFlangeFeatureData)f.GetDefinition(); flanges.Add(new JObject { ["feature"] = f.Name, ["length"] = e.OffsetDistance, ["angle"] = e.BendAngle, ["bend_radius"] = e.BendRadius, ["position_type"] = e.PositionType, ["gap"] = e.GapDistance, ["relief_type"] = e.ReliefTearType }); }
                else if (tn == "CornerFeat") { var c = (IClosedCornerFeatureData)f.GetDefinition(); corners.Add(new JObject { ["feature"] = f.Name, ["corner_type"] = c.CornerType, ["gap"] = c.GapDistance, ["overlap_ratio"] = c.OverlapUnderlapRatio, ["open_bend_region"] = c.OpenBendRegion }); }
                var s = f.GetFirstSubFeature() as IFeature;
                while (s != null)
                {
                    if (s.GetTypeName2() == "OneBend" && bends.Count < 200) { var ob = (IOneBendFeatureData)s.GetDefinition(); bends.Add(new JObject { ["feature"] = s.Name, ["angle"] = ob.BendAngle, ["bend_radius"] = ob.BendRadius, ["k_factor"] = ob.KFactor, ["use_default_allowance"] = ob.UseDefaultBendAllowance, ["allowance_type"] = ob.BendAllowanceType }); }
                    s = s.GetNextSubFeature() as IFeature;
                }
            }
            o["edge_flanges"] = flanges; o["bends"] = bends; o["closed_corners"] = corners;
            var cut = CutListItems(doc); if (cut.Count > 0) o["cut_list"] = cut;
            var fp = FirstOfType(doc, "FlatPattern");
            if (fp != null) { var fd = (IFlatPatternFeatureData)fp.GetDefinition(); o["flat_pattern"] = new JObject { ["feature"] = fp.Name, ["suppressed"] = fp.IsSuppressed(), ["corner_treatment"] = fd.CornerTreatment, ["simplify_bends"] = fd.SimplifyBends, ["merge_faces"] = fd.MergeFace, ["show_slit_in_corner_relief"] = fd.ShowSlitInCornerRelief }; }
            return o;
        }

        public ExecutionResponse SheetMetalParams(ToolRequest request)
        {
            var action = ((request.Params as JObject)?.Value<string>("action") ?? "get").ToLowerInvariant();
            return Typed(request, action == "set", (doc, p) =>
            {
                if (!(doc is IPartDoc)) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_params needs an active part.");
                if (FirstOfType(doc, "SheetMetal") == null) throw new ToolFailure("NOT_SHEET_METAL", "The active part has no Sheet-Metal feature.");
                string smName = p.Value<string>("feature");                    // which Sheet-Metal feature (= which sheet-metal body); default the first
                if (action == "get") return SheetMetalSummary(doc, smName);
                if (action != "set") throw new ToolFailure("INVALID_PARAMETER", "action must be get or set.");

                if (!string.IsNullOrEmpty(smName) && (p["thickness"] != null || p["bend_radius"] != null))
                    throw new ToolFailure("INVALID_PARAMETER", "thickness / bend_radius are overrides of a BASE FLANGE and this tool edits the first one: for another sheet-metal body use feature_data on its Base-Flange feature ('Base-Flange2' ...). Nothing was changed.");
                var done = new JArray(); var sm = SheetMetalFeature(doc, smName);
                // 1. global bend allowance (K-factor / direct / deduction) and auto relief - the override sequence, NOT the plain properties (those are silently ignored)
                double? k = p.Value<double?>("k_factor"); string baType = p.Value<string>("bend_allowance_type"); double? baVal = p.Value<double?>("bend_allowance"); double? bdVal = p.Value<double?>("bend_deduction");
                string relief = p.Value<string>("auto_relief"); double? reliefRatio = p.Value<double?>("relief_ratio"); var reset = AsArray(p["reset_overrides"], "reset_overrides");
                if (k.HasValue || baType != null || baVal.HasValue || bdVal.HasValue || relief != null || reliefRatio.HasValue || (reset != null && reset.Count > 0))
                {
                    var d = (ISheetMetalFeatureData)sm.GetDefinition(); bool acc = d.AccessSelections(doc, null);
                    try
                    {
                        // back to the document / material defaults (note: switching the bend-allowance override off removes the D2@Sheet-Metal1 K-factor dimension an equation may drive)
                        if (reset != null)
                            foreach (var rt in reset)
                            {
                                string which = (rt.Type == JTokenType.String ? rt.Value<string>() : "").ToLowerInvariant(); int idx;
                                switch (which)
                                {
                                    case "bend_parameters": idx = (int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_BendParameters; break;
                                    case "bend_allowance": idx = (int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_BendAllowance; break;
                                    case "auto_relief": idx = (int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_AutoRelief; break;
                                    default: throw new ToolFailure("INVALID_PARAMETER", "reset_overrides entries must be bend_parameters, bend_allowance or auto_relief.");
                                }
                                d.SetOverrideDefaultParameter2(idx, false); done.Add("reset:" + which);
                            }
                        if (k.HasValue || baType != null || baVal.HasValue || bdVal.HasValue)
                        {
                            d.SetOverrideDefaultParameter2((int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_BendAllowance, true);
                            var cba = d.GetCustomBendAllowance() ?? (CustomBendAllowance)doc.FeatureManager.CreateCustomBendAllowance();
                            var spec = new JObject { ["type"] = baType ?? (baVal.HasValue ? "direct" : bdVal.HasValue ? "deduction" : "k_factor") };
                            if (k.HasValue) spec["k_factor"] = k.Value; if (baVal.HasValue) spec["bend_allowance"] = baVal.Value; if (bdVal.HasValue) spec["bend_deduction"] = bdVal.Value;
                            ApplyBendAllowance(cba, spec); d.SetCustomBendAllowance(cba); done.Add("bend_allowance");
                        }
                        if (relief != null || reliefRatio.HasValue)
                        {
                            d.SetOverrideDefaultParameter2((int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_AutoRelief, true); d.UseAutoRelief = true;
                            if (relief != null)
                            {
                                switch (relief.ToLowerInvariant())
                                {
                                    case "tear": d.AutoReliefType = (int)swSheetMetalAutoReliefTypes_e.swSheetMetalAutoReliefTypes_e_Tear; break;
                                    case "rectangular": d.AutoReliefType = (int)swSheetMetalAutoReliefTypes_e.swSheetMetalAutoReliefTypes_e_Rectangular; break;
                                    case "obround": d.AutoReliefType = (int)swSheetMetalAutoReliefTypes_e.swSheetMetalAutoReliefTypes_e_Obround; break;
                                    default: throw new ToolFailure("INVALID_PARAMETER", "auto_relief must be tear, rectangular or obround.");
                                }
                            }
                            if (reliefRatio.HasValue) d.ReliefRatio = reliefRatio.Value; done.Add("auto_relief");
                        }
                        if (!sm.ModifyDefinition(d, doc, null)) throw new ToolFailure("MODIFY_FAILED", "ModifyDefinition on '" + sm.Name + "' returned false.");
                    }
                    finally { if (acc) d.ReleaseSelectionAccess(); }
                }

                // 2. thickness / bend radius: the base-flange overrides (or the Convert definition's own overrides)
                double? t = p.Value<double?>("thickness"); double? r = p.Value<double?>("bend_radius");
                if (t.HasValue || r.HasValue)
                {
                    var bf = FirstOfType(doc, "SMBaseFlange"); var cv = FirstOfType(doc, "SolidToSheetMetal");
                    if (bf != null)
                    {
                        var b = (IBaseFlangeFeatureData)bf.GetDefinition(); bool acc = b.AccessSelections(doc, null);
                        try
                        {
                            if (t.HasValue) { b.OverrideThickness = true; b.Thickness = t.Value; }
                            if (r.HasValue) { b.OverrideRadius = true; b.BendRadius = r.Value; }
                            if (!bf.ModifyDefinition(b, doc, null)) throw new ToolFailure("MODIFY_FAILED", "ModifyDefinition on '" + bf.Name + "' returned false.");
                        }
                        finally { if (acc) b.ReleaseSelectionAccess(); }
                        done.Add("thickness_radius(base_flange)");
                    }
                    else if (cv != null)
                    {
                        var c = (IConvertSolidFeatureData)cv.GetDefinition(); bool acc = c.AccessSelections(doc, null);
                        try
                        {
                            if (t.HasValue) { c.OverrideSheetThickness = true; c.SheetThickness = t.Value; }
                            if (r.HasValue) { c.OverrideBendRadius = true; c.BendRadius = r.Value; }
                            if (!cv.ModifyDefinition(c, doc, null)) throw new ToolFailure("MODIFY_FAILED", "ModifyDefinition on '" + cv.Name + "' returned false.");
                        }
                        finally { if (acc) c.ReleaseSelectionAccess(); }
                        done.Add("thickness_radius(convert)");
                    }
                    else throw new ToolFailure("NOT_SUPPORTED", "Neither a base flange nor a Convert feature carries the thickness / radius of this part.");
                }
                if (done.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "Nothing to set: give k_factor / bend_allowance_type + bend_allowance / bend_deduction, auto_relief (+ relief_ratio), thickness and/or bend_radius.");

                // 3. verify against what the API now holds
                var summary = SheetMetalSummary(doc, smName); bool ok = true; var problems = new JArray(); var notes = new JArray();
                var glob = summary["global"] as JObject; var cb = glob == null ? null : glob["bend_allowance"] as JObject;
                if (k.HasValue && (cb == null || Math.Abs(cb.Value<double>("k_factor") - k.Value) > 1e-9)) { ok = false; problems.Add("k_factor did not take effect"); }
                if (relief != null && glob != null && glob.Value<int>("auto_relief_type") != (relief.ToLowerInvariant() == "tear" ? 3 : relief.ToLowerInvariant() == "obround" ? 2 : 1)) { ok = false; problems.Add("auto_relief did not take effect"); }
                if (reliefRatio.HasValue && glob != null)
                {
                    if (glob.Value<int>("auto_relief_type") == (int)swSheetMetalAutoReliefTypes_e.swSheetMetalAutoReliefTypes_e_Obround) notes.Add("an obround auto relief stores no ratio (it reads back 0.0); rectangular and tear keep it");
                    else if (Math.Abs(glob.Value<double>("relief_ratio") - reliefRatio.Value) > 1e-9) { ok = false; problems.Add("relief_ratio did not take effect"); }
                }
                if (reset != null && glob != null)
                {
                    var ov = glob["overrides"] as JObject;
                    foreach (var rt in reset)
                    {
                        string w = rt.Value<string>().ToLowerInvariant();
                        bool alsoSet = (w == "bend_allowance" && (k.HasValue || baType != null || baVal.HasValue || bdVal.HasValue)) || (w == "auto_relief" && (relief != null || reliefRatio.HasValue));
                        if (!alsoSet && ov != null && ov.Value<bool?>(w) == true) { ok = false; problems.Add("override " + w + " is still on"); }
                    }
                }
                if (t.HasValue || r.HasValue)
                {
                    var bb = summary["base_flange"] as JObject ?? summary["convert"] as JObject;
                    if (bb == null || (t.HasValue && Math.Abs(bb.Value<double>("thickness") - t.Value) > 1e-9) || (r.HasValue && Math.Abs(bb.Value<double>("bend_radius") - r.Value) > 1e-9)) { ok = false; problems.Add("thickness / bend_radius did not take effect"); }
                }
                summary["set"] = done; summary["verified"] = ok; if (!ok) summary["problems"] = problems; if (notes.Count > 0) summary["notes"] = notes;
                return summary;
            });
        }

        // The global K-factor, set the only way that takes effect (bend-allowance override + custom allowance on the Sheet-Metal feature). True when the part now reads that K.
        // Does nothing (returns true) when the part already has that K-factor, e.g. the template default.
        private bool ApplyGlobalKFactor(IModelDoc2 doc, double k, IFeature smFeature = null)
        {
            var sm = smFeature ?? FirstOfType(doc, "SheetMetal"); if (sm == null) return false;
            var d = (ISheetMetalFeatureData)sm.GetDefinition(); bool acc = d.AccessSelections(doc, null);
            try
            {
                if (Math.Abs(d.KFactor - k) < 1e-9 && d.BendAllowanceType == (int)swBendAllowanceTypes_e.swBendAllowanceKFactor) return true;
                d.SetOverrideDefaultParameter2((int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_BendAllowance, true);
                var cba = d.GetCustomBendAllowance() ?? (CustomBendAllowance)doc.FeatureManager.CreateCustomBendAllowance();
                cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor; cba.KFactor = k; d.SetCustomBendAllowance(cba);
                if (!sm.ModifyDefinition(d, doc, null)) return false;
            }
            finally { if (acc) d.ReleaseSelectionAccess(); }
            var back = (ISheetMetalFeatureData)sm.GetDefinition(); bool acc2 = back.AccessSelections(doc, null);
            try { return Math.Abs(back.KFactor - k) < 1e-9; } finally { if (acc2) back.ReleaseSelectionAccess(); }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // sheet_metal_corners - Closed Corners through the typed API (no UI), configured as asked
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // a point guaranteed to lie ON the face (closest point to the centre of its box): survives the topology change of the corners made before it
        private static double[] PointOnFace(IFace2 f)
        {
            var box = (double[])f.GetBox(); var c = new[] { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
            var q = f.GetClosestPointOn(c[0], c[1], c[2]) as double[]; return q != null && q.Length >= 3 ? new[] { q[0], q[1], q[2] } : c;
        }

        public ExecutionResponse SheetMetalCorners(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                if (!(doc is IPartDoc)) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_corners needs an active part.");
                if (FirstOfType(doc, "SheetMetal") == null) throw new ToolFailure("NOT_SHEET_METAL", "The active part has no Sheet-Metal feature.");
                string typeName = (p.Value<string>("corner_type") ?? "overlap").ToLowerInvariant(); int ctype;
                switch (typeName) { case "overlap": ctype = (int)swClosedCornerTypes_e.swClosedCornerTypeOverlap; break; case "underlap": ctype = (int)swClosedCornerTypes_e.swClosedCornerTypeUnderlap; break; case "butt": ctype = (int)swClosedCornerTypes_e.swClosedCornerTypeButt; break; default: throw new ToolFailure("INVALID_PARAMETER", "corner_type must be overlap, underlap or butt."); }
                double gap = p.Value<double?>("gap") ?? 0.0000762, ratio = p.Value<double?>("overlap_ratio") ?? 1.0; bool openBend = p.Value<bool?>("open_bend_region") ?? false;

                // targets -> points ON the faces, resolved up front (a face index is only valid until the first corner changes the topology)
                var points = new List<double[]>(); var faces = FlattenFaces(doc as IPartDoc);
                var idx = AsArray(p["faces"], "faces"); if (idx != null) foreach (var t in idx) { int i = t.Value<int>(); if (i < 0 || i >= faces.Count) throw new ToolFailure("FACE_NOT_FOUND", "Face index " + i + " out of range (part has " + faces.Count + " faces). Call analyze_model(faces)."); points.Add(PointOnFace(faces[i])); }
                var pts = AsArray(p["points"], "points"); if (pts != null) foreach (var t in pts) { var a = t as JArray; if (a == null || a.Count < 3) throw new ToolFailure("INVALID_PARAMETER", "points must be [[x,y,z], ...] in meters."); points.Add(new[] { a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>() }); }
                if (points.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "Give faces [indices from analyze_model(faces)] and/or points [[x,y,z]]: the face(s) to EXTEND so they close the corner (one per corner; the end face of the wall, pointing at its neighbour).");

                var created = new JArray(); var failed = new JArray(); int before = FeatureCountOfType(doc, "CornerFeat");
                foreach (var pt in points)
                {
                    var known = new HashSet<string>(TopFeatures(doc).Where(f => f.GetTypeName2() == "CornerFeat").Select(f => f.Name));
                    doc.ClearSelection2(true);
                    var target = FaceAtPoint(doc, pt);     // by geometry, selected as an object: SelectByID2(x,y,z) picks through the current VIEW and can land on another entity or miss
                    if (target == null || !((IEntity)target).Select4(false, null)) { failed.Add(new JObject { ["point"] = new JArray(pt), ["reason"] = "no face at that point" }); continue; }
                    try { doc.InsertSheetMetalClosedCorner(); } catch (Exception e) { failed.Add(new JObject { ["point"] = new JArray(pt), ["reason"] = "InsertSheetMetalClosedCorner threw: " + e.Message }); continue; }
                    var nf = TopFeatures(doc).FirstOrDefault(f => f.GetTypeName2() == "CornerFeat" && !known.Contains(f.Name));
                    if (nf == null) { failed.Add(new JObject { ["point"] = new JArray(pt), ["reason"] = "no Closed Corner feature was created (the face must belong to a wall that can be extended to its neighbour)" }); continue; }
                    var d = (IClosedCornerFeatureData)nf.GetDefinition(); bool acc = d.AccessSelections(doc, null); bool ok;
                    try { d.CornerType = ctype; d.GapDistance = gap; d.OverlapUnderlapRatio = ratio; d.OpenBendRegion = openBend; ok = nf.ModifyDefinition(d, doc, null); } finally { if (acc) d.ReleaseSelectionAccess(); }
                    var back = (IClosedCornerFeatureData)nf.GetDefinition();
                    created.Add(new JObject { ["feature"] = nf.Name, ["configured"] = ok && back.CornerType == ctype && Math.Abs(back.GapDistance - gap) < 1e-9 && Math.Abs(back.OverlapUnderlapRatio - ratio) < 1e-9, ["corner_type"] = back.CornerType, ["gap"] = back.GapDistance, ["overlap_ratio"] = back.OverlapUnderlapRatio, ["feature_error"] = FeatureError(nf) });
                }
                doc.ClearSelection2(true);
                if (created.Count == 0) throw new ToolFailure("NO_CORNER_CREATED", "No Closed Corner was created: " + failed.ToString(Newtonsoft.Json.Formatting.None));
                bool all = failed.Count == 0 && created.All(c => c.Value<bool>("configured") && c.Value<int>("feature_error") == 0);
                return new JObject { ["created"] = created, ["failed"] = failed, ["closed_corners_before"] = before, ["closed_corners_after"] = FeatureCountOfType(doc, "CornerFeat"), ["verified"] = all };
            });
        }

        private static int FeatureCountOfType(IModelDoc2 doc, string typeName) { return TopFeatures(doc).Count(f => f.GetTypeName2() == typeName); }

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // flat_pattern_control - flat-pattern options, the blank's size, and the direct flat DXF
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static JObject BodyStats(IModelDoc2 doc)
        {
            var bodies = (doc as IPartDoc)?.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; var o = new JObject();
            if (bodies == null || bodies.Length == 0) return o;
            var b = (IBody2)bodies[0]; var x = (double[])b.GetBodyBox();
            o["size"] = new JArray(x[3] - x[0], x[4] - x[1], x[5] - x[2]); o["faces"] = b.GetFaceCount(); o["edges"] = b.GetEdgeCount();
            if (bodies.Length > 1)       // several sheet-metal bodies: every body's box (the order of the bodies is SolidWorks' own and can change between calls, so the name is given)
            {
                var all = new JArray();
                foreach (var bo in bodies) { var bb = (IBody2)bo; var bx = (double[])bb.GetBodyBox(); all.Add(new JObject { ["name"] = bb.Name, ["size"] = new JArray(bx[3] - bx[0], bx[4] - bx[1], bx[5] - bx[2]), ["faces"] = bb.GetFaceCount() }); }
                o["bodies"] = all;
            }
            return o;
        }

        public ExecutionResponse FlatPatternControl(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "flat_pattern_control needs an active part.");
                var fp = FirstOfType(doc, "FlatPattern"); if (fp == null) throw new ToolFailure("NOT_SHEET_METAL", "The active part has no Flat-Pattern feature.");
                // a part with several sheet-metal bodies has ONE Flat-Pattern feature per body (Flat-Pattern1, Flat-Pattern13 ...): `feature` picks it, so each body gets its own blank and its own DXF
                var flatNames = TopFeatures(doc).Where(f => f.GetTypeName2() == "FlatPattern").Select(f => f.Name).ToList();
                string fpName = p.Value<string>("feature");
                if (!string.IsNullOrEmpty(fpName))
                {
                    fp = TopFeatures(doc).FirstOrDefault(f => f.GetTypeName2() == "FlatPattern" && f.Name == fpName);
                    if (fp == null) throw new ToolFailure("FEATURE_NOT_FOUND", "No Flat-Pattern feature named '" + fpName + "' (the part has: " + string.Join(", ", flatNames) + ").");
                }
                bool wasSuppressed = fp.IsSuppressed(); bool leaveFlat = p.Value<bool?>("leave_flat") ?? false;
                var restoreSuppressed = new List<string>();          // the other bodies' flat patterns unsuppressed for a multi-body DXF export: suppressed again afterwards
                var res = new JObject { ["folded"] = BodyStats(doc), ["flat_pattern"] = fp.Name };
                if (flatNames.Count > 1) res["flat_patterns"] = new JArray(flatNames);
                fp.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); doc.EditRebuild3();
                try
                {
                    int ferr = FeatureError(fp); if (ferr != 0) throw new ToolFailure("FLAT_PATTERN_ERROR", "The flat pattern does not rebuild (feature error " + ferr + ").");
                    var wanted = new Dictionary<string, JToken>(); foreach (var key in new[] { "corner_treatment", "simplify_bends", "merge_faces", "show_slit_in_corner_relief" }) if (p[key] != null) wanted[key] = p[key];
                    if (wanted.Count > 0)
                    {
                        var d = (IFlatPatternFeatureData)fp.GetDefinition(); bool acc = d.AccessSelections(doc, null);
                        try
                        {
                            if (wanted.ContainsKey("corner_treatment")) d.CornerTreatment = wanted["corner_treatment"].Value<bool>();
                            if (wanted.ContainsKey("simplify_bends")) d.SimplifyBends = wanted["simplify_bends"].Value<bool>();
                            if (wanted.ContainsKey("merge_faces")) d.MergeFace = wanted["merge_faces"].Value<bool>();
                            if (wanted.ContainsKey("show_slit_in_corner_relief")) d.ShowSlitInCornerRelief = wanted["show_slit_in_corner_relief"].Value<bool>();
                            if (!fp.ModifyDefinition(d, doc, null)) throw new ToolFailure("MODIFY_FAILED", "ModifyDefinition on the flat pattern returned false.");
                        }
                        finally { if (acc) d.ReleaseSelectionAccess(); }
                    }
                    var cur = (IFlatPatternFeatureData)fp.GetDefinition();
                    res["options"] = new JObject { ["corner_treatment"] = cur.CornerTreatment, ["simplify_bends"] = cur.SimplifyBends, ["merge_faces"] = cur.MergeFace, ["show_slit_in_corner_relief"] = cur.ShowSlitInCornerRelief };
                    bool verified = true;
                    if (wanted.ContainsKey("corner_treatment") && cur.CornerTreatment != wanted["corner_treatment"].Value<bool>()) verified = false;
                    if (wanted.ContainsKey("merge_faces") && cur.MergeFace != wanted["merge_faces"].Value<bool>()) verified = false;
                    res["flat"] = BodyStats(doc);

                    string dxf = p.Value<string>("dxf_path");
                    if (!string.IsNullOrEmpty(dxf))
                    {
                        string model = doc.GetPathName();
                        if (string.IsNullOrEmpty(model)) throw new ToolFailure("SAVE_REQUIRED", "The DXF export needs the part's file path: save the part first (save_document).");
                        int flags = p.Value<int?>("dxf_flags") ?? 5;     // 1 = outline, 4 = bend lines, 5 = both
                        string dir = Path.GetDirectoryName(Path.GetFullPath(dxf)); string stem = Path.GetFileNameWithoutExtension(dxf);
                        Directory.CreateDirectory(dir); if (File.Exists(dxf)) File.Delete(dxf);
                        if (flatNames.Count <= 1)
                        {
                            bool ok = part.ExportToDWG2(dxf, model, 1, true, null, false, false, flags, null);
                            bool exists = File.Exists(dxf);
                            res["dxf"] = new JObject { ["path"] = dxf, ["flags"] = flags, ["written"] = ok && exists, ["bytes"] = exists ? new FileInfo(dxf).Length : 0 };
                            if (!(ok && exists)) verified = false;
                        }
                        else
                        {
                            // SEVERAL sheet-metal bodies: a single-file export writes only the FIRST body's flat (measured 2026-10-28: the second DXF was the first body's again, whichever flat pattern was active).
                            // With every flat pattern unsuppressed and ExportToSingleFile = false SolidWorks writes ONE FILE PER BODY, named "<Flat-Pattern feature> - <stem>.dxf".
                            foreach (var f2 in TopFeatures(doc).Where(f => f.GetTypeName2() == "FlatPattern" && f.IsSuppressed())) { f2.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); restoreSuppressed.Add(f2.Name); }
                            doc.EditRebuild3();
                            var before = new HashSet<string>(Directory.GetFiles(dir, "* - " + stem + ".dxf"));
                            foreach (var old in before) File.Delete(old);
                            bool ok = part.ExportToDWG2(dxf, model, 1, false, null, false, false, flags, null);
                            var written = new JArray();
                            foreach (var fn in flatNames) { string fpath = Path.Combine(dir, fn + " - " + stem + ".dxf"); if (File.Exists(fpath)) written.Add(new JObject { ["flat_pattern"] = fn, ["path"] = fpath, ["bytes"] = new FileInfo(fpath).Length }); }
                            res["dxf"] = new JObject { ["path"] = dxf, ["flags"] = flags, ["written"] = ok && written.Count == flatNames.Count, ["files"] = written };
                            if (!(ok && written.Count == flatNames.Count)) verified = false;
                        }
                    }
                    res["verified"] = verified;
                }
                finally
                {
                    // the model is folded again unless the caller asked to leave it flat - whatever state it was in when we started, and also after a failure above
                    foreach (var rn in restoreSuppressed)
                    {
                        var rf = TopFeatures(doc).FirstOrDefault(f => f.Name == rn && f.GetTypeName2() == "FlatPattern");
                        if (rf != null && (rf.Name != fp.Name || !leaveFlat)) rf.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    }
                    if (!leaveFlat) { fp.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); }
                    if (restoreSuppressed.Count > 0 || !leaveFlat) doc.EditRebuild3();
                    res["flat_pattern_was"] = wasSuppressed ? "suppressed" : "active";
                    res["flat_pattern_left"] = leaveFlat ? "active" : "suppressed";
                }
                return res;
            });
        }
    }
}
