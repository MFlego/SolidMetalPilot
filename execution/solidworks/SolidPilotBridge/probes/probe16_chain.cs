// go/no-go probe 16: feature CREATION with selection marks, A/B. On a throw-away copy of the cabinet part (opened silent, never saved):
// Unfold (fixed face mark 1 + bend sub-features mark 2) -> ONE multi-radius Fillet (FeatureFillet3, 12 corner edges, radii 10/15/5 mm) -> Fold, reporting feature counts, bounding boxes and the fillet radii read back.
// arg = full path of the copy (a scratch copy, so that it is not the document that is already open)
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static int Count(ModelDoc2 doc, string type) { int n = 0; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.GetTypeName2() == type) n++; f = (Feature)f.GetNextFeature(); } return n; }
    static bool Near(double a, double b) { return Math.Abs(a - b) < 0.06; }
    static Body2 Body(PartDoc part) { var bs = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false); return bs == null ? null : (Body2)bs[0]; }
    static string Box(PartDoc part) { var b = Body(part); if (b == null) return "no body"; var x = (double[])b.GetBodyBox(); return ((x[3] - x[0]) * 1000).ToString("F2") + " x " + ((x[4] - x[1]) * 1000).ToString("F2") + " x " + ((x[5] - x[2]) * 1000).ToString("F2"); }

    // the PLATE face: planar at z ~ 0 and containing the origin
    static Face2 PlateFace(PartDoc part)
    {
        Face2 res = null;
        foreach (Face2 f in (object[])Body(part).GetFaces())
        {
            var bx = (double[])f.GetBox(); var s = (Surface)f.GetSurface();
            if (s.IsPlane() && Math.Abs(bx[2]) < 1e-4 && Math.Abs(bx[5]) < 1e-4 && bx[0] < 0 && bx[3] > 0 && bx[1] < 0 && bx[4] > 0) res = f;
        }
        return res;
    }

    static string UnfoldFold(ModelDoc2 doc, PartDoc part, bool fold)
    {
        var sm = (SelectionMgr)doc.SelectionManager; string type = fold ? "Fold" : "UnFold"; int before = Count(doc, type);
        var face = PlateFace(part); if (face == null) return type + ": plate face not found";
        var bends = new List<Feature>(); var cf = (Feature)doc.FirstFeature();
        while (cf != null) { if (cf.GetTypeName2() == "SolidToSheetMetal") { var s = (Feature)cf.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") bends.Add(s); s = (Feature)s.GetNextSubFeature(); } } cf = (Feature)cf.GetNextFeature(); }
        doc.ClearSelection2(true);
        var sd = sm.CreateSelectData(); sd.Mark = 1; bool fOk = ((Entity)face).Select4(false, sd);
        int ok = 0; foreach (var b in bends) if (b.Select2(true, 2)) ok++;
        string sel = "face " + fOk + ", bends " + ok + "/" + bends.Count + ", selection count " + sm.GetSelectedObjectCount2(-1);
        if (fold) doc.InsertSheetMetalFold(); else doc.InsertSheetMetalUnfold();
        doc.ClearSelection2(true); doc.EditRebuild3();
        return type + ": " + sel + "; features " + before + " -> " + Count(doc, type) + "; body " + Box(part);
    }

    static string Fillet(ModelDoc2 doc, PartDoc part)
    {
        var sm = (SelectionMgr)doc.SelectionManager; var fm = doc.FeatureManager; double rA = 10, rB = 15, rC = 5;
        var body = Body(part);
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
        if (edges.Count != 12) return "Fillet: matched " + edges.Count + " of 12 edges - stopped";
        doc.ClearSelection2(true); var sd = sm.CreateSelectData(); sd.Mark = 1;
        foreach (var e in edges) ((Entity)e).Select4(true, sd);
        var feat = fm.FeatureFillet3(0, rA / 1000.0, 0, 0, (int)swFeatureFilletType_e.swFeatureFilletType_Simple, 0, 0, radii.ToArray(), null, null, null, null, null, null) as Feature;
        doc.ClearSelection2(true); doc.EditRebuild3();
        if (feat == null) return "Fillet: FeatureFillet3 -> null";
        var sb = new StringBuilder("Fillet: " + feat.Name + "; radii");
        var d = (ISimpleFilletFeatureData2)feat.GetDefinition(); sb.Append(" multiRadius=" + d.IsMultipleRadius + " edges=" + d.GetEdgeCount() + ":");
        if (d.AccessSelections(doc, null)) { for (int k = 0; k < d.GetEdgeCount(); k++) { var o = d.GetFilletItemAtIndex(k); sb.Append(" " + (d.GetRadius(o) * 1000).ToString("F1")); } d.ReleaseSelectionAccess(); }
        return sb.ToString() + "; body " + Box(part);
    }

    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); var original = sw.ActiveDoc as ModelDoc2; string originalTitle = original == null ? null : original.GetTitle();
        int err = 0, warn = 0;
        var doc = sw.OpenDoc6(arg, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        if (doc == null) return "open failed err=" + err + " warn=" + warn;
        try
        {
            var part = (PartDoc)doc;
            sb.AppendLine("start: body " + Box(part));
            sb.AppendLine(UnfoldFold(doc, part, false));
            sb.AppendLine(Fillet(doc, part));
            sb.AppendLine(UnfoldFold(doc, part, true));
        }
        finally
        {
            sw.CloseDoc(doc.GetTitle());
            if (originalTitle != null) { int e2 = 0; sw.ActivateDoc3(originalTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2); }
        }
        return sb.ToString();
    }
}
