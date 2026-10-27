// Probe 67: the faces of the solid body whose box centre lies within a radius of a point (mm) - kind, area, planar normal, box - and, for the pairs of SMALL faces, the minimum distance between them from
// IMeasure (the weld-seam gap between two flanges). arg = "x=125.4;y=208;z=107;r=12;maxarea=60;pairs=1". Folded or flat, whatever the model is in now.
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
    static string F(double v) { return (v * 1000).ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Dictionary<string, string> a, string k, double dflt) { return a.ContainsKey(k) ? double.Parse(a[k], CultureInfo.InvariantCulture) : dflt; }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; double cx = D(a, "x", 0) / 1000, cy = D(a, "y", 0) / 1000, cz = D(a, "z", 0) / 1000, r = D(a, "r", 10) / 1000, maxArea = D(a, "maxarea", 60) / 1e6;
        var faces = new List<Face2>();
        foreach (Body2 b in (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, true))
            foreach (Face2 f in (object[])b.GetFaces())
            {
                var bx = (double[])f.GetBox(); double mx = (bx[0] + bx[3]) / 2, my = (bx[1] + bx[4]) / 2, mz = (bx[2] + bx[5]) / 2;
                if (Math.Sqrt((mx - cx) * (mx - cx) + (my - cy) * (my - cy) + (mz - cz) * (mz - cz)) <= r) faces.Add(f);
            }
        Add("faces within " + F(r) + " mm of (" + F(cx) + "," + F(cy) + "," + F(cz) + "): " + faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i]; var bx = (double[])f.GetBox(); var s = (Surface)f.GetSurface();
            string kind = s.IsPlane() ? "plane" : s.IsCylinder() ? "cylinder" : s.IsCone() ? "cone" : s.IsTorus() ? "torus" : "other"; string nrm = "";
            if (s.IsPlane()) { var n = (double[])f.Normal; nrm = " n(" + n[0].ToString("0.###", CultureInfo.InvariantCulture) + "," + n[1].ToString("0.###", CultureInfo.InvariantCulture) + "," + n[2].ToString("0.###", CultureInfo.InvariantCulture) + ")"; }
            Add("#" + i + " " + kind + nrm + " area " + (f.GetArea() * 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " mm2 box [" + F(bx[0]) + "," + F(bx[1]) + "," + F(bx[2]) + "]..[" + F(bx[3]) + "," + F(bx[4]) + "," + F(bx[5]) + "]");
        }
        if (D(a, "pairs", 0) > 0)
        {
            var small = Enumerable.Range(0, faces.Count).Where(i => faces[i].GetArea() <= maxArea).ToList();
            for (int x = 0; x < small.Count; x++)
                for (int y = x + 1; y < small.Count; y++)
                {
                    doc.ClearSelection2(true); ((Entity)faces[small[x]]).Select4(false, null); ((Entity)faces[small[y]]).Select4(true, null);
                    var m = doc.Extension.CreateMeasure(); m.ArcOption = 0; bool ok = m.Calculate(null);
                    if (ok && m.Distance < 0.003) Add("   distance #" + small[x] + " <-> #" + small[y] + " = " + (m.Distance * 1000).ToString("0.####", CultureInfo.InvariantCulture) + " mm");
                }
            doc.ClearSelection2(true);
        }
        return sb.ToString();
    }
}
