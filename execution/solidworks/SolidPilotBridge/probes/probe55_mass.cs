// Probe 55: exact mass properties of the active part (analyze_model(mass_properties) prints only six significant digits - about 1 mm3 of resolution at 1e-4 m3, too coarse for closed-form
// volume checks). Output: "volume=<m3, round-trip format> ;; area=<m2> ;; cg=<x>,<y>,<z> (m)". System units (meters), whatever the document's display unit is.
using System;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no document";
        var mp = (MassProperty)doc.Extension.CreateMassProperty(); mp.UseSystemUnits = true;
        var cg = (double[])mp.CenterOfMass; var inv = CultureInfo.InvariantCulture;
        return "volume=" + mp.Volume.ToString("R", inv) + " ;; area=" + mp.SurfaceArea.ToString("R", inv) + " ;; cg=" + cg[0].ToString("R", inv) + "," + cg[1].ToString("R", inv) + "," + cg[2].ToString("R", inv);
    }
}
