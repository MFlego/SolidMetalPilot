// Probe 20 (set-up for the bend-less Convert experiments): open the scratch COPY of the cabinet part, delete its Convert feature with all dependents (the model returns to: offset surface body + rip sketches),
// rebuild, and preselect the FIXED FACE (back plate of the offset surface body, mark 1). The document is left OPEN and ACTIVE - the caller closes it (probe21 with arg "close", or CloseDoc).
// arg = path of the scratch copy
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static bool Near(double a, double b) { return Math.Abs(a - b) < 0.02; }

    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); int err = 0, warn = 0;
        var doc = sw.OpenDoc6(arg, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        if (doc == null) return "open failed err=" + err + " warn=" + warn;
        var part = (PartDoc)doc; var sm = (SelectionMgr)doc.SelectionManager;
        Feature cf = null; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.GetTypeName2() == "SolidToSheetMetal") cf = f; f = (Feature)f.GetNextFeature(); }
        if (cf == null) return "no Convert feature in the copy";
        string name = cf.Name; doc.ClearSelection2(true);
        bool selected = cf.Select2(false, 0);
        // dependents only: the absorbed rip sketches (Sketch30..33) must survive as top-level sketches, exactly as they stood before the Convert was created
        bool deleted = doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Children);
        doc.EditRebuild3();
        sb.AppendLine("deleted " + name + ": select " + selected + ", delete " + deleted);
        int i = 0; f = (Feature)doc.FirstFeature(); var tree = new StringBuilder();
        while (f != null) { i++; if (i > 17) tree.Append(i + " " + f.Name + " [" + f.GetTypeName2() + "]  "); f = (Feature)f.GetNextFeature(); }
        sb.AppendLine("tree: " + tree);
        Body2 surf = null; foreach (Body2 b in (object[])part.GetBodies2(1, false)) { var bb = (double[])b.GetBodyBox(); if (bb[2] * 1000 > 0.01) surf = b; }
        if (surf == null) return sb + "offset surface body not found";
        Face2 fixedFace = null;
        foreach (Face2 fc in (object[])surf.GetFaces()) { var bx = (double[])fc.GetBox(); if (Near(bx[2] * 1000, 1.524) && Near(bx[5] * 1000, 1.524) && (bx[3] - bx[0]) * 1000 > 200) fixedFace = fc; }
        if (fixedFace == null) return sb + "fixed (back) face not found";
        doc.ClearSelection2(true); var sd = sm.CreateSelectData(); sd.Mark = 1;
        sb.AppendLine("surface body faces " + surf.GetFaceCount() + " edges " + surf.GetEdgeCount() + "; fixed face selected (mark 1): " + ((Entity)fixedFace).Select4(false, sd) + "; selection count " + sm.GetSelectedObjectCount2(-1));
        sb.AppendLine("active document: " + ((ModelDoc2)sw.ActiveDoc).GetTitle());
        return sb.ToString();
    }
}
