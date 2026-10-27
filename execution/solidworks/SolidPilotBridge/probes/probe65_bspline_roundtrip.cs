// !!! WARNING: THIS PROBE CRASHED SOLIDWORKS (2026-10-27 12:44, sldworks.exe heap corruption 0xc0000374) - variant (b) (SplineParamData refilled with SetControlPoints and fed to CreateSplinesByEqnParams2)
// !!! or variant (a) (ISketchSpline.ModifyControlPoint) is the culprit. Kept as the evidence; do NOT run it on a document you care about - a scratch COPY only, and expect to restart SolidWorks.
// Probe 65: re-creating an EXACT B-spline in a sketch. Seeds: (a) a 3-fit-point spline made with CreateSpline2 whose control points are then moved with ISketchSpline.ModifyControlPoint, (b) the SplineParamData
// of such a seed refilled with SetControlPoints / SetKnotPoints and fed to CreateSplinesByEqnParams2. Runs in a NEW sketch on the Front Plane of the active part (a scratch copy!). Reports the control points read back.
// arg = "cps=x0,y0,z0,...;pts=x0,y0,x1,y1,x2,y2" (meters; pts = the three fit points of the seed)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static string R(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    static double[] D(string s) { return s.Split(',').Select(x => double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray(); }
    static string Arr(object o) { var d = o as double[]; if (d == null) return "null"; return "[" + string.Join(",", d.Select(R).ToArray()) + "]"; }

    static void Report(string label, SketchSegment seg)
    {
        if (seg == null) { Add(label + ": null"); return; }
        var cv = (Curve)seg.GetCurve(); var pd = cv.GetBCurveParams5(true, false, true, false); object cps, kns; pd.GetControlPoints(out cps); pd.GetKnotPoints(out kns);
        Add(label + ": order " + pd.Order + " knots " + Arr(kns)); Add("   control points " + Arr(cps));
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var sm = doc.SketchManager; var cps = D(a["cps"]); var pts = D(a["pts"]);
        doc.ClearSelection2(true); doc.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0); sm.InsertSketch(true); sm.AddToDB = true;
        // (a) seed through the three fit points, then move the five control points
        var seed = sm.CreateSpline2(new double[] { pts[0], pts[1], 0, pts[2], pts[3], 0, pts[4], pts[5], 0 }, false) as SketchSegment; Report("seed (natural ends)", seed);
        var spl = seed as SketchSpline;
        if (spl != null)
        {
            for (int i = 0; i < cps.Length / 3; i++) { bool ok = spl.ModifyControlPoint(i, cps[3 * i], cps[3 * i + 1], cps[3 * i + 2]); if (!ok) Add("ModifyControlPoint(" + i + ") -> false"); }
            Report("(a) after ModifyControlPoint", seed);
        }
        // (b) param data refilled
        var seed2 = sm.CreateSpline2(new double[] { pts[0], pts[1], 0, pts[2], pts[3], 0, pts[4], pts[5], 0 }, false) as SketchSegment;
        var pd = ((Curve)seed2.GetCurve()).GetBCurveParams5(true, false, true, false); object c0, k0; pd.GetControlPoints(out c0); pd.GetKnotPoints(out k0);
        Add("(b) seed2 param data: cps " + pd.ControlPointsCount + " knots " + pd.KnotPointsCount);
        try { pd.SetControlPoints(cps); Add("(b) SetControlPoints ok: cps now " + pd.ControlPointsCount); } catch (Exception ex) { Add("(b) SetControlPoints threw " + ex.Message); }
        object r = null; try { r = sm.CreateSplinesByEqnParams2(pd); } catch (Exception ex) { Add("(b) create threw " + ex.Message); }
        var arr = r as object[]; Add("(b) CreateSplinesByEqnParams2 -> " + (arr == null ? "null" : arr.Length + " segment(s)"));
        if (arr != null && arr.Length > 0) Report("(b) created", arr[0] as SketchSegment);
        sm.AddToDB = false; sm.InsertSketch(true);
        return sb.ToString();
    }
}
