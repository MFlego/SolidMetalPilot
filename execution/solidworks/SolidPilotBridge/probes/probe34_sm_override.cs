// Probe 34: the documented way to set the GLOBAL bend allowance / K-factor: ISheetMetalFeatureData.SetOverrideDefaultParameter2(BendAllowance, true) + a CustomBendAllowance object.
// Then the same idea for auto relief and bend parameters, and what the feature tree looks like afterwards (dimensions, per-bend values). Restores at the end.
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string FirstBend(ModelDoc2 doc)
    {
        var f = (Feature)doc.FirstFeature();
        while (f != null) { var s = (Feature)f.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") { var d = (IOneBendFeatureData)s.GetDefinition(); return s.Name + " K=" + d.KFactor.ToString("G4") + " BA=" + (d.BendAllowance * 1000).ToString("F4") + " mm type=" + d.BendAllowanceType + " R=" + (d.BendRadius * 1000).ToString("F3") + " useDefaultBA=" + d.UseDefaultBendAllowance; } s = (Feature)s.GetNextSubFeature(); } f = (Feature)f.GetNextFeature(); }
        return "no bend";
    }
    static string Dims(Feature f)
    {
        var sb = new StringBuilder(); var dd = (DisplayDimension)f.GetFirstDisplayDimension();
        while (dd != null) { var d = (Dimension)dd.GetDimension2(0); var v = (double[])d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null); sb.Append(d.FullName + "=" + (v != null && v.Length > 0 ? v[0].ToString("G6") : "?") + "  "); dd = (DisplayDimension)f.GetNextDisplayDimension(dd); }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }
    static string Ovr(ISheetMetalFeatureData d)
    {
        var sb = new StringBuilder();
        for (int p = 0; p <= 2; p++) { bool o = false; int rc = d.GetOverrideDefaultParameter2(p, out o); sb.Append("[" + p + "]=" + o + (rc != 0 ? "(rc " + rc + ")" : "") + " "); }
        return sb.ToString();
    }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager; var sb = new StringBuilder();
        var sm = (Feature)part.FeatureByName("Sheet-Metal1");
        var d = (ISheetMetalFeatureData)sm.GetDefinition(); d.AccessSelections(doc, null);
        sb.AppendLine("before : overrides " + Ovr(d) + " | " + FirstBend(doc) + " | dims " + Dims(sm));
        var cba0 = d.GetCustomBendAllowance(); sb.AppendLine("         custom allowance object: " + (cba0 == null ? "null" : "type=" + cba0.Type + " K=" + cba0.KFactor + " BA=" + cba0.BendAllowance));

        // 1. override the bend allowance with a K-factor
        int rc = d.SetOverrideDefaultParameter2((int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_BendAllowance, true);
        var cba = cba0 ?? (CustomBendAllowance)fm.CreateCustomBendAllowance();
        cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor; cba.KFactor = 0.42; d.SetCustomBendAllowance(cba);
        sb.AppendLine("1 SetOverride(BendAllowance)=" + rc + "  ModifyDefinition -> " + sm.ModifyDefinition(d, doc, null));
        try { d.ReleaseSelectionAccess(); } catch { }
        var r = (ISheetMetalFeatureData)sm.GetDefinition(); r.AccessSelections(doc, null); var rc2 = r.GetCustomBendAllowance();
        sb.AppendLine("  after : overrides " + Ovr(r) + " | custom K=" + (rc2 == null ? "null" : rc2.KFactor.ToString("G4") + " type " + rc2.Type) + " | " + FirstBend(doc) + " | dims " + Dims(sm));
        try { r.ReleaseSelectionAccess(); } catch { }

        // 2. auto relief override (type + ratio)
        var a = (ISheetMetalFeatureData)sm.GetDefinition(); a.AccessSelections(doc, null);
        int rcA = a.SetOverrideDefaultParameter2((int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_AutoRelief, true);
        a.UseAutoRelief = true; a.AutoReliefType = (int)swSheetMetalAutoReliefTypes_e.swSheetMetalAutoReliefTypes_e_Tear; a.ReliefRatio = 0.6;
        sb.AppendLine("2 SetOverride(AutoRelief)=" + rcA + "  type=Tear ratio=0.6  ModifyDefinition -> " + sm.ModifyDefinition(a, doc, null)); try { a.ReleaseSelectionAccess(); } catch { }
        var ra = (ISheetMetalFeatureData)sm.GetDefinition(); sb.AppendLine("  after : overrides " + Ovr(ra) + " | AutoReliefType=" + ra.AutoReliefType + " ratio=" + ra.ReliefRatio + " | dims " + Dims(sm));

        // 3. restore everything
        var z = (ISheetMetalFeatureData)sm.GetDefinition(); z.AccessSelections(doc, null);
        z.SetOverrideDefaultParameter2(1, false); z.SetOverrideDefaultParameter2(2, false);
        sb.AppendLine("3 restore (overrides off) -> " + sm.ModifyDefinition(z, doc, null)); try { z.ReleaseSelectionAccess(); } catch { }
        var rz = (ISheetMetalFeatureData)sm.GetDefinition(); sb.AppendLine("  end   : overrides " + Ovr(rz) + " | AutoReliefType=" + rz.AutoReliefType + " ratio=" + rz.ReliefRatio + " | " + FirstBend(doc));
        return sb.ToString();
    }
}
