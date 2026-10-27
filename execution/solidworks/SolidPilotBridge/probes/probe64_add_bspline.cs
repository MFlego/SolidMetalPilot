// NOTE: a hand-filled SplineParamData does NOT work - the counts stay -1 and CreateSplinesByEqnParams2 returns null (no crash). The related probe 65 DID crash SolidWorks; see its header.
// Probe 64: create an EXACT B-spline in the ACTIVE sketch from SplineParamData (order, knots, control points in meters) - ISketchManager.CreateSplineParamData + CreateSplinesByEqnParams2.
// arg = "order=4;dim=3;knots=0,0,0,0,0.638245,1,1,1,1;cps=x0,y0,z0,x1,y1,z1,..."  (invariant culture numbers)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static double[] D(string s) { return s.Split(',').Select(x => double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray(); }

    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var sm = doc.SketchManager; if (sm.ActiveSketch == null) return "no active sketch";
        var pd = (SplineParamData)sm.CreateSplineParamData();
        pd.Order = int.Parse(a["order"]); pd.Dimension = a.ContainsKey("dim") ? int.Parse(a["dim"]) : 3; pd.Periodic = 0;
        var knots = D(a["knots"]); var cps = D(a["cps"]);
        pd.SetKnotPoints(knots); pd.SetControlPoints(cps);
        bool prev = sm.AddToDB; sm.AddToDB = true;      // no inference / snapping while the geometry is written
        try
        {
            object res = sm.CreateSplinesByEqnParams2(pd);
            var arr = res as object[]; int n = arr == null ? 0 : arr.Length;
            return "CreateSplinesByEqnParams2 -> " + (res == null ? "null" : n + " segment(s)") + "; order " + pd.Order + ", " + pd.ControlPointsCount + " control points, " + pd.KnotPointsCount + " knots";
        }
        finally { sm.AddToDB = prev; }
    }
}
