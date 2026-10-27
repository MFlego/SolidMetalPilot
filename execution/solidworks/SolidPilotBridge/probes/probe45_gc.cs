// Probe 45: do lingering COM wrappers (RCWs) keep SolidWorks resources alive after a document is closed? Forces a full GC + finalizer pass INSIDE SolidWorks and reports the process's handle count before / after.
// RESULT: a forced GC released ~85 handles once (after many suites) and only 10-15 per call afterwards - NOT the cause of the per-cycle growth (a GC after every cycle did not change the slope).
// The cause is the automatic snapshot (SaveAs3 copy, ~16 handles each): see tests/doc-soak.ps1 -NoSnapshots and probes 44 / 46 (raw API cycles leak nothing).
using System;
using System.Diagnostics;
using SolidWorks.Interop.sldworks;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var p = Process.GetCurrentProcess(); p.Refresh(); int before = p.HandleCount; long mb0 = p.PrivateMemorySize64 / (1024 * 1024);
        var t = Stopwatch.StartNew();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        t.Stop(); p.Refresh();
        return "handles " + before + " -> " + p.HandleCount + " (" + (p.HandleCount - before) + "), private MB " + mb0 + " -> " + (p.PrivateMemorySize64 / (1024 * 1024)) + ", GC took " + t.ElapsedMilliseconds + " ms, managed heap " + (GC.GetTotalMemory(false) / 1024) + " KB";
    }
}
