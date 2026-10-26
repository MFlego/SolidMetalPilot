// Probe 25: timing of purely SOLIDWORKS-BOUND work - open a file, force-rebuild it three times, close it (never saved). arg = path of a throw-away copy.
// In-process (bridge run_cs) vs out-of-process (oop_host.ps1 Go-time): if the process boundary does not matter for work SolidWorks itself performs, the two must agree.
using System;
using System.Diagnostics;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); var original = sw.ActiveDoc as ModelDoc2; string originalTitle = original == null ? null : original.GetTitle();
        int err = 0, warn = 0; var t = Stopwatch.StartNew();
        var doc = sw.OpenDoc6(arg, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        if (doc == null) return "open failed err=" + err + " warn=" + warn;
        sb.Append("open " + t.ElapsedMilliseconds + " ms; ");
        for (int i = 1; i <= 3; i++) { t.Restart(); doc.ForceRebuild3(false); sb.Append("rebuild" + i + " " + t.ElapsedMilliseconds + " ms; "); }
        t.Restart(); string title = doc.GetTitle(); sw.CloseDoc(title); sb.Append("close " + t.ElapsedMilliseconds + " ms");
        if (originalTitle != null) { int e2 = 0; sw.ActivateDoc3(originalTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2); }
        return sb.ToString();
    }
}
