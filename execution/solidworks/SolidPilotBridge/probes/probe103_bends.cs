// Probe 103 (round 9): the BENDS of every solid body, measured from the model: every PARTIAL cylinder face (an arc of less than 340 degrees: a full cylinder is a hole) with its axis point and direction,
// radius, arc angle (from its circle edges) and generator length (its longest line edge parallel to the axis). Concentric inner / outer faces of one bend are paired later (outer - inner = thickness).
// Output (one line each, mm / deg): BEND body=<name> o=(x,y,z) d=(dx,dy,dz) r=<radius> ang=<deg> len=<mm>      arg unused.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string F(double v) { return (v * 1000).ToString("0.###", CultureInfo.InvariantCulture); }
    static string U(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder();
        foreach (var bo in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
        {
            var body = bo as IBody2; if (body == null) continue;
            foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
            {
                var face = fo as IFace2; if (face == null) continue;
                var surf = face.IGetSurface(); if (surf == null || !surf.IsCylinder()) continue;
                var cp = surf.CylinderParams as double[]; if (cp == null || cp.Length < 7) continue;       // origin xyz, axis xyz, radius
                double r = cp[6]; if (r <= 0) continue;
                double circ = 0, gen = 0; bool full = false;
                foreach (var eo in (face.GetEdges() as object[]) ?? new object[0])
                {
                    var e = eo as IEdge; if (e == null) continue;
                    var c = e.GetCurve() as ICurve; if (c == null) continue;
                    double a, b; bool closed, periodic; c.GetEndParams(out a, out b, out closed, out periodic);
                    double len = c.GetLength3(a, b);
                    if (c.IsCircle()) { double ang = len / r; if (closed || ang > 340.0 * Math.PI / 180.0) full = true; if (ang > circ) circ = ang; }
                    else if (c.IsLine()) { if (len > gen) gen = len; }
                }
                if (circ <= 0.05)                                                                          // round 9: mitre-flange bends end in ELLIPSE arcs (no circle edge): the angle and length come from the face's UV bounds (u = angle, v = along the axis)
                {
                    var uvb = face.GetUVBounds() as double[];
                    if (uvb != null && uvb.Length >= 4)
                    {
                        double du = uvb[1] - uvb[0];
                        if (du > 340.0 * Math.PI / 180.0) full = true;
                        if (du > 0.05) circ = du;
                        if (gen <= 0) gen = Math.Abs(uvb[3] - uvb[2]);
                    }
                }
                if (full || circ <= 0.05) continue;                                                       // a hole (or a degenerate sliver)
                sb.Append("BEND body=" + body.Name + " o=(" + F(cp[0]) + "," + F(cp[1]) + "," + F(cp[2]) + ") d=(" + U(cp[3]) + "," + U(cp[4]) + "," + U(cp[5]) + ") r=" + F(r) + " ang=" + (circ * 180.0 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture) + " len=" + F(gen) + " ;; ");
            }
        }
        return sb.Length == 0 ? "no bends" : sb.ToString();
    }
}
