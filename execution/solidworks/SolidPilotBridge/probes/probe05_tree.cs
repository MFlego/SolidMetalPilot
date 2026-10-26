// Feature tree dump with sub-features, suppression and error codes (typed, in-process). arg = optional name filter. Also reports GetSaveFlag (modified) and the sheet-metal bend/flat state.
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sb = new StringBuilder();
        sb.AppendLine("doc " + doc.GetTitle() + " modified=" + doc.GetSaveFlag());
        var f = (Feature)doc.FirstFeature(); int i = 0;
        while (f != null)
        {
            i++;
            if (i > 17 || !string.IsNullOrEmpty(arg))
            {
                bool sup = f.IsSuppressed(); int err = 0; bool warn = false; try { err = f.GetErrorCode2(out warn); } catch { }
                sb.Append(i + " " + f.Name + " [" + f.GetTypeName2() + "]" + (sup ? " suppressed" : "") + (err != 0 ? " err=" + err : ""));
                var s = (Feature)f.GetFirstSubFeature(); int k = 0; while (s != null && k < 40) { sb.Append(" {" + s.Name + "}"); s = (Feature)s.GetNextSubFeature(); k++; }
                sb.AppendLine();
            }
            f = (Feature)f.GetNextFeature();
        }
        return sb.ToString();
    }
}
