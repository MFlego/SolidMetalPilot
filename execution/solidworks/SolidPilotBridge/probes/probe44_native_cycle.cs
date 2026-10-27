// Probe 44: baseline for the handle-growth question - open and close N new parts using ONLY SolidWorks' own API (no SolidPilot tools), so the growth measured around it is SolidWorks' own.   arg = N (default 20)
using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        int n; if (!int.TryParse(arg, out n)) n = 20; string template = sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); int ok = 0;
        for (int i = 0; i < n; i++)
        {
            var doc = (ModelDoc2)sw.NewDocument(template, 0, 0, 0); if (doc == null) continue;
            string title = doc.GetTitle(); sw.CloseDoc(title); ok++;
        }
        return "opened and closed " + ok + " of " + n + " parts natively (template " + template + ")";
    }
}
