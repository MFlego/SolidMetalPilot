// Probe 70: the vertices (x, y, z in METERS, 12 digits) of the solid body within a radius of a point, and the edges between them with their curve type and exact length - the exact end points of a corner curve
// that analyze_model rounds to a micron. arg = "x=247.9;y=212.6;r=1.5" (mm).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string D(double v) { return v.ToString("0.000000000000", CultureInfo.InvariantCulture); }

    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        double cx = double.Parse(a["x"], CultureInfo.InvariantCulture) / 1000, cy = double.Parse(a["y"], CultureInfo.InvariantCulture) / 1000, r = (a.ContainsKey("r") ? double.Parse(a["r"], CultureInfo.InvariantCulture) : 1.5) / 1000;
        var part = (PartDoc)sw.ActiveDoc; var bodies = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false);
        foreach (Body2 b in bodies)
            foreach (Edge e in (object[])b.GetEdges())
            {
                var v1 = (Vertex)e.GetStartVertex(); var v2 = (Vertex)e.GetEndVertex(); if (v1 == null || v2 == null) continue;
                var p1 = (double[])v1.GetPoint(); var p2 = (double[])v2.GetPoint();
                bool n1 = Math.Sqrt((p1[0] - cx) * (p1[0] - cx) + (p1[1] - cy) * (p1[1] - cy)) < r, n2 = Math.Sqrt((p2[0] - cx) * (p2[0] - cx) + (p2[1] - cy) * (p2[1] - cy)) < r;
                if (!n1 && !n2) continue; if (Math.Abs(p1[2]) > 1e-9 || Math.Abs(p2[2]) > 1e-9) continue;       // the z = 0 face only
                var c = (Curve)e.GetCurve(); string kind = c.IsLine() ? "line" : c.IsCircle() ? "circle" : c.IsEllipse() ? "ellipse" : c.IsBcurve() ? "bcurve" : "other";
                double len = Math.Sqrt((p2[0] - p1[0]) * (p2[0] - p1[0]) + (p2[1] - p1[1]) * (p2[1] - p1[1]));      // the chord (a curved edge is longer)
                sb.Append("edge " + kind + " length " + D(len) + "  (" + D(p1[0]) + ", " + D(p1[1]) + ") -> (" + D(p2[0]) + ", " + D(p2[1]) + ") ;; ");
            }
        return sb.ToString();
    }
}
