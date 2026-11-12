// Probe 101 (round 7): every feature of the active part with its rebuild error code / warning flag, suppression and type (a flange that built nothing shows up here).  arg = name filter (substring) or empty
using System;
using System.Text;
using SolidWorks.Interop.sldworks;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sb = new StringBuilder();
        var f = (IFeature)doc.FirstFeature();
        while (f != null)
        {
            bool warn; int err = f.GetErrorCode2(out warn);
            if (string.IsNullOrEmpty(arg) || f.Name.Contains(arg))
                if (err != 0 || warn || !string.IsNullOrEmpty(arg)) sb.Append(f.Name + " [" + f.GetTypeName2() + "] error=" + err + (warn ? " warning" : "") + (f.IsSuppressed() ? " SUPPRESSED" : "") + " ;; ");
            f = (IFeature)f.GetNextFeature();
        }
        return sb.Length == 0 ? "no feature with an error or warning" : sb.ToString();
    }
}
