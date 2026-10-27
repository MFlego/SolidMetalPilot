// Probe 68: show a named view and zoom to a box of the model (mm) - to take a close-up screenshot of one corner with sw-capture.ps1. arg = "view=*Isometric;box=x0,y0,z0,x1,y1,z1"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; string view = a.ContainsKey("view") ? a["view"] : "*Isometric";
        doc.ShowNamedView2(view, -1); doc.ViewZoomtofit2();
        if (a.ContainsKey("box"))
        {
            var b = a["box"].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture) / 1000).ToArray();
            doc.ViewZoomTo2(b[0], b[1], b[2], b[3], b[4], b[5]);
        }
        doc.GraphicsRedraw2();
        return "view " + view + (a.ContainsKey("box") ? ", zoomed to " + a["box"] + " mm" : "");
    }
}
