// Probe 97 (round 7): what do the references of the user's curve pattern point at? arg = "ref=CrvPattern1"
// Prints the face of D1FaceNormal (a point on it and its normal), what D1Direction is (feature / edge / sketch segment) and its name or end points, read-only.
using System;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string F(double v) { return v.ToString("0.#####", CultureInfo.InvariantCulture); }
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sb = new StringBuilder(); string refName = "CrvPattern1";
        if (!string.IsNullOrEmpty(arg) && arg.StartsWith("ref=")) refName = arg.Substring(4);
        Feature rf = null; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.Name == refName) rf = f; f = (Feature)f.GetNextFeature(); }
        if (rf == null) return "no feature " + refName;
        var rd = rf.GetDefinition() as ICurveDrivenPatternFeatureData; rd.AccessSelections(doc, null);
        try
        {
            object fn = rd.D1FaceNormal; var face = fn as Face2;
            if (face != null)
            {
                var b = (double[])face.GetBox(); var sf = (Surface)face.GetSurface();
                sb.Append("D1FaceNormal face: box (" + F(b[0]) + "," + F(b[1]) + "," + F(b[2]) + ")-(" + F(b[3]) + "," + F(b[4]) + "," + F(b[5]) + ") area " + F(face.GetArea()) + " planar " + sf.IsPlane() + " ;; ");
                var cp = (double[])face.GetClosestPointOn((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2); var nn = face.Normal as double[]; if (cp != null) sb.Append("  point on it (" + F(cp[0]) + "," + F(cp[1]) + "," + F(cp[2]) + ")" + (nn != null ? " normal (" + F(nn[0]) + "," + F(nn[1]) + "," + F(nn[2]) + ")" : "") + " ;; ");
            }
            else sb.Append("D1FaceNormal is " + (fn == null ? "null" : fn.GetType().Name) + " ;; ");
            object dir = rd.D1Direction;
            var feat = dir as Feature; var edge = dir as Edge; var seg = dir as SketchSegment;
            sb.Append("D1Direction: " + (feat != null ? "feature " + feat.Name : edge != null ? "edge" : seg != null ? "sketch segment" : dir == null ? "null" : dir.GetType().Name) + " ;; ");
            if (seg != null) { var c = seg.GetType(); sb.Append("  segment type " + seg.GetType() + " ;; "); }
            if (edge != null) { var sv = (Vertex)edge.GetStartVertex(); var ev2 = (Vertex)edge.GetEndVertex(); if (sv != null && ev2 != null) { var p = (double[])sv.GetPoint(); var q = (double[])ev2.GetPoint(); sb.Append("  edge from (" + F(p[0]) + "," + F(p[1]) + "," + F(p[2]) + ") to (" + F(q[0]) + "," + F(q[1]) + "," + F(q[2]) + ") ;; "); } }
        }
        finally { rd.ReleaseSelectionAccess(); }
        return sb.ToString();
    }
}
