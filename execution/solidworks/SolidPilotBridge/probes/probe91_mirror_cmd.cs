// Probe 91 (round 7): open the Mirror PropertyManager (RunCommand) with a BODY and a PLANE preselected, to see in the UI whether SolidWorks takes a sheet-metal body into "Bodies to Mirror".
// arg = "id=83;body=Base-Flange2;plane=Front Plane;pre=1"   (pre=0: only run the command, no preselection).  Nothing is closed afterwards: inspect with probe73 (list) and cancel with probe73 click=<red X> or Escape.
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(); foreach (var kv in (arg ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) a[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; string log = ""; doc.ClearSelection2(true);
        string bodyName; a.TryGetValue("body", out bodyName); string plane; if (!a.TryGetValue("plane", out plane)) plane = "Front Plane";
        if (a.ContainsKey("pre") && a["pre"] == "0") { }
        else
        {
            foreach (Body2 b in (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false)) if (b.Name == bodyName) log += "body selected " + b.Select2(false, null) + "; ";
            log += "plane selected " + doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, true, 0, null, 0) + "; ";
        }
        int id = int.Parse(a.ContainsKey("id") ? a["id"] : "83");
        bool ok = sw.RunCommand(id, "");
        return log + "RunCommand(" + id + ") -> " + ok + "; selected objects: " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
    }
}
