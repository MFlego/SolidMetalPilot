// Probe 32: how do the GLOBAL sheet-metal parameters (K-factor, thickness, bend radius, bend-allowance type) really get written?
//   1 Sheet-Metal1 definition WITH AccessSelections first
//   2 Base-Flange1 definition with its Override* flags (OverrideKFactor / OverrideRadius / OverrideThickness) -> KFactor / BendRadius / Thickness
//   3 what the per-bend values and the folded box say after each
// Everything is restored at the end.
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Box(ModelDoc2 doc)
    {
        var bs = (object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false); if (bs == null || bs.Length == 0) return "no body";
        var b = (double[])((Body2)bs[0]).GetBodyBox(); return ((b[3] - b[0]) * 1000).ToString("F3") + " x " + ((b[4] - b[1]) * 1000).ToString("F3") + " x " + ((b[5] - b[2]) * 1000).ToString("F3");
    }
    static string FirstBend(ModelDoc2 doc)
    {
        var f = (Feature)doc.FirstFeature();
        while (f != null) { var s = (Feature)f.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") { var d = (IOneBendFeatureData)s.GetDefinition(); return s.Name + " K=" + d.KFactor.ToString("G4") + " R=" + (d.BendRadius * 1000).ToString("F3") + " mm BAtype=" + d.BendAllowanceType; } s = (Feature)s.GetNextSubFeature(); } f = (Feature)f.GetNextFeature(); }
        return "no bend";
    }
    static string Err(Feature f) { bool w; int e = 0; try { e = f.GetErrorCode2(out w); } catch { } return e == 0 ? "ok" : "ERROR " + e; }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder();
        var sm = (Feature)part.FeatureByName("Sheet-Metal1"); var bf = (Feature)part.FeatureByName("Base-Flange1");
        sb.AppendLine("start      : " + FirstBend(doc) + "  box " + Box(doc));

        // 1. Sheet-Metal1 with AccessSelections
        var d = (ISheetMetalFeatureData)sm.GetDefinition(); bool acc = d.AccessSelections(doc, null);
        double k0 = d.KFactor; d.KFactor = 0.42;
        sb.AppendLine("1 access=" + acc + " write K=0.42 -> " + sm.ModifyDefinition(d, doc, null) + "  K now " + ((ISheetMetalFeatureData)sm.GetDefinition()).KFactor + "  | " + FirstBend(doc));
        try { d.ReleaseSelectionAccess(); } catch { }

        // 2. Base-Flange1 with the override flags
        var b = (IBaseFlangeFeatureData)bf.GetDefinition(); bool acc2 = b.AccessSelections(doc, null);
        sb.AppendLine("2 base flange before: OverrideK=" + b.OverrideKFactor + " K=" + b.KFactor + " OverrideR=" + b.OverrideRadius + " R=" + b.BendRadius * 1000 + " OverrideT=" + b.OverrideThickness + " T=" + b.Thickness * 1000 + " OverrideAll=" + b.OverrideDefaultSheetMetalParameters + " (access " + acc2 + ")");
        b.OverrideDefaultSheetMetalParameters = true; b.OverrideKFactor = true; b.KFactor = 0.42;
        sb.AppendLine("2 write OverrideK + K=0.42 -> " + bf.ModifyDefinition(b, doc, null) + "  " + Err(bf) + "  | " + FirstBend(doc));
        try { b.ReleaseSelectionAccess(); } catch { }
        var b2 = (IBaseFlangeFeatureData)bf.GetDefinition(); sb.AppendLine("2 read back: OverrideK=" + b2.OverrideKFactor + " K=" + b2.KFactor);

        // 2b. radius and thickness through the same override
        var b3 = (IBaseFlangeFeatureData)bf.GetDefinition(); b3.AccessSelections(doc, null);
        b3.OverrideRadius = true; b3.BendRadius = 0.002; b3.OverrideThickness = true; b3.Thickness = 0.002;
        sb.AppendLine("2b write R=2 mm t=2 mm -> " + bf.ModifyDefinition(b3, doc, null) + "  " + Err(bf) + "  | " + FirstBend(doc) + "  box " + Box(doc));
        try { b3.ReleaseSelectionAccess(); } catch { }

        // 3. restore: remove the overrides again
        var b4 = (IBaseFlangeFeatureData)bf.GetDefinition(); b4.AccessSelections(doc, null);
        b4.OverrideKFactor = false; b4.OverrideRadius = false; b4.OverrideThickness = false; b4.OverrideDefaultSheetMetalParameters = false;
        sb.AppendLine("3 restore overrides -> " + bf.ModifyDefinition(b4, doc, null) + "  " + Err(bf) + "  | " + FirstBend(doc) + "  box " + Box(doc));
        try { b4.ReleaseSelectionAccess(); } catch { }
        var dd = (ISheetMetalFeatureData)sm.GetDefinition(); dd.AccessSelections(doc, null); dd.KFactor = k0; sm.ModifyDefinition(dd, doc, null); try { dd.ReleaseSelectionAccess(); } catch { }
        sb.AppendLine("end        : " + FirstBend(doc) + "  box " + Box(doc) + "  Sheet-Metal1 K=" + ((ISheetMetalFeatureData)sm.GetDefinition()).KFactor);
        return sb.ToString();
    }
}
