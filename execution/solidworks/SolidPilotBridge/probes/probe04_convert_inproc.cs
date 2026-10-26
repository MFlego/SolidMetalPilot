// go/no-go probe 04: does Convert to Sheet Metal on the SURFACE base shape work when called IN-PROCESS with typed API access?
// Out-of-process (PowerShell typed interop) results: InsertConvertToSheetMetal2 half-built (Sheet-Metal + Flat-Pattern, no Convert feature); CreateDefinition+CreateFeature returned null;
// ModifyDefinition on bend edges raised RPC_E_SERVERFAULT.  arg = variant:  "def" (CreateDefinition/CreateFeature) | "insert" (InsertConvertToSheetMetal2 with marks 1/2/4) | "defnorip" (def without rips)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    const double ZBack = 1.524, ZRim = 81.026, OHX = 150.876, OHY = 227.076, IHX = 125.476, IHY = 208.026;   // mm, this part
    static bool Near(double a, double b) { return Math.Abs(a - b) < 0.02; }
    static double[] Mid(Edge e) { var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per); var p = (double[])c.Evaluate2((s + en) / 2, 0); return new[] { p[0] * 1000, p[1] * 1000, p[2] * 1000 }; }

    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager; var sm = (SelectionMgr)doc.SelectionManager;
        string variant = string.IsNullOrEmpty(arg) ? "def" : arg;
        Body2 body = null;
        foreach (Body2 b in (object[])part.GetBodies2((int)swBodyType_e.swSheetBody, false)) { var bb = (double[])b.GetBodyBox(); if (bb[2] * 1000 > 0.01) body = b; }
        if (body == null) return "offset body not found";
        Face2 fixedFace = null;
        foreach (Face2 f in (object[])body.GetFaces()) { var bx = (double[])f.GetBox(); if (Near(bx[2] * 1000, ZBack) && Near(bx[5] * 1000, ZBack) && (bx[3] - bx[0]) * 1000 > 200) fixedFace = f; }
        var s1 = new List<Edge>(); var s2 = new List<Edge>(); var s3 = new List<Edge>();
        foreach (Edge e in (object[])body.GetEdges())
        {
            var m = Mid(e);
            if (Near(m[2], ZBack)) s1.Add(e);
            else if (Near(m[2], ZRim) && (Near(Math.Abs(m[0]), OHX) || Near(Math.Abs(m[1]), OHY))) s2.Add(e);
            else if (Near(m[2], ZRim) && (Near(Math.Abs(m[0]), IHX) || Near(Math.Abs(m[1]), IHY))) s3.Add(e);
        }
        sb.AppendLine("edges " + s1.Count + "/" + s2.Count + "/" + s3.Count + "  fixed face " + (fixedFace != null));
        var all = new List<object>(); all.AddRange(s1); all.AddRange(s2); all.AddRange(s3);
        var rips = new List<object>(); foreach (var n in new[] { "Sketch30", "Sketch31", "Sketch32", "Sketch33" }) rips.Add(part.FeatureByName(n));
        int before = Count(doc);

        if (variant == "def" || variant == "defnorip")
        {
            var d = (IConvertSolidFeatureData)fm.CreateDefinition((int)swFeatureNameID_e.swFmSolidToSheetMetal);
            sb.AppendLine("definition: " + (d == null ? "NULL" : "ok"));
            d.Initialize(false, false, null);
            d.OverrideSheetThickness = true; d.SheetThickness = 0.001524; d.ReverseThickness = true; d.OverrideBendRadius = true; d.BendRadius = 0.004572; d.KeepBody = false;
            d.RipGap = 0.0000762; d.RipOverlapRatio = 1.0; d.CornerDefaults = 1; d.ReliefType = 2; d.ReliefRatio = 0.5;
            d.SetFixedFace(fixedFace);
            var bw = new DispatchWrapper[all.Count]; for (int i = 0; i < all.Count; i++) bw[i] = new DispatchWrapper(all[i]);
            d.BendEdges = bw;
            if (variant == "def") { var rw = new DispatchWrapper[rips.Count]; for (int i = 0; i < rips.Count; i++) rw[i] = new DispatchWrapper(rips[i]); d.RipSketches = rw; }
            var f = fm.CreateFeature(d) as Feature;
            sb.AppendLine("CreateFeature -> " + (f == null ? "null" : f.Name));
        }
        else if (variant == "insert")
        {
            doc.ClearSelection2(true);
            var sd = sm.CreateSelectData();
            sd.Mark = 1; ((Entity)fixedFace).Select4(false, sd);
            sd.Mark = 2; foreach (var e in s1) ((Entity)e).Select4(true, sd);
            foreach (Feature rf in rips) rf.Select2(true, 4);
            sd.Mark = 2; foreach (var e in s2) ((Entity)e).Select4(true, sd); foreach (var e in s3) ((Entity)e).Select4(true, sd);
            sb.AppendLine("selection count " + sm.GetSelectedObjectCount2(-1));
            bool ok = fm.InsertConvertToSheetMetal2(0.001524, true, false, 0.004572, 0.0000762, 2, 0.5, 1, 1.0, false);
            sb.AppendLine("InsertConvertToSheetMetal2 -> " + ok);
        }
        else return "unknown variant " + variant;

        doc.EditRebuild3();
        sb.AppendLine("features " + before + " -> " + Count(doc));
        int solids = 0, faces = 0;
        var sol = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        if (sol != null) { solids = sol.Length; foreach (Body2 b in sol) faces += b.GetFaceCount(); }
        sb.AppendLine("solid bodies " + solids + " faces " + faces);
        var mp = doc.Extension.CreateMassProperty() as MassProperty; if (mp != null && solids > 0) sb.AppendLine("volume mm3 " + (mp.Volume * 1e9).ToString("F0") + "  (back plate alone = 196900)");
        return sb.ToString();
    }

    static int Count(ModelDoc2 doc) { int n = 0; var f = (Feature)doc.FirstFeature(); while (f != null) { n++; f = (Feature)f.GetNextFeature(); } return n; }
}
