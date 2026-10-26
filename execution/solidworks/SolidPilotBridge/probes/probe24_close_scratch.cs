// Probe 24: close a scratch document WITHOUT saving (CloseDoc through the API never saves) and reactivate the original document.  arg = "<scratch title>|<original title>"
using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var p = arg.Split('|'); sw.CloseDoc(p[0]); int err = 0;
        var act = sw.ActivateDoc3(p[1], false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err) as ModelDoc2;
        var cur = sw.ActiveDoc as ModelDoc2;
        return "closed " + p[0] + "; active: " + (cur == null ? "none" : cur.GetTitle() + " modified " + cur.GetSaveFlag());
    }
}
