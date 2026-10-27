// Probe 63: the exact B-spline of a sketch spline (order, knots, control points - in the sketch's own units, METERS), so the same curve can be created again with ISketchManager.CreateSplinesByEqnParams2.
// arg = "sketch=Sketch19". Output: for each spline segment the SplineParamData fields.
using System;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static string R(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    static string Arr(object o) { var d = o as double[]; if (d == null) return "null"; var s = new StringBuilder("["); for (int i = 0; i < d.Length; i++) { if (i > 0) s.Append(","); s.Append(R(d[i])); } return s.Append("]").ToString(); }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); string name = "Sketch19"; foreach (var kv in (arg ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2 && p[0].Trim() == "sketch") name = p[1].Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var f = (Feature)doc.FirstFeature(); while (f != null && f.Name != name) f = (Feature)f.GetNextFeature();
        if (f == null) return "no sketch " + name; var sk = (Sketch)f.GetSpecificFeature2();
        var segs = sk.GetSketchSegments() as object[]; if (segs == null) return "no segments";
        foreach (SketchSegment s in segs)
        {
            if (s.GetType() != (int)swSketchSegments_e.swSketchSPLINE) continue;
            var cv = (Curve)s.GetCurve(); Add("spline: IsBcurve " + cv.IsBcurve());
            foreach (bool cubic in new[] { true, false })
            {
                var pd = cv.GetBCurveParams5(cubic, false, true, false);
                if (pd == null) { Add("GetBCurveParams5(cubic " + cubic + ") -> null"); continue; }
                object cps, kns; pd.GetControlPoints(out cps); pd.GetKnotPoints(out kns);
                Add("PARAMS cubic=" + cubic + " order " + pd.Order + " dimension " + pd.Dimension + " periodic " + pd.Periodic + " control points " + pd.ControlPointsCount + " knots " + pd.KnotPointsCount);
                Add("   knots " + Arr(kns)); Add("   control points (model, m) " + Arr(cps));
            }
            // 2D view: the points mapped through the sketch transform (sketch coordinates)
            var xf = sk.ModelToSketchTransform; double s0, s1; bool cl, per; cv.GetEndParams(out s0, out s1, out cl, out per);
            foreach (double u in new[] { s0, (s0 + s1) / 2, s1 }) { var pt = (double[])cv.Evaluate2(u, 0); var mp = (MathPoint)((MathUtility)sw.GetMathUtility()).CreatePoint(new double[] { pt[0], pt[1], pt[2] }); var sp = (MathPoint)mp.MultiplyTransform(xf); var a = (double[])sp.ArrayData; Add("   curve at u=" + R(u) + ": model (" + R(pt[0]) + "," + R(pt[1]) + "," + R(pt[2]) + ") sketch (" + R(a[0]) + "," + R(a[1]) + "," + R(a[2]) + ")"); }
        }
        return sb.ToString();
    }
}
