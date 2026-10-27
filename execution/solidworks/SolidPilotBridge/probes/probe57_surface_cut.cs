// Probe 57: SURFACE EXTRUDE of an open sketch (a ribbon) + SURFACE CUT of the solid with it - what has to be selected, what comes back, which side goes. The toy: a 100 x 60 x 20 box and a sketch whose line
// x + y = 70 mm cuts the corner (40,30)-(50,20)-(50,30) off: the cut volume is (1/2) 10 x 10 x 20 = 1000 mm3.
// Needs: an active part with the solid box and an exited OPEN sketch named in arg. arg = "sketch=Sketch2;depth=0.03;reverse=0;flip=0;keep=0;sel=feature|body|both;scope=0|1"
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static Dictionary<string, string> Parse(string a) { var d = new Dictionary<string, string>(); foreach (var kv in (a ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2) d[p[0].Trim()] = p[1].Trim(); } return d; }
    static HashSet<string> Names(ModelDoc2 doc) { var h = new HashSet<string>(); var f = (Feature)doc.FirstFeature(); while (f != null) { h.Add(f.Name); f = (Feature)f.GetNextFeature(); } return h; }
    static string Bodies(PartDoc part) { var sb2 = new StringBuilder(); foreach (var t in new[] { swBodyType_e.swSolidBody, swBodyType_e.swSheetBody }) { var bs = part.GetBodies2((int)t, false) as object[]; sb2.Append(t == swBodyType_e.swSolidBody ? "solid " : "sheet ").Append(bs == null ? 0 : bs.Length).Append(' '); } return sb2.ToString(); }
    static double Volume(ModelDoc2 doc) { var mp = (MassProperty)doc.Extension.CreateMassProperty(); mp.UseSystemUnits = true; return mp.Volume; }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = Parse(arg); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager;
        string sketch = a.ContainsKey("sketch") ? a["sketch"] : "Sketch2"; double depth = a.ContainsKey("depth") ? double.Parse(a["depth"], System.Globalization.CultureInfo.InvariantCulture) : 0.03;
        bool reverse = a.ContainsKey("reverse") && a["reverse"] == "1", flip = a.ContainsKey("flip") && a["flip"] == "1"; int keep = a.ContainsKey("keep") ? int.Parse(a["keep"]) : 0;
        string sel = a.ContainsKey("sel") ? a["sel"] : "feature"; bool scope = a.ContainsKey("scope") && a["scope"] == "1";
        double v0 = Volume(doc); Add("start: " + Bodies(part) + "volume " + (v0 * 1e9).ToString("F3") + " mm3");
        if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);
        // ---- 1. surface extrude ----
        var names = Names(doc); doc.ClearSelection2(true);
        bool sk = doc.Extension.SelectByID2(sketch, "SKETCH", 0, 0, 0, false, 0, null, 0); Add("sketch selected: " + sk);
        fm.FeatureExtruRefSurface3(true, reverse, 0, 0.0, 0, 0, depth, 0.0, false, false, false, false, 0.0, 0.0, false, false, false, false, false, false, false, false);
        Feature surf = null; var f = (Feature)doc.FirstFeature(); while (f != null) { if (!names.Contains(f.Name)) { Add("NEW feature " + f.Name + " [" + f.GetTypeName2() + "]"); if (f.GetTypeName2() == "ExtruRefSurface") surf = f; } f = (Feature)f.GetNextFeature(); }
        Add("after the surface extrude: " + Bodies(part));
        if (surf == null) return sb.ToString() + "NO SURFACE EXTRUDE";
        // ---- 2. surface cut ----
        names = Names(doc); doc.ClearSelection2(true); bool ok = true;
        if (sel == "feature" || sel == "both") ok &= surf.Select2(false, 0);
        if (sel == "body" || sel == "both")
        {
            var sheets = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
            if (sheets != null && sheets.Length > 0) ok &= ((Body2)sheets[0]).Select2(sel == "both", null); else Add("no sheet body to select");
        }
        Add("selected (" + sel + ") = " + ok + ", count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));
        int err = 0; Feature cut = null;
        try { cut = fm.InsertCutSurface(flip, keep, scope, !scope, null, out err) as Feature; } catch (Exception ex) { Add("InsertCutSurface threw " + ex.GetType().Name + ": " + ex.Message); }
        Add("InsertCutSurface -> " + (cut == null ? "null" : cut.Name + " [" + cut.GetTypeName2() + "]") + " error " + err);
        doc.ClearSelection2(true); doc.EditRebuild3();
        f = (Feature)doc.FirstFeature(); while (f != null) { if (!names.Contains(f.Name)) Add("NEW feature " + f.Name + " [" + f.GetTypeName2() + "] error " + FeatErr(f)); f = (Feature)f.GetNextFeature(); }
        double v1 = Volume(doc); Add("after the cut: " + Bodies(part) + "volume " + (v1 * 1e9).ToString("F3") + " mm3, removed " + ((v0 - v1) * 1e9).ToString("F3") + " mm3");
        return sb.ToString();
    }
    static int FeatErr(Feature f) { bool w; return f.GetErrorCode2(out w); }
}
