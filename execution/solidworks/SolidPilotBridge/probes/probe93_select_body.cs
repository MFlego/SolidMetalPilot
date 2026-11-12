// Probe 93 (round 7): select a BODY by name with a given selection mark (append) while a PropertyManager box is active. arg = "body=Base-Flange2;mark=1;append=1;clear=0"
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sm = (SelectionMgr)doc.SelectionManager; string log = "";
        if (a.ContainsKey("clear") && a["clear"] == "1") { doc.ClearSelection2(true); log += "cleared; "; }
        string pl; if (a.TryGetValue("plane", out pl)) log += "plane " + pl + " mark " + (a.ContainsKey("pmark") ? a["pmark"] : "2") + " -> " + doc.Extension.SelectByID2(pl, "PLANE", 0, 0, 0, true, int.Parse(a.ContainsKey("pmark") ? a["pmark"] : "2"), null, 0) + "; ";
        string ft; if (a.TryGetValue("feature", out ft)) { var f = (Feature)doc.FirstFeature(); int fm = int.Parse(a.ContainsKey("fmark") ? a["fmark"] : "1"); while (f != null) { if (f.Name == ft) { log += "feature " + ft + " mark " + fm + " -> " + f.Select2(true, fm) + "; "; } f = (Feature)f.GetNextFeature(); } }
        if (!a.ContainsKey("body")) return log + "selected objects " + sm.GetSelectedObjectCount2(-1);
        int mark = int.Parse(a.ContainsKey("mark") ? a["mark"] : "1"); bool append = !a.ContainsKey("append") || a["append"] == "1";
        int bt = a.ContainsKey("bt") ? int.Parse(a["bt"]) : (int)swBodyType_e.swSolidBody;
        var all = part.GetBodies2(bt, false) as object[]; if (all == null) return log + "no bodies of type " + bt; foreach (Body2 b in all) log += "[" + b.Name + "] ";
        foreach (Body2 b in all)
            if (b.Name == a["body"]) { var sd = sm.CreateSelectData() as SelectData; sd.Mark = mark; log += "Select2(" + append + ", mark " + mark + ") -> " + b.Select2(append, sd) + "; "; }
        return log + "selected objects " + sm.GetSelectedObjectCount2(-1) + " marks: " + string.Join(",", Enumerable(sm));
    }
    static IEnumerable<string> Enumerable(SelectionMgr sm) { for (int i = 1; i <= sm.GetSelectedObjectCount2(-1); i++) yield return sm.GetSelectedObjectType3(i, -1) + "/" + sm.GetSelectedObjectMark(i); }
}
