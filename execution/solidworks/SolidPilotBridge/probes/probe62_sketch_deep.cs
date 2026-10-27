// Probe 62: a sketch read to the last digit - every segment with its endpoints at full precision (micrometres hide a lot in a trim sliver), for splines the fit points, the control points, the degree, the
// tangent handles and the relations attached to each segment. arg = "sketch=Sketch19"
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
    static string Pts(object o) { var d = o as double[]; if (d == null) return "null"; var s = new StringBuilder(); for (int i = 0; i + 2 < d.Length + 0 && i < d.Length; i += 3) s.Append("(" + R(d[i] * 1000) + "," + R(d[i + 1] * 1000) + "," + R(d[i + 2] * 1000) + ") "); return s.ToString(); }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); string name = "Sketch19"; foreach (var kv in (arg ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2 && p[0].Trim() == "sketch") name = p[1].Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var f = (Feature)doc.FirstFeature(); while (f != null && f.Name != name) f = (Feature)f.GetNextFeature();
        if (f == null) return "no sketch " + name; var sk = (Sketch)f.GetSpecificFeature2();
        var segs = sk.GetSketchSegments() as object[]; Add(name + ": " + (segs == null ? 0 : segs.Length) + " segments; plane/transform available: " + (sk.ModelToSketchTransform != null));
        if (segs != null)
            foreach (SketchSegment s in segs)
            {
                int t = s.GetType(); string kind = Enum.GetName(typeof(swSketchSegments_e), t);
                Add("SEGMENT " + kind + " construction=" + s.ConstructionGeometry + " length " + R(s.GetLength() * 1000) + " mm, relations " + s.GetRelationsCount());
                var rels = s.GetRelations() as object[]; if (rels != null) foreach (SketchRelation r in rels) Add("     relation type " + Enum.GetName(typeof(swConstraintType_e), r.GetRelationType()));
                var line = s as SketchLine; if (line != null) { var a = (SketchPoint)line.GetStartPoint2(); var b = (SketchPoint)line.GetEndPoint2(); Add("     from (" + R(a.X * 1000) + "," + R(a.Y * 1000) + ") to (" + R(b.X * 1000) + "," + R(b.Y * 1000) + ")"); }
                var spl = s as SketchSpline;
                if (spl != null)
                {
                    Add("     SPLINE degree " + spl.CurveDegree + " points " + spl.GetPointCount() + " rational " + spl.IsRationalCurve + " style " + spl.IsStyleSpline + " proportional " + spl.Proportional);
                    Add("     points2: " + Pts(spl.GetPoints2()));
                    Add("     handles (" + spl.GetSplineHandleCount() + "): " + Pts(spl.GetSplineHandles()));
                    var cv = s.GetCurve() as Curve; if (cv != null) { double s0, s1; bool cl, per; cv.GetEndParams(out s0, out s1, out cl, out per); var a = (double[])cv.Evaluate2(s0, 1); var b = (double[])cv.Evaluate2(s1, 1); Add("     tangent at start (" + R(a[3]) + "," + R(a[4]) + "," + R(a[5]) + ") at end (" + R(b[3]) + "," + R(b[4]) + "," + R(b[5]) + ")"); }
                }
            }
        return sb.ToString();
    }
}
