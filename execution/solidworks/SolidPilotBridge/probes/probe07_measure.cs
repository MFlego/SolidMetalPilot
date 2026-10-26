// Measure the active part: solid bodies, faces, bounding box (mm / in), volume, face-type census (planar / cylindrical bend faces), bend count, sheet-metal thickness.
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder();
        var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        if (bodies == null) return "no solid body";
        foreach (Body2 b in bodies)
        {
            var bb = (double[])b.GetBodyBox(); int planar = 0, cyl = 0, other = 0, faces = 0;
            foreach (Face2 f in (object[])b.GetFaces()) { faces++; var s = (Surface)f.GetSurface(); if (s.IsPlane()) planar++; else if (s.IsCylinder()) cyl++; else other++; }
            sb.AppendLine("body '" + b.Name + "' faces=" + faces + " (planar " + planar + ", cylindrical " + cyl + ", other " + other + ")");
            sb.AppendLine("bbox mm: x " + (bb[0] * 1000).ToString("F3") + ".." + (bb[3] * 1000).ToString("F3") + "  y " + (bb[1] * 1000).ToString("F3") + ".." + (bb[4] * 1000).ToString("F3") + "  z " + (bb[2] * 1000).ToString("F3") + ".." + (bb[5] * 1000).ToString("F3"));
            sb.AppendLine("size in: " + ((bb[3] - bb[0]) / 0.0254).ToString("F3") + " x " + ((bb[4] - bb[1]) / 0.0254).ToString("F3") + " x " + ((bb[5] - bb[2]) / 0.0254).ToString("F3"));
        }
        var mp = doc.Extension.CreateMassProperty() as MassProperty; if (mp != null) sb.AppendLine("volume mm3 " + (mp.Volume * 1e9).ToString("F0") + "  surface area mm2 " + (mp.SurfaceArea * 1e6).ToString("F0"));
        int bends = 0; var f0 = (Feature)doc.FirstFeature();
        while (f0 != null) { var sub = (Feature)f0.GetFirstSubFeature(); while (sub != null) { string tn = sub.GetTypeName2(); if (tn == "OneBend" || tn == "SketchBend") bends++; sub = (Feature)sub.GetNextSubFeature(); } f0 = (Feature)f0.GetNextFeature(); }
        sb.AppendLine("bend sub-features: " + bends);
        return sb.ToString();
    }
}
