// Probe 12: read every Unfold / Fold feature definition (IFoldsFeatureData): fixed face box and bend count - to see which face the Unfold used and what a Fold must use to return to the original pose.
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sb = new StringBuilder(); var f = (Feature)doc.FirstFeature();
        while (f != null)
        {
            string t = f.GetTypeName2();
            if (t == "UnFold" || t == "Fold")
            {
                var d = (IFoldsFeatureData)f.GetDefinition(); bool acc = d.AccessSelections(doc, null);
                var ff = d.FixedFace as Face2; var bends = d.Bends as object[];
                sb.AppendLine(f.Name + " [" + t + "] access=" + acc + " bends=" + (bends == null ? "null" : bends.Length.ToString()) + " fixedFace=" +
                    (ff == null ? "null" : "box mm " + string.Join(",", Array.ConvertAll((double[])ff.GetBox(), x => (x * 1000).ToString("F2"))) + " area " + (ff.GetArea() * 1e6).ToString("F0")));
                d.ReleaseSelectionAccess();
            }
            f = (Feature)f.GetNextFeature();
        }
        return sb.ToString();
    }
}
