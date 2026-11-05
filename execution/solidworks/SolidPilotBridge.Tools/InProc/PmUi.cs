// In-process UI layer for the tools that have to work THROUGH SolidWorks' user interface because the API cannot do the job (Convert to Sheet Metal first of all: the API cannot create the feature
// nor feed its PropertyManager lists). What makes this practical is that SolidWorks 2026 builds that PropertyManager as a WPF control inside its own process: from the add-in its visual tree is a
// plain object graph - lists, check boxes, buttons, numeric boxes - that can be read and driven through the very automation peers UI Automation would use, without a UI Automation client (the
// first locate of the lists cost ~55 s out-of-process and a client call from the UI thread deadlocks).
// Everything here runs on SolidWorks' UI thread (the gate has put the tool there). WAITING is done by pumping that thread's message queue (Pump), never by blocking it: posted mouse messages, WPF
// layout and SolidWorks' own idle work only happen while the queue is pumped.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace SolidworksExecution.Services
{
    internal static class PmUi
    {
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Win32
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

        public static string ClassOf(IntPtr h) { var sb = new StringBuilder(120); GetClassName(h, sb, 120); return sb.ToString(); }

        public static string TextOf(IntPtr h)
        {
            var sb = new StringBuilder(120); IntPtr res;
            SendMessageTimeout(h, 0x000D, (IntPtr)120, sb, 0x0002, 150, out res);      // WM_GETTEXT, SMTO_ABORTIFHUNG
            return sb.ToString().Replace("\r", " ").Replace("\n", " ");
        }

        public static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }

        public static IntPtr WindowAt(int sx, int sy) { return WindowFromPoint(new POINT { X = sx, Y = sy }); }

        private static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>True when the window belongs to the SolidWorks process. A click is only ever posted to such a window: a pixel that another application's window covers (a browser, an editor, a
        /// chat) must never receive one from us.</summary>
        public static bool IsOurs(IntPtr h) { if (h == IntPtr.Zero) return false; uint pid; GetWindowThreadProcessId(h, out pid); return pid == MyPid; }

        /// <summary>"process: class in 'title of the top-level window'" - what a person recognises, for messages about a window that is in the way.</summary>
        public static string Who(IntPtr h)
        {
            if (h == IntPtr.Zero) return "no window";
            uint pid; GetWindowThreadProcessId(h, out pid); string proc = "pid " + pid; try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch (Exception) { }
            IntPtr top = h; for (int i = 0; i < 16; i++) { var up = GetParent(top); if (up == IntPtr.Zero) break; top = up; }
            return proc + ": " + ClassOf(h) + " in '" + TextOf(top) + "'";
        }

        public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

        public static IEnumerable<string> ParentChain(IntPtr h)
        {
            for (int i = 0; i < 8 && h != IntPtr.Zero; i++) { yield return h.ToInt64().ToString("X") + " " + ClassOf(h) + " '" + TextOf(h) + "'"; h = GetParent(h); }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // waiting without blocking
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        /// <summary>While set, waiting does NOT stop when the gate has given up on the call - for the clean-up that must still run then (cancelling a PropertyManager).</summary>
        [ThreadStatic] public static bool IgnoreCancel;

        // The gate told the caller STA_TIMEOUT: nobody waits for this call's answer any more, so every wait is a chance to stop (the tool's own catch block then cleans up).
        private static void CheckCancel()
        {
            if (!IgnoreCancel && SolidPilot.Bridge.ToolGate.CancelRequested) throw new OperationCanceledException("the caller was told STA_TIMEOUT and gave up on this call: stopping and cleaning up");
        }

        /// <summary>Let this thread's queue run for <paramref name="ms"/>: posted mouse messages, WPF layout and SolidWorks' idle work are only processed while it does.</summary>
        public static void Pump(int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            do { CheckCancel(); System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); } while (DateTime.UtcNow < end);
        }

        /// <summary>Pump until <paramref name="done"/> holds or the time is up; true when it held.</summary>
        public static bool PumpUntil(Func<bool> done, int timeoutMs)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                CheckCancel();
                if (done()) return true;
                if (DateTime.UtcNow >= end) return false;
                System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(15);
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // WPF roots
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        public sealed class Root
        {
            public IntPtr Handle; public string Title, Class; public RECT Rect; public HwndSource Source;
            public DependencyObject Visual { get { return Source == null ? null : Source.RootVisual; } }
        }

        /// <summary>Every visible WPF-hosted ("HwndWrapper...") window below the SolidWorks frame.</summary>
        public static List<Root> Roots(ISldWorks sw)
        {
            var frame = (IntPtr)sw.IFrameObject().GetHWnd(); var roots = new List<Root>();
            EnumChildWindows(frame, (h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var cls = ClassOf(h); if (!cls.StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;
                HwndSource src = null; try { src = HwndSource.FromHwnd(h); } catch { }
                roots.Add(new Root { Handle = h, Title = TextOf(h), Class = cls, Rect = RectOf(h), Source = src });
                return true;
            }, IntPtr.Zero);
            return roots;
        }

        public static Root FindRoot(ISldWorks sw, string titleFragment)
        {
            return Roots(sw).FirstOrDefault(r => r.Visual != null && (string.IsNullOrEmpty(titleFragment) || (r.Title ?? "").IndexOf(titleFragment, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        public static Root WaitForRoot(ISldWorks sw, string titleFragment, int timeoutMs)
        {
            Root hit = null; PumpUntil(() => (hit = FindRoot(sw, titleFragment)) != null, timeoutMs); return hit;
        }

        public static bool WaitUntilGone(ISldWorks sw, string titleFragment, int timeoutMs) { return PumpUntil(() => FindRoot(sw, titleFragment) == null, timeoutMs); }

        /// <summary>The words in SolidWorks' balloons - the "Rebuild Errors / Unable to thicken the surface." callout is a top-level, no-activate WPF window of this process: the TextBlocks of every
        /// visible top-level WPF window other than the docked PropertyManager. Empty when there is none.</summary>
        public static List<string> BalloonTexts()
        {
            var res = new List<string>(); uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid); if (pid != me || !IsWindowVisible(h) || !ClassOf(h).StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;
                HwndSource src = null; try { src = HwndSource.FromHwnd(h); } catch { }
                var root = src == null ? null : src.RootVisual; if (root == null) return true;
                var texts = new List<string>();
                Action<DependencyObject> walk = null;
                walk = d => { var tb = d as TextBlock; if (tb != null && !string.IsNullOrWhiteSpace(tb.Text)) texts.Add(tb.Text.Trim()); int n = VisualTreeHelper.GetChildrenCount(d); for (int i = 0; i < n; i++) walk(VisualTreeHelper.GetChild(d, i)); };
                walk(root);
                if (texts.Count > 0) res.Add(string.Join(" | ", texts.ToArray()));
                return true;
            }, IntPtr.Zero);
            return res;
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // visual tree: find, describe, drive
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        /// <summary>The <paramref name="nth"/>-th element named <paramref name="name"/> (x:Name or AutomationId) under <paramref name="scope"/>; templates repeat names, hence the scope.</summary>
        public static FrameworkElement Find(DependencyObject scope, string name, int nth = 0)
        {
            FrameworkElement hit = null; int seen = 0;
            Action<DependencyObject> walk = null;
            walk = d =>
            {
                if (d == null || hit != null) return;
                var fe = d as FrameworkElement;
                if (fe != null && (fe.Name == name || AutomationProperties.GetAutomationId(fe) == name) && seen++ == nth) { hit = fe; return; }
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n && hit == null; i++) walk(VisualTreeHelper.GetChild(d, i));
            };
            walk(scope);
            return hit;
        }

        public static T Need<T>(DependencyObject scope, string name, string within = null) where T : FrameworkElement
        {
            if (!string.IsNullOrEmpty(within)) { scope = Find(scope, within); if (scope == null) throw new InvalidOperationException("no element named '" + within + "'"); }
            var el = Find(scope, name) as T; if (el == null) throw new InvalidOperationException("no " + typeof(T).Name + " named '" + name + "'" + (within == null ? "" : " in '" + within + "'"));
            return el;
        }

        public static int ItemCount(DependencyObject root, string listName) { var l = Find(root, listName) as ItemsControl; return l == null ? -1 : l.Items.Count; }

        /// <summary>The captions of a PropertyManager selection list (Edge&lt;1&gt;, Sketch30 ...): the TextBlocks inside its item containers.</summary>
        public static List<string> ListCaptions(DependencyObject root, string listName)
        {
            var res = new List<string>(); var list = Find(root, listName); if (list == null) return res;
            Action<DependencyObject> walk = null;
            walk = d => { var tb = d as TextBlock; if (tb != null && !string.IsNullOrEmpty(tb.Text)) res.Add(tb.Text); int n = VisualTreeHelper.GetChildrenCount(d); for (int i = 0; i < n; i++) walk(VisualTreeHelper.GetChild(d, i)); };
            walk(list);
            return res;
        }

        public static string Text(DependencyObject root, string numericOrTextName)
        {
            var el = Find(root, numericOrTextName); if (el == null) return null;
            var tb = el as TextBox ?? Find(el, "valueText") as TextBox; return tb == null ? null : tb.Text;
        }

        public static bool? Checked(DependencyObject root, string name) { var t = Find(root, name) as ToggleButton; return t == null ? (bool?)null : t.IsChecked; }

        /// <summary>Drive one control through the same automation peers UI Automation would use, so SolidWorks' own handlers run exactly as for a user.</summary>
        public static string Act(FrameworkElement el, string op, string value)
        {
            switch (op)
            {
                case "expand": { var ex = el as Expander; if (ex == null) throw new InvalidOperationException(el.GetType().Name + " is not an Expander"); ex.IsExpanded = true; ex.UpdateLayout(); return "expanded"; }
                case "collapse": { var ex = el as Expander; if (ex == null) throw new InvalidOperationException(el.GetType().Name + " is not an Expander"); ex.IsExpanded = false; ex.UpdateLayout(); return "collapsed"; }
                case "focus": return "focus " + el.Focus();
                case "toggle":
                {
                    var peer = UIElementAutomationPeer.CreatePeerForElement(el); var tp = peer == null ? null : peer.GetPattern(PatternInterface.Toggle) as System.Windows.Automation.Provider.IToggleProvider;
                    if (tp == null) throw new InvalidOperationException(el.GetType().Name + " has no Toggle pattern");
                    tp.Toggle(); var t = el as ToggleButton; return "toggled -> " + (t != null && t.IsChecked.HasValue ? t.IsChecked.Value.ToString() : "?");
                }
                case "invoke":
                {
                    var peer = UIElementAutomationPeer.CreatePeerForElement(el); var ip = peer == null ? null : peer.GetPattern(PatternInterface.Invoke) as System.Windows.Automation.Provider.IInvokeProvider;
                    if (ip == null) throw new InvalidOperationException(el.GetType().Name + " has no Invoke pattern");
                    ip.Invoke(); return "invoked";
                }
                case "set_text":
                {
                    var tb = el as TextBox ?? Find(el, "valueText") as TextBox; if (tb == null) throw new InvalidOperationException(el.GetType().Name + " holds no TextBox");
                    tb.Focus(); tb.SelectAll(); tb.Text = value ?? ""; return "text = " + tb.Text;
                }
                case "select_index":
                {
                    var sel = el as Selector; if (sel == null) throw new InvalidOperationException(el.GetType().Name + " is not a Selector");
                    sel.SelectedIndex = int.Parse(value ?? "0"); return "selected " + sel.SelectedIndex;
                }
                default: throw new InvalidOperationException("op must be expand, collapse, focus, toggle, invoke, set_text or select_index");
            }
        }

        /// <summary>Set a check box / toggle to the wanted state (no-op when it already is).</summary>
        public static void SetChecked(DependencyObject root, string name, bool on)
        {
            var t = Need<ToggleButton>(root, name); if ((t.IsChecked ?? false) != on) Act(t, "toggle", null);
        }

        // one visual as a compact record (the visual tree dump of ui_inspect)
        public static JObject Describe(DependencyObject d, int depth)
        {
            var o = new JObject { ["depth"] = depth, ["type"] = d.GetType().Name };
            string id = null, name = null;
            try { id = AutomationProperties.GetAutomationId(d); name = AutomationProperties.GetName(d); } catch { }
            var fe = d as FrameworkElement;
            if (!string.IsNullOrEmpty(id)) o["id"] = id;
            if (!string.IsNullOrEmpty(name)) o["name"] = name;
            if (fe != null && !string.IsNullOrEmpty(fe.Name)) o["x:name"] = fe.Name;
            var ui = d as UIElement;
            if (ui != null) { if (!ui.IsVisible) o["visible"] = false; if (!ui.IsEnabled) o["enabled"] = false; if (ui.IsKeyboardFocusWithin) o["focus"] = true; }
            var tb = d as TextBlock; if (tb != null && !string.IsNullOrEmpty(tb.Text)) o["text"] = Trunc(tb.Text, 80);
            var box = d as TextBox; if (box != null) o["value"] = Trunc(box.Text, 60);
            var cc = d as ContentControl; if (cc != null && cc.Content is string) o["content"] = Trunc((string)cc.Content, 80);
            var tg = d as ToggleButton; if (tg != null) o["checked"] = tg.IsChecked.HasValue ? tg.IsChecked.Value.ToString() : "null";
            var ic = d as ItemsControl;
            if (ic != null)
            {
                o["items"] = ic.Items.Count;
                if (ic.Items.Count > 0) o["first"] = new JArray(ic.Items.Cast<object>().Take(6).Select(i => (object)Trunc(i == null ? "null" : i.ToString(), 60)).ToArray());
            }
            var sel = d as Selector; if (sel != null) o["selected"] = sel.SelectedIndex;
            return o;
        }

        private static string Trunc(string s, int n) { return s == null ? null : (s.Length <= n ? s : s.Substring(0, n) + "..."); }

        // boring layout containers are skipped unless they carry an id / name; interesting controls always make it
        public static bool Interesting(JObject o)
        {
            if (o["id"] != null || o["name"] != null || o["x:name"] != null || o["items"] != null || o["text"] != null || o["value"] != null || o["content"] != null || o["checked"] != null) return true;
            switch ((string)o["type"]) { case "Button": case "CheckBox": case "RadioButton": case "ListBox": case "ListView": case "ComboBox": case "Expander": case "TabItem": case "GroupBox": return true; }
            return false;
        }

        public static void Walk(DependencyObject d, int depth, int maxDepth, bool onlyInteresting, List<JObject> sink, int budget)
        {
            if (d == null || depth > maxDepth || sink.Count >= budget) return;
            var rec = Describe(d, depth);
            if (!onlyInteresting || Interesting(rec)) sink.Add(rec);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n && sink.Count < budget; i++) Walk(VisualTreeHelper.GetChild(d, i), depth + 1, maxDepth, onlyInteresting, sink, budget);
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // picking in the graphics area
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        /// <summary>Screen position of the top-left corner of the graphics window's client area. The view transform's translation (m9, m10) is measured from THAT corner, not from the screen:
        /// it only looked absolute while the window started at x = 0. A toolbar docked at the left edge (measured 2026-11-05: an Annotation toolbar, 45 px) moves the whole window right and
        /// every click missed by the same 45 px. The window is the nearest window of this process, from the one under the middle of the visible box upward, whose client area holds the whole
        /// box (an overlay or a child control under the middle is skipped). Null when none is found (the box is covered by another application): Project then falls back to the old rule.</summary>
        public static int[] GraphicsOrigin(IModelView view)
        {
            var box = (int[])view.GetVisibleBox();
            var h = WindowFromPoint(new POINT { X = (box[0] + box[2]) / 2, Y = (box[1] + box[3]) / 2 });
            for (int i = 0; i < 8 && h != IntPtr.Zero; i++, h = GetParent(h))
            {
                if (!IsOurs(h)) return null;
                RECT c; var o = new POINT { X = 0, Y = 0 }; if (!GetClientRect(h, out c) || !ClientToScreen(h, ref o)) continue;
                if (box[0] >= o.X - 3 && box[1] >= o.Y - 3 && box[2] <= o.X + c.R + 3 && box[3] <= o.Y + c.B + 3) return new[] { o.X, o.Y };
            }
            return null;
        }

        /// <summary>Model point (m) to screen pixel in the active view. IModelView.Transform.ArrayData is a 3x3 COLUMN-major rotation (0..8), a translation (9..11) and a scale in pixels per
        /// meter (12); (m9, m10) are pixels from the top-left corner of the graphics window (<see cref="GraphicsOrigin"/>). Calibrated 2026-10-26 as "x absolute, y from the top of the visible
        /// box", which held only while the window sat at x = 0; that rule remains the fallback.</summary>
        public static int[] Project(IModelView view, double[] p)
        {
            var m = (double[])((IMathTransform)view.Transform).ArrayData; var box = (int[])view.GetVisibleBox(); double s = m[12];
            var o = GraphicsOrigin(view); double ox = o == null ? 0 : o[0], oy = o == null ? box[1] : o[1];
            return new[] { (int)Math.Round(ox + m[9] + s * (m[0] * p[0] + m[3] * p[1] + m[6] * p[2])), (int)Math.Round(oy + m[10] + s * (m[1] * p[0] + m[4] * p[1] + m[7] * p[2])) };
        }

        public static int[] VisibleBox(IModelView view) { return (int[])view.GetVisibleBox(); }

        /// <summary>WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP POSTED to the window under the screen point: a genuine pick event for SolidWorks (it fills PropertyManager lists, which API selections
        /// never do) without moving the user's cursor or stealing focus. Pumped in between so each message is handled before the next, as a human's hand would. Returns the window it went to.
        /// Only a window of the SolidWorks process is ever clicked: when another application covers the pixel this throws and nothing is posted.</summary>
        public static IntPtr Click(int sx, int sy, bool ctrl)
        {
            var pt = new POINT { X = sx, Y = sy }; var h = WindowFromPoint(pt); if (h == IntPtr.Zero) return h;
            if (!IsOurs(h)) throw new InvalidOperationException("the pixel (" + sx + "," + sy + ") is covered by a window of another application (" + Who(h) + "): nothing was clicked");
            ScreenToClient(h, ref pt); var lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)); var mk = (IntPtr)(0x0001 | (ctrl ? 0x0008 : 0));
            PostMessage(h, 0x0200, IntPtr.Zero, lp); Pump(80);
            PostMessage(h, 0x0201, mk, lp); Pump(50);
            PostMessage(h, 0x0202, (IntPtr)(ctrl ? 0x0008 : 0), lp); Pump(150);
            return h;
        }
    }
}
