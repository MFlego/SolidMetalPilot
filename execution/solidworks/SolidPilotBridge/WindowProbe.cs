// Go/no-go probes for driving SolidWorks without hijacking the user's mouse: (1) where is the graphics window, (2) do POSTED mouse messages (no cursor movement, no focus
// change) select geometry / fill PropertyManager selection boxes. Runs inside the SolidWorks process (DPI-aware: physical pixels, same space as IModelView.GetVisibleBox).
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SolidPilot.Bridge
{
    public static class WindowProbe
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct RC { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RC r);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PT p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref PT p);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);

        private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        private static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
        private static string Txt(IntPtr h) { var sb = new StringBuilder(128); GetWindowText(h, sb, 128); return sb.ToString(); }
        private static IntPtr MainWindow()
        {
            IntPtr best = IntPtr.Zero; long area = 0; uint me = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == me && IsWindowVisible(h) && string.IsNullOrEmpty(Cls(h)) == false && Cls(h).StartsWith("Afx", StringComparison.Ordinal)) { RC r; GetWindowRect(h, out r); long a = (long)(r.R - r.L) * (r.B - r.T); if (a > area) { area = a; best = h; } } return true; }, IntPtr.Zero);
            return best;
        }

        /// <summary>Visible descendants of the main window with class, caption and physical-pixel rectangle; arg = minimum width (default 200) to keep the output short.</summary>
        public static string Tree(string arg)
        {
            int minW; if (!int.TryParse(arg, out minW)) minW = 200;
            var main = MainWindow(); if (main == IntPtr.Zero) return "main window not found";
            var sb = new StringBuilder("main=" + main.ToInt64());
            EnumChildWindows(main, (h, l) =>
            {
                if (!IsWindowVisible(h)) return true; RC r; GetWindowRect(h, out r); if (r.R - r.L < minW || r.B - r.T < minW / 2) return true;
                sb.Append(" ;; " + h.ToInt64() + " " + Cls(h) + " '" + Txt(h) + "' " + r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T) + " parent=" + GetParent(h).ToInt64());
                return true;
            }, IntPtr.Zero);
            return sb.ToString();
        }

        /// <summary>arg = "sx sy" screen pixels. Posts MOUSEMOVE / LBUTTONDOWN / LBUTTONUP to the window under that point WITHOUT moving the cursor or changing focus.</summary>
        public static string PostClick(string arg)
        {
            var p = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); int sx, sy;
            if (p.Length < 2 || !int.TryParse(p[0], out sx) || !int.TryParse(p[1], out sy)) return "usage: post_click <screenX> <screenY>";
            var pt = new PT { X = sx, Y = sy }; var h = WindowFromPoint(pt); if (h == IntPtr.Zero) return "no window at point";
            var c = new PT { X = sx, Y = sy }; ScreenToClient(h, ref c);
            IntPtr lp = (IntPtr)((c.Y << 16) | (c.X & 0xFFFF)); IntPtr mk = (IntPtr)0x0001;
            PostMessage(h, WM_MOUSEMOVE, IntPtr.Zero, lp); PostMessage(h, WM_LBUTTONDOWN, mk, lp); PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, lp);
            return "posted to hwnd=" + h.ToInt64() + " class=" + Cls(h) + " client=" + c.X + "," + c.Y;
        }
    }
}
