// Probe 96 (round 7): commit a number into a classic PropertyManager numeric box the way a typist does. WM_SETTEXT alone does NOT commit (the callout keeps the old value).
// arg = "hwnd=0x150EEC;text=20mm;mode=replace|chars;enter=1;tab=0;focus=1"
//   mode=replace: EM_SETSEL(0,-1) + EM_REPLACESEL(text)   mode=chars: EM_SETSEL(0,-1) then one WM_CHAR per character
//   enter=1: WM_KEYDOWN / WM_KEYUP VK_RETURN to the edit afterwards.  focus=1: SetFocus on the edit first (the thread of the window = SolidWorks' UI thread, which runs this script)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;

public static class Script
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var h = new IntPtr(Convert.ToInt64(a["hwnd"].Substring(2), 16)); string text = a["text"]; string mode = a.ContainsKey("mode") ? a["mode"] : "replace"; var log = new StringBuilder();
        if (a.ContainsKey("focus") && a["focus"] == "1") { var old = SetFocus(h); log.Append("SetFocus (old " + old.ToInt64().ToString("X") + ", now " + GetFocus().ToInt64().ToString("X") + ") ;; "); }
        SendMessage(h, 0x00B1, IntPtr.Zero, new IntPtr(-1));                                // EM_SETSEL 0, -1
        if (mode == "chars") { foreach (char c in text) { PostMessage(h, 0x0102, new IntPtr(c), IntPtr.Zero); System.Windows.Forms.Application.DoEvents(); } }     // WM_CHAR
        else SendMessage(h, 0x00C2, new IntPtr(1), text);                                   // EM_REPLACESEL
        System.Windows.Forms.Application.DoEvents();
        if (a.ContainsKey("enter") && a["enter"] == "1") { PostMessage(h, 0x0100, new IntPtr(13), IntPtr.Zero); PostMessage(h, 0x0101, new IntPtr(13), IntPtr.Zero); System.Windows.Forms.Application.DoEvents(); }
        if (a.ContainsKey("tab") && a["tab"] == "1") { PostMessage(h, 0x0100, new IntPtr(9), IntPtr.Zero); PostMessage(h, 0x0101, new IntPtr(9), IntPtr.Zero); System.Windows.Forms.Application.DoEvents(); }
        var t = new StringBuilder(80); GetWindowText(h, t, 80);
        return log + "text now '" + t + "'";
    }
}
