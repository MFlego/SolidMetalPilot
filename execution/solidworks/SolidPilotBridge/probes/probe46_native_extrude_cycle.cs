// Probe 46: is the handle growth per document cycle SolidWorks' own, or caused by SolidPilot's code? N cycles of new part -> sketch -> boss extrude -> close using ONLY raw SolidWorks API calls
// (no SolidPilot tools), measured from inside the process.   arg = "N[,gc]"  (gc = full GC + finalizers after every cycle; default N = 20, no GC)
using System;
using System.Diagnostics;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var parts = (arg ?? "").Split(','); int n; if (!int.TryParse(parts[0], out n)) n = 20; bool gc = parts.Length > 1 && parts[1] == "gc";
        string template = sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); var p = Process.GetCurrentProcess();
        p.Refresh(); int h0 = p.HandleCount; int built = 0;
        for (int i = 0; i < n; i++)
        {
            var doc = (ModelDoc2)sw.NewDocument(template, 0, 0, 0); if (doc == null) continue;
            doc.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0);
            doc.SketchManager.InsertSketch(true); doc.SketchManager.CreateCornerRectangle(-0.05, -0.03, 0, 0.05, 0.03, 0); doc.SketchManager.InsertSketch(true);
            var f = doc.FeatureManager.FeatureExtrusion3(true, false, false, 0, 0, 0.01, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
            if (f != null) built++;
            string title = doc.GetTitle(); doc = null; f = null; sw.CloseDoc(title);
            if (gc) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        }
        p.Refresh(); int h1 = p.HandleCount;
        return "raw API: " + built + "/" + n + " parts built and closed" + (gc ? " (GC after every cycle)" : "") + ", handles " + h0 + " -> " + h1 + " (" + (h1 - h0) + ", " + ((h1 - h0) / (double)n).ToString("F1") + " per cycle)";
    }
}
