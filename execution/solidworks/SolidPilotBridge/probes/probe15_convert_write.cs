// go/no-go probe 15: WRITE-path A/B for the Convert-to-Sheet-Metal definition. Opens the reference part (v4) read-only + silent (it is never saved), changes the Convert definition through
// ModifyDefinition, reads it back, closes it again and reactivates the original document.
// arg = "<full path of the .SLDPRT>;<mode>"   mode: fix = rip gap 0.0762 mm / overlap ratio 1 / Overlap corners / Tear relief
//                                              rewrite = the same plus re-assigning the feature's OWN BendEdges and RipSketches (the call that raised RPC_E_SERVERFAULT from PowerShell)
using System;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Fail(Exception e) { var ce = e as COMException; return e.GetType().Name + (ce != null ? " 0x" + ((uint)ce.ErrorCode).ToString("X8") : "") + ": " + e.Message; }

    public static string Run(SldWorks sw, string arg)
    {
        var parts = arg.Split(';'); string path = parts[0]; string mode = parts.Length > 1 ? parts[1] : "fix";
        var sb = new StringBuilder(); var original = sw.ActiveDoc as ModelDoc2; string originalTitle = original == null ? null : original.GetTitle();
        int err = 0, warn = 0;
        var doc = sw.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly | (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        if (doc == null) return "open failed err=" + err + " warn=" + warn;
        try
        {
            Feature cf = null; var f = (Feature)doc.FirstFeature();
            while (f != null) { if (f.GetTypeName2() == "SolidToSheetMetal") cf = f; f = (Feature)f.GetNextFeature(); }
            if (cf == null) return "no SolidToSheetMetal feature";
            var d = (IConvertSolidFeatureData)cf.GetDefinition();
            sb.AppendLine(cf.Name + " before: ripGap " + (d.RipGap * 1000).ToString("F4") + " mm ratio " + d.RipOverlapRatio + " corner " + d.CornerDefaults + " relief " + d.ReliefType + "/" + d.ReliefRatio);
            sb.AppendLine("AccessSelections " + d.AccessSelections(doc, null));
            var be = d.BendEdges as object[]; var rs = d.RipSketches as object[];
            sb.AppendLine("BendEdges " + (be == null ? "null" : be.Length.ToString()) + "  RipSketches " + (rs == null ? "null" : rs.Length.ToString()));
            d.RipGap = 0.0000762; d.RipOverlapRatio = 1.0; d.CornerDefaults = 1; d.OverrideDefaultAutoReliefParameters = true; d.ReliefType = 2; d.ReliefRatio = 0.5;
            if (mode == "rewrite" && be != null && rs != null)
            {
                try { d.BendEdges = Array.ConvertAll(be, o => new DispatchWrapper(o)); d.RipSketches = Array.ConvertAll(rs, o => new DispatchWrapper(o)); sb.AppendLine("re-assigned BendEdges / RipSketches"); }
                catch (Exception e) { sb.AppendLine("assign FAILED " + Fail(e)); }
            }
            try { sb.AppendLine("ModifyDefinition -> " + cf.ModifyDefinition(d, doc, null)); }
            catch (Exception e) { sb.AppendLine("ModifyDefinition FAILED " + Fail(e)); }
            try { d.ReleaseSelectionAccess(); } catch { }
            var d2 = (IConvertSolidFeatureData)cf.GetDefinition(); bool warnFlag; int ec = cf.GetErrorCode2(out warnFlag);
            sb.AppendLine("after: ripGap " + (d2.RipGap * 1000).ToString("F4") + " mm ratio " + d2.RipOverlapRatio + " corner " + d2.CornerDefaults + " relief " + d2.ReliefType + "/" + d2.ReliefRatio + "  featureError " + ec);
        }
        finally
        {
            sw.CloseDoc(doc.GetTitle());
            if (originalTitle != null) { int e2 = 0; sw.ActivateDoc3(originalTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2); }
        }
        return sb.ToString();
    }
}
