// Probe 23: write a silent COPY of the active document to the path in arg (SaveAs with the Copy option: the active document keeps its name, path and modified state). A cheap safety net before UI-driven experiments.
using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; int err = 0, warn = 0; string before = doc.GetTitle() + " | " + doc.GetPathName() + " | modified " + doc.GetSaveFlag();
        bool ok = doc.Extension.SaveAs3(arg, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, null, ref err, ref warn);
        return "SaveAs3(copy) -> " + ok + " err " + err + " warn " + warn + "\r\nbefore: " + before + "\r\nafter : " + doc.GetTitle() + " | " + doc.GetPathName() + " | modified " + doc.GetSaveFlag();
    }
}
