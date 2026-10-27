// Probe 47: read the segments of a named sketch of the ACTIVE part straight from the API (no rebuild, no analyze_model - which pops "What's Wrong" on a part with a mitre flange): type, start and end in
// model space (mm and inches) and the sketch's plane origin / normal.   arg = sketch name (e.g. "Sketch2")
using System;
using System.Text;
using SolidWorks.Interop.sldworks;

public static class Script
{
    static StringBuilder sb;
    static void L(string s) { sb.Append(s).Append(" ;; "); }
    static string P(double[] v) { return "(" + (v[0] * 1000).ToString("F3") + ", " + (v[1] * 1000).ToString("F3") + ", " + (v[2] * 1000).ToString("F3") + ") mm = (" + (v[0] / 0.0254).ToString("F4") + ", " + (v[1] / 0.0254).ToString("F4") + ", " + (v[2] / 0.0254).ToString("F4") + ") in"; }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var doc = (ModelDoc2)sw.ActiveDoc; var mu = (MathUtility)sw.GetMathUtility();
        Feature f = (Feature)doc.FirstFeature(); Feature hit = null; while (f != null) { if (f.Name == arg) { hit = f; break; } f = (Feature)f.GetNextFeature(); }
        if (hit == null) return "no top-level feature named " + arg;
        var sk = hit.GetSpecificFeature2() as Sketch; if (sk == null) return arg + " is not a sketch (" + hit.GetTypeName2() + ")";
        var inv = (MathTransform)((MathTransform)sk.ModelToSketchTransform).Inverse(); var segs = sk.GetSketchSegments() as object[];
        L(arg + ": " + (segs == null ? 0 : segs.Length) + " segments");
        if (segs != null)
            foreach (object o in segs)
            {
                var s = (SketchSegment)o; int t = s.GetType();
                var ln = o as SketchLine; var arc = o as SketchArc;
                if (ln != null)
                {
                    var a = (SketchPoint)ln.GetStartPoint2(); var b = (SketchPoint)ln.GetEndPoint2();
                    var ma = (double[])((MathPoint)((MathPoint)mu.CreatePoint(new double[] { a.X, a.Y, a.Z })).MultiplyTransform(inv)).ArrayData; var mb = (double[])((MathPoint)((MathPoint)mu.CreatePoint(new double[] { b.X, b.Y, b.Z })).MultiplyTransform(inv)).ArrayData;
                    double len = Math.Sqrt(Math.Pow(mb[0] - ma[0], 2) + Math.Pow(mb[1] - ma[1], 2) + Math.Pow(mb[2] - ma[2], 2));
                    L("  line " + P(ma) + " -> " + P(mb) + "  length " + (len * 1000).ToString("F3") + " mm = " + (len / 0.0254).ToString("F4") + " in" + (s.ConstructionGeometry ? "  [construction]" : ""));
                }
                else if (arc != null) L("  arc (radius " + (arc.GetRadius() * 1000).ToString("F3") + " mm)");
                else L("  segment type " + t);
            }
        return sb.ToString();
    }
}
