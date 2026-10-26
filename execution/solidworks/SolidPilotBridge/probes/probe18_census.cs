// Probe 18: a chatty read-only traversal comparable to the execution layer's analyze_model 'faces' and 'edges' modes - for EVERY face: surface type, plane normal, area, box; for EVERY edge: curve end params and
// start / mid / end points. Prints counts and wall time of the traversal only. Run in-process (bridge run_cs) and out-of-process (oop_host.ps1) to size the real-world gap on a tool-shaped workload.
using System;
using System.Diagnostics;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder(); double sum = 0; int faces = 0, edges = 0;
        var t = Stopwatch.StartNew();
        foreach (Body2 b in (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false))
        {
            foreach (Face2 f in (object[])b.GetFaces())
            {
                faces++; var s = (Surface)f.GetSurface(); bool plane = s.IsPlane();
                if (plane) { var n = (double[])f.Normal; sum += n[0] + n[1] + n[2]; }
                sum += f.GetArea(); var bx = (double[])f.GetBox(); sum += bx[0];
            }
            foreach (Edge e in (object[])b.GetEdges())
            {
                edges++; var c = (Curve)e.GetCurve(); double s0, s1; bool cl, per; c.GetEndParams(out s0, out s1, out cl, out per);
                var p0 = (double[])c.Evaluate2(s0, 0); var pm = (double[])c.Evaluate2((s0 + s1) / 2, 0); var p1 = (double[])c.Evaluate2(s1, 0);
                sum += p0[0] + pm[1] + p1[2];
            }
        }
        t.Stop();
        sb.AppendLine("faces " + faces + " edges " + edges + " checksum " + sum.ToString("F6") + " traversal " + t.ElapsedMilliseconds + " ms");
        return sb.ToString();
    }
}
