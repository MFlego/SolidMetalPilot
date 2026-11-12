// Probe 98 (round 7): name, box (mm) and centre of every solid and surface body, sorted by name. Read-only. arg = "filter=CrvPattern2"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string F(double v) { return (v * 1000).ToString("0.0", CultureInfo.InvariantCulture); }
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; string flt = (arg ?? "").StartsWith("filter=") ? arg.Substring(7) : ""; var rows = new List<string>();
        foreach (int t in new[] { 0, 1 })
            foreach (var o in (part.GetBodies2(t, false) as object[]) ?? new object[0])
            {
                var b = (Body2)o; if (flt.Length > 0 && !b.Name.Contains(flt)) continue; var bx = (double[])b.GetBodyBox();
                rows.Add(b.Name + " | x " + F(bx[0]) + " .. " + F(bx[3]) + " | y " + F(bx[1]) + " .. " + F(bx[4]) + " | z " + F(bx[2]) + " .. " + F(bx[5]) + " | centre (" + F((bx[0] + bx[3]) / 2) + ", " + F((bx[1] + bx[4]) / 2) + ", " + F((bx[2] + bx[5]) / 2) + ")");
            }
        return string.Join(" ;; ", rows.OrderBy(r => r));
    }
}
