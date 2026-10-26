using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Cheap modal-dialog detection done from the execution layer itself (plain Win32, ~1 ms, no bridge needed): visible top-level #32770 / HwndWrapper windows owned by a
    /// SolidWorks process. This is the pre-flight used before every tool call. The bridge add-in adds in-process facts (UI thread responsiveness, idle age, answering a dialog).
    /// Text is read with WM_GETTEXT + a 150 ms timeout so a wedged dialog cannot hang the caller.
    /// </summary>
    public static class LocalDialogs
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeoutMs, out IntPtr result);
        private const uint WM_GETTEXT = 0x000D, SMTO_ABORTIFHUNG = 0x0002;

        private static int[] _pids = new int[0]; private static DateTime _pidsAt = DateTime.MinValue;
        private static int[] SolidWorksPids()
        {
            if ((DateTime.UtcNow - _pidsAt).TotalSeconds > 2)
            {
                try { _pids = Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToArray(); } catch { _pids = new int[0]; }
                _pidsAt = DateTime.UtcNow;
            }
            return _pids;
        }


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

        private static string Text(IntPtr h)
        {
            var sb = new StringBuilder(1024); IntPtr res;
            return SendMessageTimeout(h, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, 150, out res) == IntPtr.Zero ? "" : sb.ToString();
        }
        private static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }

        /// <summary>JSON array of open modal dialogs: [{"title":..,"text":[..],"buttons":[..]}]; "[]" when none.</summary>
        public static string Json()
        {
            var pids = SolidWorksPids(); var items = new List<string>();
            if (pids.Length == 0) return "[]";
            EnumWindows((h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (Array.IndexOf(pids, (int)pid) < 0 || !IsWindowVisible(h)) return true;
                var cls = Cls(h); if (cls != "#32770" && !cls.StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;
                var text = new List<string>(); var buttons = new List<string>();
                EnumChildWindows(h, (c, l2) =>
                {
                    var cc = Cls(c); var t = Text(c); if (string.IsNullOrWhiteSpace(t)) return true;
                    if (cc == "Button") buttons.Add(t.Replace("&", "")); else if (cc == "Static") text.Add(t);
                    return true;
                }, IntPtr.Zero);
                if (text.Count == 0) text.AddRange(UiaTexts(h));
                items.Add("{\"class\":" + Q(cls) + ",\"title\":" + Q(Text(h)) + ",\"text\":[" + string.Join(",", text.Select(Q).ToArray()) + "],\"buttons\":[" + string.Join(",", buttons.Select(Q).ToArray()) + "]}");
                return true;
            }, IntPtr.Zero);
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\""; }
    }
}
