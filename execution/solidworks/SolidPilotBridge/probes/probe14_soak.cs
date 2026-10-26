// Probe 14 (soak, in-process side): N iterations of a typical read mix against the active part - title, feature walk, body/face/edge census, selection count, view transform.
// Counts exceptions and reports wall time per 100 iterations. arg = iterations (default 500).
using System;
using System.Diagnostics;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        int n; if (!int.TryParse(arg, out n)) n = 500;
        var sb = new StringBuilder(); int errors = 0; var sw0 = Stopwatch.StartNew(); long checksum = 0; string firstError = null;
        for (int i = 0; i < n; i++)
        {
            try
            {
                var doc = (ModelDoc2)sw.ActiveDoc; checksum += doc.GetTitle().Length;
                var f = (Feature)doc.FirstFeature(); int k = 0; while (f != null) { k++; f = (Feature)f.GetNextFeature(); } checksum += k;
                var part = (PartDoc)doc; var bodies = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false);
                if (bodies != null) foreach (Body2 b in bodies) { checksum += b.GetFaceCount(); checksum += b.GetEdgeCount(); }
                checksum += ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
                var v = (ModelView)doc.ActiveView; checksum += ((double[])v.Transform.ArrayData).Length;
            }
            catch (Exception e) { errors++; if (firstError == null) firstError = e.GetType().Name + ": " + e.Message; }
        }
        sw0.Stop();
        var p = Process.GetCurrentProcess();
        sb.AppendLine("in-process: " + n + " iterations, " + errors + " errors, " + sw0.ElapsedMilliseconds + " ms total (" + (sw0.ElapsedMilliseconds * 100.0 / n).ToString("F0") + " ms / 100 iterations), checksum " + checksum);
        sb.AppendLine("SolidWorks process: handles " + p.HandleCount + " private MB " + (p.PrivateMemorySize64 / 1048576) + " workingset MB " + (p.WorkingSet64 / 1048576));
        if (firstError != null) sb.AppendLine("first error: " + firstError);
        return sb.ToString();
    }
}
