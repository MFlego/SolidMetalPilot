// Probe 31: do typed WRITES to the sheet-metal definitions take effect?  On the ACTIVE sheet-metal fixture (sm_fixture.ps1):
//   A  Sheet-Metal1 (ISheetMetalFeatureData): KFactor / BendAllowanceType / thickness -> ModifyDefinition -> read back + effect on the per-bend values
//   B  Edge-Flange1 (IEdgeFlangeFeatureData): OffsetDistance (flange length) -> folded bounding box changes
//   C  OneBend sub-features: what they hold (K, allowance, angle, radius)
// Everything is put back afterwards. Prints one line per observation.
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Box(ModelDoc2 doc)
    {
        var part = (PartDoc)doc; var bs = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false); if (bs == null || bs.Length == 0) return "no body";
        var b = (double[])((Body2)bs[0]).GetBodyBox(); return ((b[3] - b[0]) * 1000).ToString("F3") + " x " + ((b[4] - b[1]) * 1000).ToString("F3") + " x " + ((b[5] - b[2]) * 1000).ToString("F3") + " mm";
    }

    static List<OneBendInfo> Bends(ModelDoc2 doc)
    {
        var res = new List<OneBendInfo>(); var f = (Feature)doc.FirstFeature();
        while (f != null)
        {
            var s = (Feature)f.GetFirstSubFeature();
            while (s != null)
            {
                if (s.GetTypeName2() == "OneBend") { var d = (IOneBendFeatureData)s.GetDefinition(); res.Add(new OneBendInfo { Name = s.Name, K = d.KFactor, BA = d.BendAllowance, BAType = d.BendAllowanceType, Radius = d.BendRadius, Angle = d.BendAngle, UseDefBA = d.UseDefaultBendAllowance }); }
                s = (Feature)s.GetNextSubFeature();
            }
            f = (Feature)f.GetNextFeature();
        }
        return res;
    }
    class OneBendInfo { public string Name; public double K, BA, Radius, Angle; public int BAType; public bool UseDefBA; }
    static string BendLine(OneBendInfo b) { return b.Name + ": K=" + b.K.ToString("G4") + " BA=" + (b.BA * 1000).ToString("F4") + " mm type=" + b.BAType + " R=" + (b.Radius * 1000).ToString("F3") + " angle=" + (b.Angle * 180 / Math.PI).ToString("F1") + " useDefaultBA=" + b.UseDefBA; }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder();
        sb.AppendLine("start: folded box " + Box(doc));
        var bends = Bends(doc); sb.AppendLine("bends found: " + bends.Count); foreach (var b in bends) sb.AppendLine("   " + BendLine(b));

        // ---- A: global sheet-metal parameters
        var sm = (Feature)part.FeatureByName("Sheet-Metal1"); var d = (ISheetMetalFeatureData)sm.GetDefinition();
        double k0 = d.KFactor; int t0 = d.BendAllowanceType; double r0 = d.BendRadius, th0 = d.Thickness;
        sb.AppendLine("A before: K=" + k0 + " type=" + t0 + " R=" + r0 * 1000 + " mm t=" + th0 * 1000 + " mm");
        d.KFactor = 0.42; d.BendAllowanceType = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
        bool ok = sm.ModifyDefinition(d, doc, null); sb.AppendLine("A ModifyDefinition(K=0.42) -> " + ok);
        var d2 = (ISheetMetalFeatureData)sm.GetDefinition(); sb.AppendLine("A after : K=" + d2.KFactor + " type=" + d2.BendAllowanceType);
        foreach (var b in Bends(doc)) sb.AppendLine("   " + BendLine(b));
        // put it back
        d2.KFactor = k0; d2.BendAllowanceType = t0; sb.AppendLine("A restore -> " + sm.ModifyDefinition(d2, doc, null) + "  K now " + ((ISheetMetalFeatureData)sm.GetDefinition()).KFactor);

        // thickness / radius write
        var d3 = (ISheetMetalFeatureData)sm.GetDefinition(); d3.Thickness = 0.002; d3.BendRadius = 0.002;
        sb.AppendLine("A ModifyDefinition(t=2 mm, R=2 mm) -> " + sm.ModifyDefinition(d3, doc, null) + "  folded box " + Box(doc));
        var d4 = (ISheetMetalFeatureData)sm.GetDefinition(); d4.Thickness = th0; d4.BendRadius = r0;
        sb.AppendLine("A restore -> " + sm.ModifyDefinition(d4, doc, null) + "  folded box " + Box(doc));

        // ---- B: a flange length
        var ef = (Feature)part.FeatureByName("Edge-Flange1"); var ed = (IEdgeFlangeFeatureData)ef.GetDefinition(); double len0 = ed.OffsetDistance;
        sb.AppendLine("B Edge-Flange1 length before " + len0 * 1000 + " mm, folded box " + Box(doc));
        bool acc = ed.AccessSelections(doc, null); ed.OffsetDistance = 0.035; sb.AppendLine("B ModifyDefinition(length 35 mm) -> " + ef.ModifyDefinition(ed, doc, null) + "  folded box " + Box(doc)); try { ed.ReleaseSelectionAccess(); } catch { }
        var ed2 = (IEdgeFlangeFeatureData)ef.GetDefinition(); bool acc2 = ed2.AccessSelections(doc, null); ed2.OffsetDistance = len0; sb.AppendLine("B restore -> " + ef.ModifyDefinition(ed2, doc, null) + "  folded box " + Box(doc)); try { ed2.ReleaseSelectionAccess(); } catch { }
        sb.AppendLine("end: folded box " + Box(doc) + "  (access flags " + acc + "/" + acc2 + ")");
        return sb.ToString();
    }
}
