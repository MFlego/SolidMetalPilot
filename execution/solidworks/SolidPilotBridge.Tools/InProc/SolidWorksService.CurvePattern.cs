// curve_pattern: a CURVE-DRIVEN PATTERN of whole BODIES (sheet-metal bodies included) along a sketch / 3D sketch / helix / edge, driven through SolidWorks' own Curve Driven Pattern PropertyManager.
// WHY IN PROCESS AND THROUGH THE USER INTERFACE (measured 2026-11-12, round 7): the API route - CreateDefinition(swFmCurvePattern) + AccessSelections - KILLS SolidWorks (round 5 twice, round 7 once:
// the log stopped at AccessSelections of a fresh definition). The PropertyManager (RunCommand swCommands_CurveDrivenPattern = 362) takes the same input without a crash and accepts a SHEET-METAL
// body in its Bodies list: 12 identical sheet-metal bodies in ONE cut-list item (quantity 12), each with its own flat pattern. The Mirror PropertyManager, in contrast, refuses sheet-metal bodies.
// Recipe the tool follows (the PM is a classic Win32 one; ids measured with probe 73):
//   1. the curve is PRESELECTED (a feature = sketch / 3D sketch / helix, selection mark 1; or an edge by a point on it)
//   2. RunCommand(362): the PM opens with Direction 1 = the curve; its controls: 5540 instances, 5539 spacing, 5557 equal spacing, 5600 / 5601 transform / offset curve, 4646 / 4648 tangent to curve /
//      align to seed, 2557 face normal (offset curve), 53406 the Bodies section header, 2656 the Bodies list, 4900 OK, 4903 Cancel
//   3. the Bodies list is activated by a posted click, then each body is selected with SelectData mark 256 (Select2 answers false for any other mark or when the list is not active)
//   4. the PM must be left alone for a moment so that SolidWorks' idle loop builds the preview (OK pressed inside the same call ended with 'Please select edge or sketch segment to define the
//      direction' and an emptied PM): the tool has two stages, prepare and finish, and the adapter makes the two calls with a pause between them
//   5. OK is a click on the check mark (id 4900); the PM is gone when the pattern is built. The numbers cannot be typed into the PM (WM_SETTEXT and EM_REPLACESEL + Enter leave the callout at 12 /
//      10 mm): the pattern is built with the defaults and its own dimensions D1 (instances) and D3 (spacing) are set afterwards.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        private const int CurveDrivenPatternCommand = 362;
        private const int PmIdCount = 5540, PmIdSpacing = 5539, PmIdEqual = 5557, PmIdTransform = 5600, PmIdOffset = 5601, PmIdTangent = 4646, PmIdSeed = 4648, PmIdBodiesHeader = 53406, PmIdBodiesList = 2656, PmIdOk = 4900, PmIdCancel = 4903, PmIdFaceNormal = 2557;

        private delegate bool CpEnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, CpEnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out CpRect r);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct CpRect { public int L, T, R, B; }

        private sealed class PmCtl { public IntPtr H; public string Cls, Text; public int Id; public bool Visible, Enabled; public CpRect R; }

        private List<PmCtl> PmControls()
        {
            var list = new List<PmCtl>(); var frame = new IntPtr(_solidWorks.IFrameObject().GetHWnd());
            EnumChildWindows(frame, (h, l) =>
            {
                var c = new StringBuilder(80); GetClassName(h, c, 80); var t = new StringBuilder(160); GetWindowText(h, t, 160); CpRect r; GetWindowRect(h, out r);
                list.Add(new PmCtl { H = h, Cls = c.ToString(), Text = t.ToString(), Id = GetDlgCtrlID(h), Visible = IsWindowVisible(h), Enabled = IsWindowEnabled(h), R = r });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static string CpTitle = "CurveDrivenPattern";
        private bool PmOpen() { return PmControls().Any(c => c.Cls == "Button" && c.Id == 99 && c.Visible && c.Text.Replace(" ", "") == CpTitle); }
        private PmCtl PmFind(int id, string cls) { return PmControls().FirstOrDefault(c => c.Id == id && c.Cls == cls && c.Visible); }
        private PmCtl PmNeed(int id, string cls, string what) { var c = PmFind(id, cls); if (c == null) throw new ToolFailure("PM_CONTROL_NOT_FOUND", "the Curve Driven Pattern PropertyManager has no visible " + what + " (id " + id + ", " + cls + ")."); return c; }
        private void PmClickButton(PmCtl b) { SendMessage(b.H, 0x00F5, IntPtr.Zero, IntPtr.Zero); PmUi.Pump(150); }                        // BM_CLICK
        private bool PmChecked(PmCtl b) { return SendMessage(b.H, 0x00F0, IntPtr.Zero, IntPtr.Zero).ToInt32() == 1; }                  // BM_GETCHECK
        private string StatusBarText() { var s = PmControls().FirstOrDefault(c => c.Cls == "msctls_statusbar32"); return s == null ? "" : s.Text; }
        private void PmCancel() { try { var c = PmFind(PmIdCancel, "Button"); if (c != null) PmClickButton(c); } catch (Exception) { } }

        public ExecutionResponse CurvePattern(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "curve_pattern needs an active part.");
                string stage = (p.Value<string>("stage") ?? "all").Trim().ToLowerInvariant(); if (stage != "prepare" && stage != "finish" && stage != "all") throw new ToolFailure("INVALID_PARAMETER", "stage is 'prepare', 'finish' or 'all'.");
                var bodyArr = AsArray(p["bodies"], "bodies"); if (bodyArr == null || bodyArr.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "bodies = a JSON array of the body names to pattern (the SEED bodies, sheet metal included).");
                var names = bodyArr.Select(t => t.ToString()).ToList();
                int count = p.Value<int?>("count") ?? 0; if (count < 2) throw new ToolFailure("MISSING_PARAMETER", "count (>= 2): the instances INCLUDING the seed.");
                bool equal = p.Value<bool?>("equal_spacing") ?? false; double spacing = p.Value<double?>("spacing") ?? 0; if (!equal && spacing <= 0) throw new ToolFailure("MISSING_PARAMETER", "spacing (meters, > 0 along the curve) or equal_spacing = true.");
                string method = (p.Value<string>("curve_method") ?? "transform").Trim().ToLowerInvariant(), align = (p.Value<string>("alignment") ?? "tangent").Trim().ToLowerInvariant();
                if (method != "transform" && method != "offset") throw new ToolFailure("INVALID_PARAMETER", "curve_method is 'transform' (default) or 'offset'.");
                if (align != "tangent" && align != "seed") throw new ToolFailure("INVALID_PARAMETER", "alignment is 'tangent' (default, tangent to the curve) or 'seed' (align to seed).");
                var problems = new JArray(); var res = new JObject(); bool keep = p.Value<bool?>("keep_open") ?? false;
                Func<Dictionary<string, Body2>> bodiesNow = () =>
                {
                    var d = new Dictionary<string, Body2>();
                    foreach (int t in new[] { (int)swBodyType_e.swSolidBody, (int)swBodyType_e.swSheetBody })
                        foreach (var o in (part.GetBodies2(t, false) as object[]) ?? new object[0]) { var b = o as Body2; if (b != null) d[b.Name] = b; }
                    return d;
                };
                var sm = (ISelectionMgr)doc.SelectionManager;

                if (stage == "prepare" || stage == "all")
                {
                    string curveName = p.Value<string>("curve"); var curvePt = p["curve_point"] as JArray;
                    if (string.IsNullOrWhiteSpace(curveName) && (curvePt == null || curvePt.Count != 3)) throw new ToolFailure("MISSING_PARAMETER", "curve = the NAME of the sketch / 3D sketch / helix feature that drives the pattern (e.g. '3DSketch1'), or curve_point = [x,y,z] (meters) ON an edge.");
                    if (PmOpen()) throw new ToolFailure("PM_ALREADY_OPEN", "the Curve Driven Pattern PropertyManager is already open: cancel it first.");
                    var before0 = bodiesNow(); foreach (var n in names) if (!before0.ContainsKey(n)) throw new ToolFailure("BODY_NOT_FOUND", "no body named '" + n + "'. Bodies: " + string.Join(", ", before0.Keys.Take(30)) + ".");
                    res["bodies_before_names"] = new JArray(before0.Keys.OrderBy(k => k));
                    doc.ClearSelection2(true); var sd = sm.CreateSelectData(); sd.Mark = 0; bool picked = false;
                    if (!string.IsNullOrWhiteSpace(curveName))
                    {
                        var f = FindFeatureByName(doc, curveName, null); if (f == null) throw new ToolFailure("CURVE_NOT_FOUND", "no feature named '" + curveName + "'.");
                        picked = f.Select2(false, 0);
                    }
                    else
                    {
                        var e = EdgeAtPointAnyBody(doc, new[] { curvePt[0].Value<double>(), curvePt[1].Value<double>(), curvePt[2].Value<double>() });
                        if (e == null) throw new ToolFailure("EDGE_NOT_FOUND", "no edge within 0.1 mm of curve_point.");
                        picked = ((IEntity)e).Select4(false, sd);
                    }
                    if (!picked) throw new ToolFailure("SELECTION_FAILED", "the curve could not be selected.");
                    ExecLog.Write("curve_pattern: curve '" + (curveName ?? "edge") + "' selected; RunCommand " + CurveDrivenPatternCommand);
                    if (!_solidWorks.RunCommand(CurveDrivenPatternCommand, CpTitle)) throw new ToolFailure("COMMAND_FAILED", "SolidWorks refused to start the Curve Driven Pattern command.");
                    if (!PmUi.PumpUntil(PmOpen, 10000)) throw new ToolFailure("PM_NOT_OPEN", "the Curve Driven Pattern PropertyManager did not open.");
                    PmUi.Pump(1500);
                    try
                    {
                        if (equal) { var eq = PmNeed(PmIdEqual, "Button", "Equal spacing check box"); if (eq.Enabled && !PmChecked(eq)) PmClickButton(eq); }
                        if (method == "offset") PmClickButton(PmNeed(PmIdOffset, "Button", "offset curve radio"));
                        if (align == "seed") PmClickButton(PmNeed(PmIdSeed, "Button", "align to seed radio"));
                        var fnPt = p["face_normal_point"] as JArray;
                        if (fnPt != null && fnPt.Count == 3)
                        {
                            var list = PmFind(PmIdFaceNormal, "ListBox");
                            if (list == null || !list.Enabled) problems.Add("the Face normal list is disabled (it works with the offset curve method)");
                            else
                            {
                                PmUi.Click((list.R.L + list.R.R) / 2, (list.R.T + list.R.B) / 2, false);
                                bool done = false; var fpt = new[] { fnPt[0].Value<double>(), fnPt[1].Value<double>(), fnPt[2].Value<double>() };
                                foreach (int mk in new[] { 0, 2, 4, 8, 16, 32, 64, 128 })
                                {
                                    var fsd = sm.CreateSelectData(); fsd.Mark = mk; int n0 = sm.GetSelectedObjectCount2(-1);
                                    var face = FaceAtPointAnyBody(doc, fpt); if (face == null) { problems.Add("no face at face_normal_point"); break; }
                                    if (((IEntity)face).Select4(true, fsd) && sm.GetSelectedObjectCount2(-1) > n0) { done = true; res["face_normal_mark"] = mk; break; }
                                }
                                if (!done) problems.Add("the face normal could not be selected");
                            }
                        }
                        if (PmFind(PmIdBodiesList, "ListBox") == null) { PmClickButton(PmNeed(PmIdBodiesHeader, "Button", "Bodies section header")); PmUi.Pump(300); }
                        var bl = PmNeed(PmIdBodiesList, "ListBox", "Bodies list");
                        PmUi.Click((bl.R.L + bl.R.R) / 2, (bl.R.T + bl.R.B) / 2, false);
                        int taken = 0;
                        foreach (var n in names)
                        {
                            var bsd = sm.CreateSelectData(); bsd.Mark = 256; var b = bodiesNow()[n];
                            if (b.Select2(true, bsd)) taken++; else problems.Add("SolidWorks did not take body '" + n + "' into the Bodies list");
                            PmUi.Pump(100);
                        }
                        if (taken == 0) throw new ToolFailure("SELECTION_FAILED", "no body was taken into the Bodies list: " + problems.ToString(Newtonsoft.Json.Formatting.None));
                        res["bodies_in_pm"] = taken;
                    }
                    catch { if (!keep) PmCancel(); doc.ClearSelection2(true); throw; }
                    if (problems.Count > 0) res["problems"] = problems;
                    if (stage == "prepare") { res["stage"] = "prepared: the PropertyManager is open; call again with stage='finish'"; return res; }
                    PmUi.Pump(3000);
                }

                // ---- finish
                if (!PmOpen()) throw new ToolFailure("PM_NOT_OPEN", "the Curve Driven Pattern PropertyManager is not open: run stage='prepare' first.");
                var beforeNames = new HashSet<string>(); var bn = p["bodies_before_names"] as JArray;
                if (bn != null) foreach (var t in bn) beforeNames.Add(t.ToString()); else foreach (var k in bodiesNow().Keys) beforeNames.Add(k);
                try
                {
                    var okb = PmNeed(PmIdOk, "Button", "OK button"); PmUi.Click((okb.R.L + okb.R.R) / 2, (okb.R.T + okb.R.B) / 2, false);
                    if (!PmUi.PumpUntil(() => !PmOpen(), 30000))
                    {
                        string status = StatusBarText();
                        if (!keep) PmCancel();
                        throw new ToolFailure("PATTERN_FAILED", "the PropertyManager stayed open after OK (status bar: '" + status + "'): it was cancelled.");
                    }
                }
                catch { if (!keep) PmCancel(); doc.ClearSelection2(true); throw; }
                doc.ClearSelection2(true); PmUi.Pump(500);
                try { doc.EditRebuild3(); } catch (Exception) { }

                // the count and the spacing: the pattern's own dimensions (the PM could not take typed numbers)
                IFeature made = null; { var f0 = doc.IFirstFeature(); while (f0 != null) { if (f0.GetTypeName2() == "CurvePattern") made = f0; f0 = f0.IGetNextFeature(); } }
                if (made != null)
                {
                    var dd = made.GetFirstDisplayDimension() as IDisplayDimension; var dimNames = new List<string>(); int guard = 0;
                    while (dd != null && guard++ < 12)
                    {
                        var dim = dd.GetDimension2(0);
                        if (dim != null)
                        {
                            string full = dim.FullName ?? dim.Name; dimNames.Add(full);
                            if (full.StartsWith("D1@")) dim.SetSystemValue3(count, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                            else if (full.StartsWith("D3@") && !equal) dim.SetSystemValue3(spacing, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                        }
                        dd = made.GetNextDisplayDimension(dd) as IDisplayDimension;
                    }
                    res["pattern_dimensions"] = new JArray(dimNames);
                    try { doc.EditRebuild3(); } catch (Exception) { }
                }

                // read back: the new bodies and the pattern feature
                var after = bodiesNow(); var added = after.Keys.Where(k => !beforeNames.Contains(k)).OrderBy(k => k).ToList();
                res["feature"] = made == null ? null : made.Name; res["seeds"] = new JArray(names); res["new_bodies"] = new JArray(added); res["bodies_before"] = beforeNames.Count; res["bodies_after"] = after.Count;
                res["count"] = count; res["spacing_m"] = equal ? (double?)null : spacing; res["equal_spacing"] = equal; res["curve_method"] = method; res["alignment"] = align;
                int expected = names.Count * (count - 1);
                if (added.Count != expected) problems.Add(added.Count + " new bodies, " + expected + " expected (" + names.Count + " seed(s) x " + (count - 1) + ")");
                if (made == null) problems.Add("no CurvePattern feature found in the tree");
                else { int err = FeatureError(made); if (err != 0) problems.Add("the feature reports rebuild error " + err); }
                int sheets = 0; foreach (var k in added) { try { if (after[k].IsSheetMetal()) sheets++; } catch (Exception) { } }
                res["new_sheet_metal_bodies"] = sheets;
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = problems.Count == 0;
                return res;
            });
        }

        // the planar or curved face nearest to a point over solid and surface bodies
        private IFace2 FaceAtPointAnyBody(IModelDoc2 doc, double[] pt)
        {
            IFace2 best = null; double bd = double.MaxValue; var part = doc as IPartDoc; if (part == null) return null;
            foreach (int kind in new[] { (int)swBodyType_e.swSolidBody, (int)swBodyType_e.swSheetBody })
                foreach (var bo in (part.GetBodies2(kind, true) as object[]) ?? new object[0])
                    foreach (var fo in ((bo as IBody2)?.GetFaces() as object[]) ?? new object[0])
                    {
                        var f = fo as IFace2; if (f == null) continue;
                        try { var cp = f.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; if (cp == null || cp.Length < 3) continue; double d = Math.Sqrt((cp[0] - pt[0]) * (cp[0] - pt[0]) + (cp[1] - pt[1]) * (cp[1] - pt[1]) + (cp[2] - pt[2]) * (cp[2] - pt[2])); if (d < bd) { bd = d; best = f; } }
                        catch (COMException) { }
                    }
            return bd < 1e-4 ? best : null;
        }
    }
}


