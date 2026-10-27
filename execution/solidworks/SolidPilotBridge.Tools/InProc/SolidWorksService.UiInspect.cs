// ui_inspect: SolidWorks' own user interface seen from INSIDE the process - the diagnostic and exploration face of PmUi. SolidWorks 2026 builds several PropertyManagers (Convert to Sheet Metal among
// them) as WPF controls hosted in an HwndSource; in-process their visual trees are plain objects. In-process-only (the out-of-process execution layer cannot offer it).
//   action = windows      every visible WPF-hosted window under the SolidWorks frame
//   action = tree         the visual tree under one of them (root = title fragment or hex handle; from = start at the element with that x:Name), flattened, depth limited
//   action = act          drive one control: name (+ within, nth), op = expand | collapse | focus | toggle | invoke | set_text | select_index, value
//   action = view         show a named view and fit it (name = "*Back" ...)
//   action = project      model points [[x, y, z], ...] (m) -> screen pixels in the active view
//   action = window_at    which window is at a screen pixel (sx, sy) - what a posted click would reach
//   action = post_click   post a genuine click to the window at the pixel (sx, sy, ctrl)
//   action = selection    the selection manager's list: type, mark and what it holds
// NOTE: while a PropertyManager is open, SolidWorks cancels it when the gate's automatic snapshot (SaveAs copy) runs before a call; switch the policy off (pipe: snapshot_policy off) for a session
// of ui_inspect calls. The tools that drive a PropertyManager themselves (sheet_metal_convert) do all of it inside ONE call, after the snapshot.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        private static JObject DescribeSelected(ISelectionMgr sm, int i)
        {
            int type = sm.GetSelectedObjectType3(i, -1); var o = new JObject { ["i"] = i, ["type"] = type, ["mark"] = sm.GetSelectedObjectMark(i) };
            object obj = null; try { obj = sm.GetSelectedObject6(i, -1); } catch { }
            var edge = obj as IEdge; var face = obj as IFace2; var feat = obj as IFeature;
            try
            {
                if (edge != null)
                {
                    var cv = edge.GetCurve() as ICurve;
                    if (cv != null)
                    {
                        double s, e; bool closed, periodic; cv.GetEndParams(out s, out e, out closed, out periodic);
                        var mid = cv.Evaluate2((s + e) / 2, 0) as double[]; if (mid != null) o["edge_mid"] = new JArray(mid[0], mid[1], mid[2]);
                    }
                }
                else if (face != null) { var bx = face.GetBox() as double[]; if (bx != null) o["face_centre"] = new JArray((bx[0] + bx[3]) / 2, (bx[1] + bx[4]) / 2, (bx[2] + bx[5]) / 2); }
                else if (feat != null) o["feature"] = feat.Name;
            }
            catch { }
            return o;
        }

        public ExecutionResponse UiInspect(ToolRequest request)
        {
            return Typed(request, false, (doc, p) =>
            {
                string action = (p.Value<string>("action") ?? "windows").ToLowerInvariant();
                switch (action)
                {
                    case "view":
                    {
                        string vn = p.Value<string>("name") ?? "*Isometric"; doc.ShowNamedView2(vn, -1); PmUi.Pump(300); doc.ViewZoomtofit2(); PmUi.Pump(500);
                        return new JObject { ["view"] = vn };
                    }
                    case "project":
                    {
                        var view = doc.ActiveView as IModelView; if (view == null) throw new ToolFailure("NO_VIEW", "No active model view.");
                        var pts = new JArray(); var given = AsArray(p["points"], "points"); if (given == null || given.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "points is required: [[x, y, z], ...] in meters.");
                        foreach (var t in given)
                        {
                            var a = t as JArray; if (a == null || a.Count < 3) throw new ToolFailure("INVALID_PARAMETER", "points must be [[x, y, z], ...] in meters.");
                            var s = PmUi.Project(view, new[] { a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>() }); pts.Add(new JArray(s[0], s[1]));
                        }
                        var vb = PmUi.VisibleBox(view);
                        return new JObject { ["screen"] = pts, ["visible_box"] = new JArray(vb[0], vb[1], vb[2], vb[3]) };
                    }
                    case "window_at":
                    {
                        var h = PmUi.WindowAt(p.Value<int?>("sx") ?? 0, p.Value<int?>("sy") ?? 0); var r = PmUi.RectOf(h);
                        return new JObject { ["hwnd"] = h.ToInt64().ToString("X"), ["class"] = PmUi.ClassOf(h), ["ours"] = PmUi.IsOurs(h), ["pid"] = PmUi.PidOf(h), ["who"] = PmUi.Who(h), ["rect"] = new JArray(r.L, r.T, r.R - r.L, r.B - r.T), ["chain"] = new JArray(PmUi.ParentChain(h).Cast<object>().ToArray()) };
                    }
                    case "post_click":
                    {
                        int psx = p.Value<int?>("sx") ?? 0, psy = p.Value<int?>("sy") ?? 0; var under = PmUi.WindowAt(psx, psy);
                        if (!PmUi.IsOurs(under)) throw new ToolFailure("FOREIGN_WINDOW", "The pixel (" + psx + "," + psy + ") is covered by a window of another application (" + PmUi.Who(under) + "): nothing was clicked.");
                        var h = PmUi.Click(psx, psy, p.Value<bool?>("ctrl") ?? false);
                        return new JObject { ["posted"] = "hwnd=" + h.ToInt64().ToString("X"), ["selection_count"] = ((ISelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1) };
                    }
                    case "selection":
                    {
                        var sm = (ISelectionMgr)doc.SelectionManager; int n = sm.GetSelectedObjectCount2(-1); var arr = new JArray();
                        for (int i = 1; i <= n; i++) arr.Add(DescribeSelected(sm, i));
                        return new JObject { ["count"] = n, ["selected"] = arr };
                    }
                    case "windows":
                    {
                        var arr = new JArray();
                        foreach (var r in PmUi.Roots(_solidWorks))
                        {
                            var o = new JObject { ["handle"] = r.Handle.ToInt64().ToString("X"), ["title"] = r.Title, ["rect"] = new JArray(r.Rect.L, r.Rect.T, r.Rect.R - r.Rect.L, r.Rect.B - r.Rect.T), ["wpf"] = r.Source != null };
                            if (r.Visual != null) { o["root_type"] = r.Visual.GetType().FullName; var dep = r.Visual as System.Windows.Threading.DispatcherObject; if (dep != null) o["on_this_thread"] = dep.Dispatcher.CheckAccess(); }
                            arr.Add(o);
                        }
                        return new JObject { ["count"] = arr.Count, ["windows"] = arr };
                    }
                    case "tree":
                    case "act":
                    {
                        string want = p.Value<string>("root"); PmUi.Root pick = null;
                        if (!string.IsNullOrEmpty(want))
                        {
                            long hv; bool isHandle = want.Length >= 4 && long.TryParse(want, System.Globalization.NumberStyles.HexNumber, null, out hv);
                            long hval = isHandle ? long.Parse(want, System.Globalization.NumberStyles.HexNumber) : 0;
                            pick = PmUi.Roots(_solidWorks).FirstOrDefault(r => r.Visual != null && ((isHandle && r.Handle.ToInt64() == hval) || (r.Title ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0));
                        }
                        else pick = PmUi.FindRoot(_solidWorks, null);
                        if (pick == null) throw new ToolFailure("WINDOW_NOT_FOUND", "No visible WPF-hosted window matches '" + want + "' (action=windows lists them).");
                        var root = pick.Visual;
                        var dep = root as System.Windows.Threading.DispatcherObject;
                        if (dep != null && !dep.Dispatcher.CheckAccess()) throw new ToolFailure("WRONG_THREAD", "The WPF root belongs to another thread than the gate's UI thread.");
                        if (action == "act")
                        {
                            string nm = p.Value<string>("name"), op = (p.Value<string>("op") ?? "").ToLowerInvariant(), within = p.Value<string>("within");
                            System.Windows.DependencyObject scope = root;
                            if (!string.IsNullOrEmpty(within)) { scope = PmUi.Find(root, within); if (scope == null) throw new ToolFailure("ELEMENT_NOT_FOUND", "No element named '" + within + "' (within) under " + pick.Title); }
                            var el = PmUi.Find(scope, nm, p.Value<int?>("nth") ?? 0);
                            if (el == null) throw new ToolFailure("ELEMENT_NOT_FOUND", "No element named '" + nm + "' under " + pick.Title + (within == null ? "" : " / " + within));
                            string outcome; try { outcome = PmUi.Act(el, op, p.Value<string>("value")); } catch (InvalidOperationException ex) { throw new ToolFailure("INVALID_ACTION", ex.Message); }
                            return new JObject { ["name"] = nm, ["op"] = op, ["result"] = outcome };
                        }
                        int maxDepth = p.Value<int?>("max_depth") ?? 14, budget = p.Value<int?>("max_items") ?? 400; bool only = p.Value<bool?>("only_interesting") ?? true;
                        string from = p.Value<string>("from"); System.Windows.DependencyObject start = root;
                        if (!string.IsNullOrEmpty(from)) { start = PmUi.Find(root, from, p.Value<int?>("nth") ?? 0); if (start == null) throw new ToolFailure("ELEMENT_NOT_FOUND", "No element named '" + from + "'."); }
                        var list = new List<JObject>(); PmUi.Walk(start, 0, maxDepth, only, list, budget);
                        return new JObject { ["window"] = pick.Handle.ToInt64().ToString("X"), ["title"] = pick.Title, ["root_type"] = root.GetType().FullName, ["count"] = list.Count, ["truncated"] = list.Count >= budget, ["items"] = new JArray(list.Cast<object>().ToArray()) };
                    }
                    default: throw new ToolFailure("INVALID_PARAMETER", "action must be windows, tree, act, view, project, window_at, post_click or selection.");
                }
            });
        }
    }
}
