// Modal-dialog detection and response from INSIDE the SolidWorks process (plain Win32, no UI-thread access needed - so it still works while a modal dialog has the UI thread blocked).
// Why: a SolidWorks message box ("No pre-made bends were found.", "This edge is a Rip edge ...") blocks every COM call from outside until someone presses OK; synthetic input
// meanwhile goes nowhere (and once crashed SolidWorks). The framework needs to SEE the dialog, read its text and answer it deliberately.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SolidPilot.Bridge
{
    public class DialogInfo
    {
        public long Handle;
        public string Class;
        public string Title;
        public List<string> Text = new List<string>();
        public List<string> Buttons = new List<string>();
    }

    public static class Dialogs
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        private const uint BM_CLICK = 0x00F5;


        /// <summary>Text of Task-Dialog style message boxes lives in a DirectUI control that GetWindowText cannot read; UI Automation exposes it as Text elements.
        /// Runs on a worker thread with a timeout so a wedged dialog cannot hang the caller.</summary>
        private static List<string> UiaTexts(IntPtr h)
        {
            var res = new List<string>();
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var el = System.Windows.Automation.AutomationElement.FromHandle(h);
                    var cond = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Text);
                    foreach (System.Windows.Automation.AutomationElement e in el.FindAll(System.Windows.Automation.TreeScope.Descendants, cond))
                    { var n = e.Current.Name; if (!string.IsNullOrWhiteSpace(n) && !res.Contains(n)) res.Add(n); }
                }
                catch { }
            }) { IsBackground = true };
            t.Start(); t.Join(1500);
            return res;
        }

        private static string Text(IntPtr h) { var sb = new StringBuilder(1024); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
        private static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }

        // SolidWorks keeps a pooled Task-Dialog window (a low window handle, created at start-up) that now and then turns visible with ONLY the template placeholder text and no buttons. It is not
        // a dialog anyone can answer, and treating it as one made tool calls fail with DIALOG_BLOCKING for ~10 s (2026-10-26). A real dialog always has buttons or real text.
        private static readonly HashSet<string> TemplateTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Title", "Description", "Button1", "Button2", "Button3", "Hyperlink Text 1", "Hyperlink Text 2" };
        private static readonly HashSet<long> SkeletonsSeen = new HashSet<long>();
        private static bool IsSkeleton(DialogInfo d) { return d.Buttons.Count == 0 && d.Text.Count > 0 && d.Text.TrueForAll(t => TemplateTexts.Contains(t)); }
        private static void NoteSkeleton(long handle) { lock (SkeletonsSeen) { if (SkeletonsSeen.Add(handle)) BridgeLog.Info("ignoring skeleton Task-Dialog window " + handle + " (template text only, no buttons)"); } }

        /// <summary>Visible top-level dialogs of THIS process (standard #32770 dialogs and WPF HwndWrapper message boxes), never the main SolidWorks window.</summary>
        public static List<DialogInfo> List()
        {
            var res = new List<DialogInfo>(); uint me = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var cls = Cls(h);
                if (cls != "#32770" && !cls.StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;
                var d = new DialogInfo { Handle = h.ToInt64(), Class = cls, Title = Text(h) };
                EnumChildWindows(h, (c, l2) =>
                {
                    var cc = Cls(c); var t = Text(c);
                    if (string.IsNullOrWhiteSpace(t)) return true;
                    if (cc == "Button") d.Buttons.Add(t.Replace("&", ""));
                    else if (cc == "Static" || cc.StartsWith("Edit", StringComparison.Ordinal)) d.Text.Add(t);
                    return true;
                }, IntPtr.Zero);
                if (d.Text.Count == 0) d.Text.AddRange(UiaTexts(h));
                if (IsSkeleton(d)) { NoteSkeleton(d.Handle); return true; }
                res.Add(d); return true;
            }, IntPtr.Zero);
            return res;
        }

        /// <summary>Press the button whose caption matches (case-insensitive, '&amp;' ignored) on the first dialog - BM_CLICK is posted, so no focus or mouse is needed.</summary>
        public static string Respond(string caption)
        {
            var want = (caption ?? "").Replace("&", "").Trim();
            foreach (var d in List())
            {
                IntPtr found = IntPtr.Zero;
                EnumChildWindows(new IntPtr(d.Handle), (c, l) =>
                {
                    if (Cls(c) == "Button" && string.Equals(Text(c).Replace("&", "").Trim(), want, StringComparison.OrdinalIgnoreCase)) { found = c; return false; }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) { PostMessage(found, BM_CLICK, IntPtr.Zero, IntPtr.Zero); return "clicked '" + want + "' on dialog '" + d.Title + "'"; }
            }
            return "no dialog with a button '" + want + "'";
        }

        public static string ToJson(List<DialogInfo> ds)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < ds.Count; i++)
            {
                var d = ds[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"handle\":").Append(d.Handle).Append(",\"class\":").Append(Json.Str(d.Class)).Append(",\"title\":").Append(Json.Str(d.Title))
                  .Append(",\"text\":").Append(Json.Arr(d.Text)).Append(",\"buttons\":").Append(Json.Arr(d.Buttons)).Append('}');
            }
            return sb.Append(']').ToString();
        }
    }

    public static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break; case '\n': sb.Append("\\n"); break; case '\r': break; case '\t': sb.Append("\\t"); break;
                    default: if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
        public static string Arr(IEnumerable<string> xs) { var l = new List<string>(); foreach (var x in xs) l.Add(Str(x)); return "[" + string.Join(",", l.ToArray()) + "]"; }
    }
}
