// Probe 56: open a document READ-ONLY and make it the active one, so the read-only tools (feature_data get, analyze_model ...) can look inside a user's reference part. It is never saved: close it with
// close_document save=false. arg = full path of the .SLDPRT / .SLDASM / .SLDDRW. Output: the title, or the open error / warning codes.
using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        if (string.IsNullOrEmpty(arg) || !System.IO.File.Exists(arg)) return "no such file: " + arg;
        string ext = System.IO.Path.GetExtension(arg).ToLowerInvariant();
        int type = ext == ".sldasm" ? (int)swDocumentTypes_e.swDocASSEMBLY : ext == ".slddrw" ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocPART;
        int err = 0, warn = 0;
        var doc = sw.OpenDoc6(arg, type, (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly | (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        if (doc == null) return "open failed err=" + err + " warn=" + warn;
        int e2 = 0; sw.ActivateDoc3(doc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2);
        return "opened read-only " + doc.GetTitle() + " (err " + err + ", warn " + warn + ", readOnly " + doc.IsOpenedReadOnly() + ")";
    }
}
