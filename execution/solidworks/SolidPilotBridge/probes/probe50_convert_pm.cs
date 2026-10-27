// Probe 50: the Convert to Sheet Metal PropertyManager from INSIDE SolidWorks. What does it look like to Win32 (which controls are real windows, their class / id / text / rectangle), and can the UI thread
// open it and wait for it without UI Automation (UIA from the UI thread deadlocks; the first out-of-process locate of the lists cost ~55 s)?
// Needs an active PART with a solid box (extrude a rectangle ~100 x 60 x 30 mm on the Front plane first). Arguments:
//   open    delete the cap face (z max), select the bottom face as the FIXED face (mark 1), run the Convert command (2882), pump messages, dump the visible child windows of the frame
//   dump    dump again (PM state after your own clicks)
//   cancel  close the PropertyManager (Esc posted to the frame) and report CommandInProgress
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Native
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static string Text(IntPtr h)
    {
        var sb = new StringBuilder(80); IntPtr res;
        SendMessageTimeout(h, 0x000D, (IntPtr)80, sb, 0x0002, 150, out res);      // WM_GETTEXT, SMTO_ABORTIFHUNG
        return sb.ToString().Replace("\r", " ").Replace("\n", " ");
    }
    public static string Class(IntPtr h) { var sb = new StringBuilder(80); GetClassName(h, sb, 80); return sb.ToString(); }
}

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }

    static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
    }

    static void Dump(IntPtr frame)
    {
        int n = 0; var seen = new List<string>();
        Native.EnumChildWindows(frame, delegate(IntPtr h, IntPtr l)
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.RECT r; Native.GetWindowRect(h, out r);
            int w = r.R - r.L, hh = r.B - r.T; if (w < 6 || hh < 6) return true;
            n++;
            seen.Add(h.ToInt64().ToString("X") + " p=" + Native.GetParent(h).ToInt64().ToString("X") + " " + Native.Class(h) + " id=" + Native.GetDlgCtrlID(h) + " [" + r.L + "," + r.T + " " + w + "x" + hh + "] '" + Native.Text(h) + "'");
            return true;
        }, IntPtr.Zero);
        Add("visible child windows: " + n);
        foreach (var s in seen) Add(s);
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear();
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document";
        IntPtr frame = IntPtr.Zero; try { frame = (IntPtr)sw.IFrameObject().GetHWnd(); } catch { }
        if (frame == IntPtr.Zero) return "no frame handle";
        string cmd = (arg ?? "").Trim().ToLowerInvariant();
        if (cmd == "cancel")
        {
            Add("CommandInProgress before: " + sw.CommandInProgress);
            Native.PostMessage(frame, 0x0100, (IntPtr)0x1B, IntPtr.Zero); Native.PostMessage(frame, 0x0101, (IntPtr)0x1B, IntPtr.Zero);     // WM_KEYDOWN / WM_KEYUP VK_ESCAPE
            Pump(1200);
            Add("CommandInProgress after Esc: " + sw.CommandInProgress);
            return sb.ToString();
        }
        if (cmd == "open")
        {
            var part = (PartDoc)doc; var bodies = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false);
            if (bodies != null && bodies.Length > 0)
            {
                Face2 cap = null; double zMax = double.MinValue;
                foreach (Face2 f in (object[])((Body2)bodies[0]).GetFaces())
                {
                    var s = (Surface)f.GetSurface(); if (!s.IsPlane()) continue;
                    var bx = (double[])f.GetBox(); if (Math.Abs(bx[2] - bx[5]) > 1e-6) continue;       // horizontal planar faces only
                    if (bx[2] > zMax) { zMax = bx[2]; cap = f; }
                }
                if (cap == null) return "cap face not found";
                doc.ClearSelection2(true);
                ((Entity)cap).Select4(false, null);
                var del = ((object)doc).GetType().InvokeMember("InsertDeleteFace", System.Reflection.BindingFlags.InvokeMethod, null, doc, null);
                doc.ClearSelection2(true); doc.EditRebuild3();
                Add("delete face on the cap (z=" + (zMax * 1000).ToString("F2") + " mm) done");
            }
            var solids2 = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false); var sheets = (object[])part.GetBodies2((int)swBodyType_e.swSheetBody, false);
            Add("solid bodies " + (solids2 == null ? 0 : solids2.Length) + ", surface bodies " + (sheets == null ? 0 : sheets.Length));
            // the bottom face of the SURFACE body
            var sbodies = sheets; if (sbodies == null) return sb + " no surface body after Delete Face";
            Face2 fixedFace = null; double zm = double.MaxValue;
            foreach (Face2 f in (object[])((Body2)sbodies[0]).GetFaces()) { var s = (Surface)f.GetSurface(); if (!s.IsPlane()) continue; var bx = (double[])f.GetBox(); if (Math.Abs(bx[2] - bx[5]) < 1e-6 && bx[2] < zm) { zm = bx[2]; fixedFace = f; } }
            doc.ClearSelection2(true);
            var sd = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = 1;
            Add("fixed face selected: " + ((Entity)fixedFace).Select4(false, sd));
            bool ran = sw.RunCommand(2882, "");
            Add("RunCommand(2882) returned " + ran + "; CommandInProgress " + sw.CommandInProgress);
            Pump(2500);
            Add("after pumping 2.5 s: CommandInProgress " + sw.CommandInProgress);
        }
        Dump(frame);
        return sb.ToString();
    }
}
