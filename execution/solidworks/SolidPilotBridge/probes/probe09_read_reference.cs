// go/no-go probe 09: VISIBILITY of SolidWorks parameters - read the user's reference part (v4) feature definitions TYPED, in-process: multi-radius Fillet (edges + radius per edge),
// surface cut, mirror (plane + features), surface extrude, plus the unfold/fold/convert chain. The file is opened read-only + silent and closed again; the original active document is reactivated.
// arg = full path of the reference .SLDPRT
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Mid(object o)
    {
        var e = o as Edge; if (e == null) return o == null ? "null" : o.GetType().Name;
        var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per); var p = (double[])c.Evaluate2((s + en) / 2, 0);
        return string.Format("mid({0:F2},{1:F2},{2:F2})", p[0] * 1000, p[1] * 1000, p[2] * 1000);
    }

    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); var original = sw.ActiveDoc as ModelDoc2; string originalTitle = original == null ? null : original.GetTitle();
        int err = 0, warn = 0;
        var doc = sw.OpenDoc6(arg, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly | (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        if (doc == null) return "open failed err=" + err + " warn=" + warn;
        try
        {
            sb.AppendLine("opened " + doc.GetTitle());
            var f = (Feature)doc.FirstFeature(); int i = 0;
            while (f != null)
            {
                i++; string t = f.GetTypeName2();
                if (i > 17) sb.AppendLine(i + " " + f.Name + " [" + t + "]" + (f.IsSuppressed() ? " suppressed" : ""));
                if (t == "Fillet")
                {
                    var d = (ISimpleFilletFeatureData2)f.GetDefinition();
                    sb.AppendLine("   FILLET type=" + d.Type + " multiRadius=" + d.IsMultipleRadius + " default r=" + (d.DefaultRadius * 1000).ToString("F3") + " mm  items=" + d.FilletItemsCount + " edges=" + d.GetEdgeCount());
                    if (d.AccessSelections(doc, null))
                    {
                        int n = d.GetEdgeCount();
                        for (int k = 0; k < n; k++)
                        {
                            var o = d.GetFilletItemAtIndex(k); double r = d.GetRadius(o);
                            sb.AppendLine("      item " + k + " r=" + (r * 1000).ToString("F3") + " mm " + Mid(o));
                        }
                        d.ReleaseSelectionAccess();
                    }
                }
                else if (t == "SurfCut")
                {
                    var d = (ISurfaceCutFeatureData)f.GetDefinition(); sb.AppendLine("   SURFACE CUT flip=" + d.Flip + " autoSelect=" + d.AutoSelect);
                }
                else if (t == "MirrorPattern")
                {
                    var d = (IMirrorPatternFeatureData)f.GetDefinition();
                    sb.AppendLine("   MIRROR geometryPattern=" + d.GeometryPattern + " plane=" + (d.Plane == null ? "null" : "set") + " features=" + d.GetPatternFeatureCount());
                }
                f = (Feature)f.GetNextFeature();
            }
        }
        finally
        {
            sw.CloseDoc(doc.GetTitle());
            if (originalTitle != null) { int e2 = 0; sw.ActivateDoc3(originalTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2); }
        }
        return sb.ToString();
    }
}
