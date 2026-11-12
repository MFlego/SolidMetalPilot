// Probe 92 (round 7): where is the SolidWorks window and what does the display look like? (screen adaptation: the user's display size is NOT guaranteed.)
// Reports the frame rectangle (SolidWorks API: pixels), the Win32 rectangle and DPI of the main window, every monitor (bounds, work area, DPI), the graphics-area rectangle and the foreground window.
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

public static class Script
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear();
        var fr = sw.Frame() as Frame;
        IntPtr h = new IntPtr((long)(int)fr.GetHWnd()); RECT r; GetWindowRect(h, out r);
        Add("main window hwnd " + h.ToInt64().ToString("X") + " rect " + r.L + "," + r.T + "," + r.R + "," + r.B + " (" + (r.R - r.L) + " x " + (r.B - r.T) + ") dpi " + GetDpiForWindow(h));
        foreach (var s in Screen.AllScreens) Add("monitor " + s.DeviceName + " bounds " + s.Bounds + " work " + s.WorkingArea + " primary " + s.Primary);
        IntPtr fg = GetForegroundWindow(); var t = new StringBuilder(160); GetWindowText(fg, t, 160); RECT fr2; GetWindowRect(fg, out fr2); Add("foreground '" + t + "' rect " + fr2.L + "," + fr2.T + "," + fr2.R + "," + fr2.B + (fg == h ? " = SolidWorks" : " = ANOTHER window"));
        var li = new LASTINPUTINFO(); li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)); GetLastInputInfo(ref li);
        Add("seconds since the last keyboard / mouse input: " + ((uint)System.Environment.TickCount - li.dwTime) / 1000.0);
        var doc = sw.ActiveDoc as ModelDoc2;
        if (doc != null) { var mv = doc.ActiveView as ModelView; if (mv != null) { Add("model view window: " + mv.FrameLeft + "," + mv.FrameTop + " " + mv.FrameWidth + " x " + mv.FrameHeight); } }
        return sb.ToString();
    }
}
