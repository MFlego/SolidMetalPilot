// Probe 10: the UNFOLDED blank's outline corners = edges parallel to Z whose length equals the sheet thickness. Prints (x, y) in mm so the corners the reference part rounds with its
// multi-radius fillet (12 corners: r 10 / 5 / 15 mm) can be matched in our own unfolded part.
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder();
        double tMm = 1.524; var body = (Body2)((object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false))[0];
        var bb = (double[])body.GetBodyBox(); sb.AppendLine("blank bbox mm: " + ((bb[3] - bb[0]) * 1000).ToString("F2") + " x " + ((bb[4] - bb[1]) * 1000).ToString("F2") + " x " + ((bb[5] - bb[2]) * 1000).ToString("F3"));
        var rows = new List<string>(); int n = 0;
        foreach (Edge e in (object[])body.GetEdges())
        {
            var cv = (Curve)e.GetCurve(); if (!cv.IsLine()) continue;
            double s, en; bool cl, per; cv.GetEndParams(out s, out en, out cl, out per);
            var a = (double[])cv.Evaluate2(s, 0); var b = (double[])cv.Evaluate2(en, 0);
            double dx = Math.Abs(a[0] - b[0]) * 1000, dy = Math.Abs(a[1] - b[1]) * 1000, dz = Math.Abs(a[2] - b[2]) * 1000;
            if (dx < 0.01 && dy < 0.01 && Math.Abs(dz - tMm) < 0.02)
            {
                n++; rows.Add(((a[0] * 1000)).ToString("F2").PadLeft(9) + " " + ((a[1] * 1000)).ToString("F2").PadLeft(9) + "   z " + (Math.Min(a[2], b[2]) * 1000).ToString("F2") + ".." + (Math.Max(a[2], b[2]) * 1000).ToString("F2"));
            }
        }
        rows.Sort(); sb.AppendLine("thickness-direction edges (outline corners): " + n); foreach (var r in rows) sb.AppendLine("   x,y mm " + r);
        return sb.ToString();
    }
}
