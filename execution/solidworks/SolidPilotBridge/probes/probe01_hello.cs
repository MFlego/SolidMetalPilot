// go/no-go probe 01: sanity - the script runner compiles, runs on SolidWorks' UI thread and has typed API access.
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SolidWorks " + sw.RevisionNumber() + "  thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId + " apartment=" + System.Threading.Thread.CurrentThread.GetApartmentState());
        var doc = sw.ActiveDoc as ModelDoc2;
        if (doc == null) return sb + "no active document";
        sb.AppendLine("doc: " + doc.GetTitle() + "  type=" + doc.GetType());
        var part = doc as PartDoc;
        if (part != null)
        {
            foreach (int kind in new[] { (int)swBodyType_e.swSolidBody, (int)swBodyType_e.swSheetBody })
            {
                var bodies = part.GetBodies2(kind, false) as object[];
                sb.AppendLine("bodies(type " + kind + "): " + (bodies == null ? 0 : bodies.Length));
                if (bodies != null) foreach (Body2 b in bodies) { var bb = (double[])b.GetBodyBox(); sb.AppendLine("   '" + b.Name + "' faces=" + b.GetFaceCount() + " box mm z " + (bb[2] * 1000).ToString("F3") + ".." + (bb[5] * 1000).ToString("F3")); }
            }
        }
        var f = (Feature)doc.FirstFeature(); int n = 0; string last = "";
        while (f != null) { n++; last = f.Name + " [" + f.GetTypeName2() + "]"; f = (Feature)f.GetNextFeature(); }
        sb.AppendLine("features: " + n + " last: " + last);
        return sb.ToString();
    }
}
