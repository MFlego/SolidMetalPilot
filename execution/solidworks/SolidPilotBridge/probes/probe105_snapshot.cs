// Probe 105 (round 9): a picture of the MODEL VIEW only (the graphics area rendered to a BMP of the given size), independent of the task pane / PropertyManager that covers the window (round 8: final pictures cropped by the right task pane).
// arg = "<abs path>.bmp[|WxH]"   (default 1500x1000). The view (named view, rotation) is set BEFORE by the caller; this probe zooms to fit and renders. Returns "OK <path>" or "FAILED".
using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var parts = (arg ?? "").Split('|');
        string path = parts[0].Trim();
        int w = 1500, h = 1000;
        if (parts.Length > 1)
        {
            var wh = parts[1].Split('x');
            if (wh.Length == 2) { int.TryParse(wh[0], out w); int.TryParse(wh[1], out h); }
        }
        var doc = (ModelDoc2)sw.ActiveDoc;
        if (doc == null) return "FAILED no active document";
        doc.ViewZoomtofit2();
        bool ok = doc.SaveBMP(path, w, h);
        return ok ? "OK " + path : "FAILED SaveBMP returned false";
    }
}
