// go/no-go probe 06: read (and optionally modify) the Convert-to-Sheet-Metal feature definition TYPED, IN-PROCESS.
// History: earlier out-of-process runs saw BendEdges / RipSketches as null (that PM-created feature genuinely had no bends) and ModifyDefinition fault (RPC_E_SERVERFAULT) when bend edges were set.
// Today this same file, run out-of-process (probes/oop_host.ps1), prints the identical 12 edges / 4 sketches as in-process (ab_compare.ps1), and WRITING a bend list is a silent no-op on both sides (probe19, probe21).
// arg: "" = read only | "fix" = set gap 0.0762 mm, overlap ratio 1, Overlap corners, Tear relief via ModifyDefinition | "fixsel" = same, but also re-write the bend edges / rips
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Mid(Edge e) { var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per); var p = (double[])c.Evaluate2((s + en) / 2, 0); return string.Format("({0:F2},{1:F2},{2:F2})", p[0] * 1000, p[1] * 1000, p[2] * 1000); }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sb = new StringBuilder();
        Feature cf = null; var f = (Feature)doc.FirstFeature();
        while (f != null) { if (f.GetTypeName2() == "SolidToSheetMetal") cf = f; f = (Feature)f.GetNextFeature(); }
        if (cf == null) return "no SolidToSheetMetal feature";
        var d = (IConvertSolidFeatureData)cf.GetDefinition();
        sb.AppendLine(cf.Name + ": thickness " + (d.SheetThickness * 1000) + " mm reverse=" + d.ReverseThickness + " radius " + (d.BendRadius * 1000) + " mm keepBody=" + d.KeepBody);
        sb.AppendLine("ripGap " + (d.RipGap * 1000) + " mm  overlapRatio " + d.RipOverlapRatio + "  cornerDefaults " + d.CornerDefaults + "  reliefType " + d.ReliefType + "  reliefRatio " + d.ReliefRatio);
        bool access = d.AccessSelections(doc, null);
        sb.AppendLine("AccessSelections " + access);
        var ff = d.GetFixedFace() as Face2; sb.AppendLine("fixed face: " + (ff == null ? "null" : "box " + string.Join(",", Array.ConvertAll((double[])ff.GetBox(), x => (x * 1000).ToString("F2")))));
        var be = d.BendEdges as object[]; sb.AppendLine("BendEdges: " + (be == null ? "null" : be.Length + " items"));
        if (be != null) for (int i = 0; i < be.Length; i++) { var e = be[i] as Edge; sb.AppendLine("   " + (i + 1) + " " + (e != null ? "edge mid " + Mid(e) : (be[i] == null ? "null" : be[i].GetType().Name))); }
        var rs = d.RipSketches as object[]; sb.AppendLine("RipSketches: " + (rs == null ? "null" : rs.Length + " items"));
        if (rs != null) foreach (var o in rs) { var rf = o as Feature; sb.AppendLine("   " + (rf != null ? rf.Name : "?")); }
        if (arg == "fix" || arg == "fixsel")
        {
            d.RipGap = 0.0000762; d.RipOverlapRatio = 1.0; d.CornerDefaults = 1; d.OverrideDefaultAutoReliefParameters = true; d.ReliefType = 2; d.ReliefRatio = 0.5;
            bool ok = cf.ModifyDefinition(d, doc, null);
            sb.AppendLine("ModifyDefinition -> " + ok);
            var d2 = (IConvertSolidFeatureData)cf.GetDefinition(); sb.AppendLine("after: ripGap " + (d2.RipGap * 1000) + " ratio " + d2.RipOverlapRatio + " corner " + d2.CornerDefaults + " relief " + d2.ReliefType);
        }
        d.ReleaseSelectionAccess();
        return sb.ToString();
    }
}
