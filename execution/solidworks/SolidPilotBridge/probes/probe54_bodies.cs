// Probe 54: every body of the active part (solid AND surface) with its type, visibility and bounding box - the tools that hide bodies while they work (sheet_metal_convert) must show them again.
// Output: one body per item, "type=<0 solid|1 sheet> visible=<True|False> box=<x0,y0,z0,x1,y1,z1 mm>".
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no document"; var part = (PartDoc)doc; var sb = new StringBuilder();
        var all = (object[])part.GetBodies2((int)swBodyType_e.swAllBodies, false); if (all == null) return "no bodies";
        foreach (Body2 b in all)
        {
            var x = (double[])b.GetBodyBox();
            sb.Append("type=" + b.GetType() + " visible=" + b.Visible + " box=" + (x[0] * 1000).ToString("F3") + "," + (x[1] * 1000).ToString("F3") + "," + (x[2] * 1000).ToString("F3") + "," + (x[3] * 1000).ToString("F3") + "," + (x[4] * 1000).ToString("F3") + "," + (x[5] * 1000).ToString("F3") + " ;; ");
        }
        return sb.ToString();
    }
}
