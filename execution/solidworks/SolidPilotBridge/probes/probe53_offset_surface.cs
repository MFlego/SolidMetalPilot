// Probe 53: IModelDoc2.InsertOffsetSurface - what does it need selected, what does it create, which way does Reverse go?
// Needs an active part with ONE surface body (box with a deleted face). arg = "body" | "face" | "faces" (what to select), optionally ",rev" to pass Reverse = true; prints the new features / bodies / boxes.
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static string Box(Body2 b) { var x = (double[])b.GetBodyBox(); return ((x[3] - x[0]) * 1000).ToString("F3") + " x " + ((x[4] - x[1]) * 1000).ToString("F3") + " x " + ((x[5] - x[2]) * 1000).ToString("F3"); }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear();
        var parts = (arg ?? "body").Split(','); string what = parts[0]; bool rev = parts.Length > 1 && parts[1] == "rev";
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc;
        var before = new HashSet<string>(); var f = (Feature)doc.FirstFeature(); while (f != null) { before.Add(f.Name); f = (Feature)f.GetNextFeature(); }
        var sheets = (object[])part.GetBodies2((int)swBodyType_e.swSheetBody, false); if (sheets == null || sheets.Length == 0) return "no surface body";
        var body = (Body2)sheets[0]; Add("body box " + Box(body) + ", faces " + body.GetFaceCount());
        doc.ClearSelection2(true); bool ok = false;
        if (what == "body") ok = body.Select2(false, null);
        else if (what == "face") { var faces = (object[])body.GetFaces(); ok = ((Entity)faces[0]).Select4(false, null); }
        else { bool app = false; foreach (Face2 fa in (object[])body.GetFaces()) { ok = ((Entity)fa).Select4(app, null); app = true; } }
        Add("selected (" + what + ") = " + ok + ", count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1) + ", type " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectType3(1, -1));
        doc.InsertOffsetSurface(1.5 / 1000.0, rev);
        doc.ClearSelection2(true); doc.EditRebuild3();
        f = (Feature)doc.FirstFeature(); while (f != null) { if (!before.Contains(f.Name)) Add("NEW feature " + f.Name + " type " + f.GetTypeName2()); f = (Feature)f.GetNextFeature(); }
        var after = (object[])part.GetBodies2((int)swBodyType_e.swAllBodies, false); Add("bodies now " + (after == null ? 0 : after.Length));
        if (after != null) foreach (Body2 b in after) Add("  body type " + b.GetType() + " box " + Box(b) + " visible " + b.Visible);
        return sb.ToString();
    }
}
