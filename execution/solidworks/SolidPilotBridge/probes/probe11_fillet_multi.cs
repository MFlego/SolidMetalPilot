// Probe 11: ONE multi-radius fillet on the 12 corner edges of the unfolded blank (the reference part's recipe: r 10 / 15 / 5 mm), created typed in-process with FeatureFillet3.
// arg "r10,r15,r5" overrides the three radii in mm (default 10,15,5)
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static bool Near(double a, double b) { return Math.Abs(a - b) < 0.06; }
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sm = (SelectionMgr)doc.SelectionManager; var fm = doc.FeatureManager; var sb = new StringBuilder();
        var rr = (string.IsNullOrEmpty(arg) ? "10,15,5" : arg).Split(','); double rA = double.Parse(rr[0]), rB = double.Parse(rr[1]), rC = double.Parse(rr[2]);
        var body = (Body2)((object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false))[0];
        // corner families: A (152.40, 230.88)  B (152.40, 301.24)  C (127.00, 334.51)  - all four sign combinations; z-parallel edges of length = thickness
        var want = new[] { new[] { 152.40, 230.88, rA }, new[] { 152.40, 301.24, rB }, new[] { 127.00, 334.51, rC } };
        var edges = new List<Edge>(); var radii = new List<double>();
        foreach (var w in want)
            for (int sx = -1; sx <= 1; sx += 2) for (int sy = -1; sy <= 1; sy += 2)
                foreach (Edge e in (object[])body.GetEdges())
                {
                    var cv = (Curve)e.GetCurve(); if (!cv.IsLine()) continue;
                    double s, en; bool cl, per; cv.GetEndParams(out s, out en, out cl, out per); var a = (double[])cv.Evaluate2(s, 0); var b = (double[])cv.Evaluate2(en, 0);
                    if (Math.Abs(a[0] - b[0]) * 1000 > 0.01 || Math.Abs(a[1] - b[1]) * 1000 > 0.01 || Math.Abs(Math.Abs(a[2] - b[2]) * 1000 - 1.524) > 0.02) continue;
                    if (Near(a[0] * 1000, sx * w[0]) && Near(a[1] * 1000, sy * w[1])) { edges.Add(e); radii.Add(w[2] / 1000.0); break; }
                }
        sb.AppendLine("edges matched: " + edges.Count + " of 12");
        if (edges.Count != 12) return sb.ToString() + "stopped: expected 12 edges";
        int before = 0; var f0 = (Feature)doc.FirstFeature(); while (f0 != null) { before++; f0 = (Feature)f0.GetNextFeature(); }
        doc.ClearSelection2(true); var sd = sm.CreateSelectData(); sd.Mark = 1;
        foreach (var e in edges) ((Entity)e).Select4(true, sd);
        sb.AppendLine("selected " + sm.GetSelectedObjectCount2(-1));
        var feat = fm.FeatureFillet3(0, rA / 1000.0, 0, 0, (int)swFeatureFilletType_e.swFeatureFilletType_Simple, 0, 0, radii.ToArray(), null, null, null, null, null, null) as Feature;
        sb.AppendLine("FeatureFillet3 -> " + (feat == null ? "null" : feat.Name));
        doc.ClearSelection2(true); doc.EditRebuild3();
        int after = 0; f0 = (Feature)doc.FirstFeature(); string last = ""; while (f0 != null) { after++; last = f0.Name + " [" + f0.GetTypeName2() + "]"; f0 = (Feature)f0.GetNextFeature(); }
        sb.AppendLine("features " + before + " -> " + after + " last " + last);
        if (feat != null)
        {
            var d = (ISimpleFilletFeatureData2)feat.GetDefinition(); sb.AppendLine("definition: multiRadius=" + d.IsMultipleRadius + " edges=" + d.GetEdgeCount());
            if (d.AccessSelections(doc, null)) { for (int k = 0; k < d.GetEdgeCount(); k++) { var o = d.GetFilletItemAtIndex(k); sb.Append(" r" + k + "=" + (d.GetRadius(o) * 1000).ToString("F1")); } d.ReleaseSelectionAccess(); }
        }
        return sb.ToString();
    }
}
