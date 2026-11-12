// Probe 99 (round 7): show a named view, fit it and rotate the camera about the screen centre by (dx, dy) radians (dx about the vertical screen axis, dy about the horizontal one), so a Convert can pick edges
// from a steeper / tilted angle than the standard views offer (views=["current"]). arg = "view=*Isometric;dx=0;dy=0.45"
using System;
using System.Collections.Generic;
using System.Globalization;
using SolidWorks.Interop.sldworks;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; string view = a.ContainsKey("view") ? a["view"] : "*Isometric";
        doc.ShowNamedView2(view, -1); doc.ViewZoomtofit2();
        double dx = a.ContainsKey("dx") ? double.Parse(a["dx"], CultureInfo.InvariantCulture) : 0, dy = a.ContainsKey("dy") ? double.Parse(a["dy"], CultureInfo.InvariantCulture) : 0;
        var mv = (ModelView)doc.ActiveView; mv.RotateAboutCenter(dx, dy); doc.ViewZoomtofit2();
        return "view " + view + " rotated by dx " + dx + " dy " + dy;
    }
}
