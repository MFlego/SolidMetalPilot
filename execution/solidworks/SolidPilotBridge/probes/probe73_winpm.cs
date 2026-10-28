// Probe 73: the classic (Win32) PropertyManager of a SolidWorks command seen as child windows. SolidWorks 2026 builds only a few PropertyManagers as WPF (ui_inspect sees those); Jog, Gusset, Cross-Break,
// Tab and Slot, Swept Flange, Lofted Bend ... are classic ones: plain Win32 controls (#32770 pages, Button, Static, Edit, ComboBox, SysListView32 ...) under the "dvePage ScrollView" pane.
// arg = "list" (default)                      every visible child window of the SolidWorks frame whose left edge is within the PropertyManager column (x < xmax, default 520 physical pixels), with class, id, text, enabled, rect
//       "list;all=1"                          also the hidden ones
//       "click=0x1234"                        BM_CLICK the window (a button / check box / radio)
//       "text=0x1234|value"                   WM_SETTEXT, then EN_CHANGE + EN_KILLFOCUS to the parent
//       "sel=0x1234|index"                    CB_SETCURSEL / LB_SETCURSEL + the selection-change notification
//       "key=0x1234|13"                       post WM_KEYDOWN / WM_KEYUP of a virtual key to the window
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;

public static class Script
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static string Cls(IntPtr h) { var s = new StringBuilder(128); GetClassName(h, s, 128); return s.ToString(); }
    static string Txt(IntPtr h) { var s = new StringBuilder(300); GetWindowText(h, s, 300); return s.ToString().Replace("\r", " ").Replace("\n", " "); }

    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var parts = (arg ?? "list").Split(';');
        string mode = parts[0].Trim().ToLowerInvariant(); foreach (var p in parts) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        var frame = new IntPtr(sw.IFrameObject().GetHWnd());
        Func<string, IntPtr> hwnd = s => new IntPtr(Convert.ToInt64(s.StartsWith("0x") ? s.Substring(2) : s, 16));
        if (a.ContainsKey("click")) { var h = hwnd(a["click"]); SendMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); return "BM_CLICK sent to " + a["click"] + " (" + Cls(h) + " '" + Txt(h) + "')"; }
        if (a.ContainsKey("lclick"))
        {
            var h = hwnd(a["lclick"]); RECT r; GetWindowRect(h, out r); int cx = (r.R - r.L) / 2, cy = (r.B - r.T) / 2; var lp = new IntPtr((cy << 16) | (cx & 0xFFFF));
            PostMessage(h, 0x0200, IntPtr.Zero, lp); PostMessage(h, 0x0201, new IntPtr(1), lp); PostMessage(h, 0x0202, IntPtr.Zero, lp);       // WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP at the centre
            return "left click posted to " + a["lclick"] + " (" + Cls(h) + ") at client (" + cx + "," + cy + ")";
        }
        if (a.ContainsKey("text"))
        {
            var kv = a["text"].Split(new[] { '|' }, 2); var h = hwnd(kv[0]); var parent = GetParent(h); int id = GetDlgCtrlID(h);
            SendMessage(h, 0x000C, IntPtr.Zero, kv[1]);                                       // WM_SETTEXT
            SendMessage(parent, 0x0111, new IntPtr((0x0300 << 16) | (id & 0xFFFF)), h);       // WM_COMMAND, EN_CHANGE
            SendMessage(parent, 0x0111, new IntPtr((0x0200 << 16) | (id & 0xFFFF)), h);       // WM_COMMAND, EN_KILLFOCUS
            return "text set on " + kv[0] + " -> '" + Txt(h) + "'";
        }
        if (a.ContainsKey("sel"))
        {
            var kv = a["sel"].Split('|'); var h = hwnd(kv[0]); var parent = GetParent(h); int id = GetDlgCtrlID(h); int idx = int.Parse(kv[1], CultureInfo.InvariantCulture); string c = Cls(h);
            bool combo = c.IndexOf("Combo", StringComparison.OrdinalIgnoreCase) >= 0;
            SendMessage(h, combo ? 0x014E : 0x0186, new IntPtr(idx), IntPtr.Zero);            // CB_SETCURSEL / LB_SETCURSEL
            SendMessage(parent, 0x0111, new IntPtr(((combo ? 1 : 1) << 16) | (id & 0xFFFF)), h);   // CBN_SELCHANGE = 1 / LBN_SELCHANGE = 1
            return "selected index " + idx + " on " + kv[0] + " (" + c + ")";
        }
        if (a.ContainsKey("key")) { var kv = a["key"].Split('|'); var h = hwnd(kv[0]); int vk = int.Parse(kv[1], CultureInfo.InvariantCulture); PostMessage(h, 0x0100, new IntPtr(vk), IntPtr.Zero); PostMessage(h, 0x0101, new IntPtr(vk), IntPtr.Zero); return "key " + vk + " posted to " + kv[0]; }

        int xmax = a.ContainsKey("xmax") ? int.Parse(a["xmax"], CultureInfo.InvariantCulture) : 520; bool all = a.ContainsKey("all");
        var sb = new StringBuilder(); int n = 0;
        EnumChildWindows(frame, (h, l) =>
        {
            RECT r; GetWindowRect(h, out r); bool vis = IsWindowVisible(h);
            if ((vis || all) && r.L < xmax && (r.R - r.L) > 0)
            {
                string c = Cls(h); sb.Append("0x" + h.ToInt64().ToString("X") + " parent 0x" + GetParent(h).ToInt64().ToString("X") + " " + c + " id " + GetDlgCtrlID(h) + " '" + Txt(h) + "' " + (IsWindowEnabled(h) ? "" : "DISABLED ") + (vis ? "" : "HIDDEN ") + "[" + r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T) + "] ;; ");
                n++;
            }
            return true;
        }, IntPtr.Zero);
        return n + " windows in the PropertyManager column ;; " + sb.ToString();
    }
}
