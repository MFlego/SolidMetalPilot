// Probe 22: select ONE back-plate perimeter edge of the offset surface body through the API (Select4, mark 2) while the Convert PropertyManager is open - the caller then reads the PM's Bend list through UI Automation
// to see whether an API selection reaches the list. arg = index 0..3 of the back-plate perimeter edge (in body edge order). Run in-process (bridge run_cs) and out-of-process (oop_host.ps1).
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        int idx; if (!int.TryParse(arg, out idx)) idx = 0;
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sm = (SelectionMgr)doc.SelectionManager; var sb = new StringBuilder();
        Body2 surf = null; foreach (Body2 b in (object[])part.GetBodies2(1, false)) { var bb = (double[])b.GetBodyBox(); if (bb[2] * 1000 > 0.01) surf = b; }
        if (surf == null) return "offset surface body not found";
        var back = new List<Edge>();
        foreach (Edge e in (object[])surf.GetEdges())
        {
            var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per); var p = (double[])c.Evaluate2((s + en) / 2, 0);
            if (Math.Abs(p[2] * 1000 - 1.524) < 0.02) back.Add(e);
        }
        sb.AppendLine("back-plate perimeter edges: " + back.Count);
        if (idx >= back.Count) return sb + "index out of range";
        var sd = sm.CreateSelectData(); sd.Mark = 2;
        sb.AppendLine("Select4(append, mark 2) -> " + ((Entity)back[idx]).Select4(true, sd) + "; selection count " + sm.GetSelectedObjectCount2(-1));
        return sb.ToString();
    }
}
