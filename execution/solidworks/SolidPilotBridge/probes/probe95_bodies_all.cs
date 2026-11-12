// Probe 95 (round 7): list every body of the active part (solid, sheet = surface, ...) with its type and visibility, optionally after a rebuild. arg = "rebuild=1"
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder();
        if ((arg ?? "").Contains("rebuild=1")) sb.Append("ForceRebuild3 " + doc.ForceRebuild3(false) + " ;; ");
        foreach (int t in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var all = part.GetBodies2(t, false) as object[]; sb.Append("type " + t + ": " + (all == null ? 0 : all.Length) + " ;; ");
            if (all != null && all.Length <= 40) foreach (Body2 b in all) sb.Append("   " + b.Name + (b.Visible ? "" : " (hidden)") + (t == 0 ? " sheetmetal=" + b.IsSheetMetal() : "") + " ;; ");
        }
        return sb.ToString();
    }
}

