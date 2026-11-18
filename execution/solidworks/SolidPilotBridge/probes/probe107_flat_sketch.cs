// METADATA SKETCH in the FLAT pattern (round 10, user D2: process-only features - grab plate, nubs, setbacks, ticks - live as sketches in the flat that are exported to DXF with the flat pattern: the manufacturer cuts the
// flat WITH the process feature and uses the folded / rolled model for the final trim). Unsuppresses the named Flat-Pattern, opens a sketch on the flat body's LARGEST PLANAR FACE and draws RECTANGLES placed in the
// flat's own frame: u = the direction of the face's longest edge, v = normal x u, origin = the corner of the face's (u, v) bounding box. Export the DXF with SheetMetalOptions bit 4 (sketches): flat_pattern_control dxf_flags=13.
// arg = "<Flat-Pattern name>|<sketch name>|<fold 0/1>|rect:<anchor_u>,<du>,<anchor_v>,<dv>,<w>,<h>;rect:..."
//   edge:<length_mm>,<tol_mm>,<depth_mm>  = one rectangle on EVERY straight edge of the flat face whose length is within tol of length_mm, projecting OUTWARD (away from the face's centre) by depth_mm: a grab plate on the end generators of a curved band.
//   anchor_u / anchor_v = min | max | mid of the blank's bounding box along u / v; the rectangle's lower-left corner is at (anchor + du, anchor + dv) mm, width w along u, height h along v (negative offsets reach OUTSIDE the blank:
//   a grab plate is a rectangle beyond the end of the strip). fold = 1 suppresses the flat pattern again after the sketch (the sketch belongs to it). Result: the bbox in (u, v), the sketch feature, entities drawn.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var a = arg.Split('|'); if (a.Length < 4) return "arg = flat|sketch name|fold 0/1|rect:...;rect:...";
        string flatName = a[0], sketchName = a[1]; bool fold = a[2] == "1";
        var doc = sw.ActiveDoc as ModelDoc2; if (doc == null) return "no active document";
        var sb = new StringBuilder();
        Feature fp = null; var f = doc.FirstFeature() as Feature;
        while (f != null) { if (f.GetTypeName2() == "FlatPattern" && f.Name == flatName) { fp = f; break; } f = f.GetNextFeature() as Feature; }
        if (fp == null) return "no flat pattern " + flatName;
        bool was = fp.IsSuppressed();
        if (was) { fp.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); doc.EditRebuild3(); }
        bool w0 = false; int err = fp.GetErrorCode2(out w0); if (err != 0) return sb.Append("the flat pattern does not rebuild (error " + err + ")").ToString();
        // the flat body = the body of the flat pattern's fixed face
        Body2 body = null;
        try { var d = fp.GetDefinition() as IFlatPatternFeatureData; var ff = d != null ? d.FixedFace2 as Face2 : null; if (ff != null) body = ff.GetBody() as Body2; } catch (Exception) { }
        if (body == null)                                                       // some flats (the stairs stringers) give no body without access: open the definition's selections
        {
            try { var d2 = fp.GetDefinition() as IFlatPatternFeatureData; if (d2 != null) { d2.AccessSelections(doc, null); var ff2 = d2.FixedFace2 as Face2; if (ff2 != null) body = ff2.GetBody() as Body2; sb.Append("fixed face via AccessSelections: " + (body != null) + " ;; "); } } catch (Exception ex) { sb.Append("access failed: " + ex.Message + " ;; "); }
        }
        if (body == null)                                                       // last resort: the sheet body that carries the flat-pattern's largest planar face nearest the flat feature's bounding box centre is not known: take the only visible solid body whose faces are all planar
        {
            var part2 = sw.ActiveDoc as IPartDoc; var bs = part2.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bs != null) foreach (var bo in bs) { var b2 = bo as Body2; if (b2 == null) continue; bool allPlanar = true; foreach (var fo in (object[])b2.GetFaces()) { var sf = ((Face2)fo).IGetSurface(); if (sf == null || !sf.IsPlane()) { allPlanar = false; break; } } if (allPlanar) { body = b2; sb.Append("flat body = the visible all-planar body " + b2.Name + " ;; "); break; } }
        }
        if (body == null) { if (was) { fp.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); doc.EditRebuild3(); sb.Append("flat pattern suppressed again ;; "); } return sb.Append("flat body not found").ToString(); }          // never leave the flat unfolded in the saved part (it crashed SolidWorks on the next open)
        Face2 big = null; double barea = 0; double[] pl = null;
        foreach (var o in (object[])body.GetFaces())
        {
            var fc = o as Face2; var sf = fc.IGetSurface(); if (sf == null || !sf.IsPlane()) continue;
            double ar = fc.GetArea(); if (ar > barea) { barea = ar; big = fc; pl = (double[])sf.PlaneParams; }
        }
        if (big == null) return sb.Append("no planar face").ToString();
        double[] n = new double[] { pl[0], pl[1], pl[2] }, o0 = new double[] { pl[3], pl[4], pl[5] };
        // u = direction of the longest edge of the face
        double[] U = null; double best = 0; var pts = new List<double[]>();
        foreach (var eo in (object[])big.GetEdges())
        {
            var e = eo as Edge; var v0 = e.GetStartVertex() as Vertex; var v1 = e.GetEndVertex() as Vertex; if (v0 == null || v1 == null) continue;
            var p0 = (double[])v0.GetPoint(); var p1 = (double[])v1.GetPoint(); pts.Add(p0); pts.Add(p1);
            double l = Math.Sqrt(Math.Pow(p1[0] - p0[0], 2) + Math.Pow(p1[1] - p0[1], 2) + Math.Pow(p1[2] - p0[2], 2));
            if (l > best) { best = l; U = new double[] { (p1[0] - p0[0]) / l, (p1[1] - p0[1]) / l, (p1[2] - p0[2]) / l }; }
        }
        if (U == null) return sb.Append("no edge").ToString();
        double[] V = new double[] { n[1] * U[2] - n[2] * U[1], n[2] * U[0] - n[0] * U[2], n[0] * U[1] - n[1] * U[0] };
        double umin = 1e9, umax = -1e9, vmin = 1e9, vmax = -1e9;
        foreach (var p in pts)
        {
            double pu = (p[0] - o0[0]) * U[0] + (p[1] - o0[1]) * U[1] + (p[2] - o0[2]) * U[2];
            double pv = (p[0] - o0[0]) * V[0] + (p[1] - o0[1]) * V[1] + (p[2] - o0[2]) * V[2];
            umin = Math.Min(umin, pu); umax = Math.Max(umax, pu); vmin = Math.Min(vmin, pv); vmax = Math.Max(vmax, pv);
        }
        sb.Append(string.Format(CultureInfo.InvariantCulture, "blank bbox in (u,v): u {0:F2}..{1:F2} v {2:F2}..{3:F2} mm (face area {4:F0} mm2) ;; ", umin * 1000, umax * 1000, vmin * 1000, vmax * 1000, barea * 1e6));
        doc.ClearSelection2(true);
        if (!((Entity)big).Select4(false, null)) return sb.Append("could not select the flat face").ToString();
        doc.SketchManager.InsertSketch(true);
        var sk = doc.SketchManager.ActiveSketch as Sketch; if (sk == null) return sb.Append("no active sketch").ToString();
        var mu = (MathUtility)sw.GetMathUtility(); var tr = sk.ModelToSketchTransform;
        Func<double, double, double[]> toSk = delegate (double pu, double pv)
        {
            var mp = (MathPoint)mu.CreatePoint(new double[] { o0[0] + pu * U[0] + pv * V[0], o0[1] + pu * U[1] + pv * V[1], o0[2] + pu * U[2] + pv * V[2] });
            mp = (MathPoint)mp.MultiplyTransform(tr); return (double[])mp.ArrayData;
        };
        int drawn = 0;
        foreach (var spec in a[3].Split(';').Where(x => x.StartsWith("rect:")))
        {
            var c = spec.Substring(5).Split(',');
            double au = c[0] == "min" ? umin : c[0] == "max" ? umax : (umin + umax) / 2, av = c[2] == "min" ? vmin : c[2] == "max" ? vmax : (vmin + vmax) / 2;
            double x0 = au + double.Parse(c[1], CultureInfo.InvariantCulture) / 1000, y0 = av + double.Parse(c[3], CultureInfo.InvariantCulture) / 1000;
            double x1 = x0 + double.Parse(c[4], CultureInfo.InvariantCulture) / 1000, y1 = y0 + double.Parse(c[5], CultureInfo.InvariantCulture) / 1000;
            var P = new[] { toSk(x0, y0), toSk(x1, y0), toSk(x1, y1), toSk(x0, y1) };
            for (int i = 0; i < 4; i++) { var q = P[i]; var r = P[(i + 1) % 4]; if (doc.SketchManager.CreateLine(q[0], q[1], 0, r[0], r[1], 0) != null) drawn++; }
        }
        foreach (var spec in a[3].Split(';').Where(x => x.StartsWith("edge:")))
        {
            var c = spec.Substring(5).Split(',');
            double Ltarget = double.Parse(c[0], CultureInfo.InvariantCulture) / 1000, tol = double.Parse(c[1], CultureInfo.InvariantCulture) / 1000, depth = double.Parse(c[2], CultureInfo.InvariantCulture) / 1000;
            double cx = 0, cy = 0, cz = 0; foreach (var p in pts) { cx += p[0]; cy += p[1]; cz += p[2]; } cx /= pts.Count; cy /= pts.Count; cz /= pts.Count;
            foreach (var eo in (object[])big.GetEdges())
            {
                var e = eo as Edge; var cv = e.GetCurve() as Curve; if (cv == null || !cv.IsLine()) continue;
                var v0 = e.GetStartVertex() as Vertex; var v1 = e.GetEndVertex() as Vertex; if (v0 == null || v1 == null) continue;
                var p0 = (double[])v0.GetPoint(); var p1 = (double[])v1.GetPoint();
                double l = Math.Sqrt(Math.Pow(p1[0] - p0[0], 2) + Math.Pow(p1[1] - p0[1], 2) + Math.Pow(p1[2] - p0[2], 2)); if (Math.Abs(l - Ltarget) > tol) continue;
                double[] d = new double[] { (p1[0] - p0[0]) / l, (p1[1] - p0[1]) / l, (p1[2] - p0[2]) / l };
                double[] o = new double[] { n[1] * d[2] - n[2] * d[1], n[2] * d[0] - n[0] * d[2], n[0] * d[1] - n[1] * d[0] };
                double side = (cx - p0[0]) * o[0] + (cy - p0[1]) * o[1] + (cz - p0[2]) * o[2]; if (side > 0) { o[0] = -o[0]; o[1] = -o[1]; o[2] = -o[2]; }          // outward = away from the centre
                var Q = new double[][] { p0, p1, new double[] { p1[0] + o[0] * depth, p1[1] + o[1] * depth, p1[2] + o[2] * depth }, new double[] { p0[0] + o[0] * depth, p0[1] + o[1] * depth, p0[2] + o[2] * depth } };
                var S = new List<double[]>();
                foreach (var q in Q) { var mp = (MathPoint)mu.CreatePoint(q); mp = (MathPoint)mp.MultiplyTransform(tr); S.Add((double[])mp.ArrayData); }
                for (int i = 0; i < 4; i++) { var q = S[i]; var r = S[(i + 1) % 4]; if (doc.SketchManager.CreateLine(q[0], q[1], 0, r[0], r[1], 0) != null) drawn++; }
                sb.Append(string.Format(CultureInfo.InvariantCulture, "edge {0:F2} mm: grab plate {1:F2} outward ;; ", l * 1000, depth * 1000));
            }
        }
        doc.SketchManager.InsertSketch(true);                                   // leaves the sketch
        Feature sf2 = doc.FeatureByPositionReverse(0) as Feature;
        if (sf2 != null && sf2.GetTypeName2() == "ProfileFeature" && !string.IsNullOrEmpty(sketchName)) sf2.Name = sketchName;
        sb.Append("sketch: " + (sf2 != null ? sf2.Name : "?") + ", " + drawn + " lines drawn ;; ");
        if (fold && was) { fp.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); doc.EditRebuild3(); sb.Append("flat pattern suppressed again ;; "); }
        return sb.ToString();
    }
}
