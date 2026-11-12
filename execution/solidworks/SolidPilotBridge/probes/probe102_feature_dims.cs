// Probe 102 (round 7): the display dimensions of ONE feature with their full names and values (mm for lengths), so the right dimension of a sheet-metal feature can be linked. arg = feature name ("Sheet-Metal7")
using System;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sb = new StringBuilder();
        var f = (IFeature)doc.FirstFeature();
        while (f != null)
        {
            if (f.Name == arg)
            {
                var d = (IDisplayDimension)f.GetFirstDisplayDimension();
                while (d != null)
                {
                    var dim = d.GetDimension2(0);
                    double val = 0; try { val = dim.GetSystemValue2("") * 1000; } catch (Exception) { }
                    sb.Append(dim.FullName + " = " + val.ToString("0.####", CultureInfo.InvariantCulture) + " ;; ");
                    d = (IDisplayDimension)f.GetNextDisplayDimension(d);
                }
                sb.Append("[" + f.GetTypeName2() + "]");
            }
            f = (IFeature)f.GetNextFeature();
        }
        return sb.Length == 0 ? "feature not found: " + arg : sb.ToString();
    }
}
