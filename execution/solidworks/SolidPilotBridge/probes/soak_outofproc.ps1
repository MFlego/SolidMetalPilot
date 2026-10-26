# Probe 14 (soak, out-of-process side): the same read mix as probe14_soak.cs through typed interop from a separate process (STA), reporting errors, time and SolidWorks' handle / memory growth.
param([int]$N = 500)
. "$PSScriptRoot\_common.ps1"; $redist = Get-SwRedist
[void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.swconst.dll"); [void][Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.sldworks.dll")
Add-Type -ReferencedAssemblies "$redist\SolidWorks.Interop.sldworks.dll", "$redist\SolidWorks.Interop.swconst.dll" -TypeDefinition @"
using System; using System.Diagnostics; using SolidWorks.Interop.sldworks; using SolidWorks.Interop.swconst;
public static class OopSoak {
  public static string Run(object swObj, int n) {
    var sw = (SldWorks)swObj; int errors = 0; long checksum = 0; string first = null; var t = Stopwatch.StartNew();
    for (int i = 0; i < n; i++) {
      try {
        var doc = (ModelDoc2)sw.ActiveDoc; checksum += doc.GetTitle().Length;
        var f = (Feature)doc.FirstFeature(); int k = 0; while (f != null) { k++; f = (Feature)f.GetNextFeature(); } checksum += k;
        var part = (PartDoc)doc; var bodies = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false);
        if (bodies != null) foreach (Body2 b in bodies) { checksum += b.GetFaceCount(); checksum += b.GetEdgeCount(); }
        checksum += ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
        var v = (ModelView)doc.ActiveView; checksum += ((double[])v.Transform.ArrayData).Length;
      } catch (Exception e) { errors++; if (first == null) first = e.GetType().Name + ": " + e.Message; }
    }
    t.Stop();
    return "out-of-process: " + n + " iterations, " + errors + " errors, " + t.ElapsedMilliseconds + " ms total (" + (t.ElapsedMilliseconds * 100.0 / n).ToString("F0") + " ms / 100 iterations), checksum " + checksum + (first != null ? "\nfirst error: " + first : "");
  }
}
"@
$p = Get-Process SLDWORKS | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
"SolidWorks before: handles $($p.HandleCount) private MB $([int]($p.PrivateMemorySize64/1MB))"
[OopSoak]::Run((Get-SolidWorks), $N)
$p.Refresh(); "SolidWorks after : handles $($p.HandleCount) private MB $([int]($p.PrivateMemorySize64/1MB))"
