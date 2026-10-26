// go/no-go probe 08: Unfold / Fold all bends through the API (IModelDoc2.InsertSheetMetalUnfold / InsertSheetMetalFold) with a preselected fixed face and the bend sub-features.
// arg: "unfold" | "fold"  (variant marks: "unfold:12" uses mark 1 for the face and 2 for the bends; plain "unfold" uses mark 0)
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static int Count(ModelDoc2 doc, string type) { int n = 0; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.GetTypeName2() == type) n++; f = (Feature)f.GetNextFeature(); } return n; }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder(); var sm = (SelectionMgr)doc.SelectionManager;
        bool fold = arg.StartsWith("fold"); bool marks = arg.EndsWith(":12");
        string type = fold ? "Fold" : "UnFold";
        int before = Count(doc, type);
        // fixed face = the big planar back face of the solid body (z ~ 0 outer or 1.524 inner)
        Face2 fixedFace = null; var body = (Body2)((object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false))[0];
        foreach (Face2 f in (object[])body.GetFaces()) { var bx = (double[])f.GetBox(); var s = (Surface)f.GetSurface(); if (s.IsPlane() && Math.Abs(bx[2]) < 1e-4 && Math.Abs(bx[5]) < 1e-4 && bx[0] < 0 && bx[3] > 0 && bx[1] < 0 && bx[4] > 0) fixedFace = f; }   // the PLATE face: planar at z~0 and containing the origin
        if (fixedFace == null) return "fixed face not found";
        var bends = new List<Feature>();
        var cf = (Feature)doc.FirstFeature();
        while (cf != null) { if (cf.GetTypeName2() == "SolidToSheetMetal") { var s = (Feature)cf.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") bends.Add(s); s = (Feature)s.GetNextSubFeature(); } } cf = (Feature)cf.GetNextFeature(); }
        sb.AppendLine("bend features found: " + bends.Count);
        doc.ClearSelection2(true);
        var sd = sm.CreateSelectData(); sd.Mark = marks ? 1 : 0; sb.AppendLine("face selected: " + ((Entity)fixedFace).Select4(false, sd));
        int ok = 0; foreach (var b in bends) if (b.Select2(true, marks ? 2 : 0)) ok++;
        sb.AppendLine("bends selected: " + ok + "  selection count " + sm.GetSelectedObjectCount2(-1));
        if (fold) doc.InsertSheetMetalFold(); else doc.InsertSheetMetalUnfold();
        doc.ClearSelection2(true); doc.EditRebuild3();
        sb.AppendLine(type + " features " + before + " -> " + Count(doc, type));
        var bb = (double[])body.GetBodyBox();
        var after = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false);
        if (after != null) { var ab = (double[])((Body2)after[0]).GetBodyBox(); sb.AppendLine("bbox mm: " + ((ab[3] - ab[0]) * 1000).ToString("F2") + " x " + ((ab[4] - ab[1]) * 1000).ToString("F2") + " x " + ((ab[5] - ab[2]) * 1000).ToString("F2")); }
        return sb.ToString();
    }
}
