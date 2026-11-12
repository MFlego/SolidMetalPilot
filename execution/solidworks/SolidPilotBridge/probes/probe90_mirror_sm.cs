// Probe 90 (round 7): can a sheet-metal BODY or its FEATURES be mirrored? Run on a SCRATCH COPY of a part with two sheet-metal bodies.
// arg = "mode=body|feat|cmd;target=Base-Flange2;plane=Front Plane;merge=0|1;geom=0|1;scope=0|1|2;mark=1;pmark=2"
// mode=body : select the BODY (mark) + the plane (pmark), InsertMirrorFeature2(true, geom, merge, false, scope)
// mode=feat : select the FEATURE (mark) + the plane, InsertMirrorFeature2(false, geom, merge, false, scope)
// Reports: the returned feature (null or name+type), body count before / after, which bodies are sheet metal (has Flat-Pattern), then deletes the new feature again.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static Dictionary<string, string> Parse(string a) { var d = new Dictionary<string, string>(); foreach (var kv in (a ?? "").Split(';')) { var p = kv.Split('='); if (p.Length == 2) d[p[0].Trim()] = p[1].Trim(); } return d; }
    static string G(Dictionary<string, string> a, string k, string dflt) { return a.ContainsKey(k) ? a[k] : dflt; }
    static HashSet<string> Names(ModelDoc2 doc) { var h = new HashSet<string>(); var f = (Feature)doc.FirstFeature(); while (f != null) { h.Add(f.Name + "|" + f.GetTypeName2()); f = (Feature)f.GetNextFeature(); } return h; }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = Parse(arg); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager;
        string mode = G(a, "mode", "body"), target = G(a, "target", "Base-Flange2"), plane = G(a, "plane", "Front Plane");
        bool merge = G(a, "merge", "0") == "1", geom = G(a, "geom", "0") == "1"; int scope = int.Parse(G(a, "scope", "0")), mark = int.Parse(G(a, "mark", "1")), pmark = int.Parse(G(a, "pmark", "2"));
        var before = Names(doc);
        var bs0 = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; Add("bodies before: " + (bs0 == null ? 0 : bs0.Length));
        doc.ClearSelection2(true);
        bool s1 = false;
        if (mode == "body")
        {
            foreach (Body2 b in bs0) { if (b.Name == target) { s1 = b.Select2(false, null); try { var sd = ((SelectionMgr)doc.SelectionManager).CreateSelectData() as SelectData; sd.Mark = mark; s1 = b.Select2(false, sd); } catch (Exception e) { Add("selectdata: " + e.Message); } } }
        }
        else
        {
            var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.Name == target) { var sd = ((SelectionMgr)doc.SelectionManager).CreateSelectData() as SelectData; sd.Mark = mark; s1 = f.Select2(false, mark); } f = (Feature)f.GetNextFeature(); }
        }
        bool s2 = doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, true, pmark, null, 0);
        Add("selected target " + s1 + ", plane " + s2 + ", count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));
        Feature nf = null;
        try { nf = fm.InsertMirrorFeature2(mode == "body", geom, merge, false, scope) as Feature; }
        catch (Exception e) { Add("EXC " + e.Message); }
        Add("InsertMirrorFeature2 -> " + (nf == null ? "null" : nf.Name + " (" + nf.GetTypeName2() + ") err " + SafeErr(nf)));
        doc.ClearSelection2(true);
        try { doc.EditRebuild3(); } catch { }
        var bs1 = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; Add("bodies after: " + (bs1 == null ? 0 : bs1.Length));
        var after = Names(doc); foreach (var n in after) if (!before.Contains(n)) Add("new feature " + n);
        if (bs1 != null) foreach (Body2 b in bs1) { Add("  body " + b.Name + " sheetmetal=" + b.IsSheetMetal()); }
        if (nf != null && G(a, "keep", "0") != "1") { doc.ClearSelection2(true); nf.Select2(false, 0); doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed); doc.ClearSelection2(true); Add("deleted the probe feature"); }
        return sb.ToString();
    }
    static string SafeErr(Feature f) { try { bool w; return f.GetErrorCode2(out w).ToString(); } catch { return "?"; } }
}
