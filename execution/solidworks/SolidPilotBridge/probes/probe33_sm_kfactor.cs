// Probe 33: restore the fixture to 1.5 mm / R1.5 and hunt for the working K-factor / bend-allowance control.
//   variants: BendAllowanceType Direct + BendAllowance | UseMaterialSheetMetalParameters | the dimensions the features own (D1@Sheet-Metal1 ... ) | the per-bend OneBend definition
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string FirstBend(ModelDoc2 doc)
    {
        var f = (Feature)doc.FirstFeature();
        while (f != null) { var s = (Feature)f.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") { var d = (IOneBendFeatureData)s.GetDefinition(); return s.Name + " K=" + d.KFactor.ToString("G4") + " BA=" + (d.BendAllowance * 1000).ToString("F4") + " mm type=" + d.BendAllowanceType + " R=" + (d.BendRadius * 1000).ToString("F3") + " mm"; } s = (Feature)s.GetNextSubFeature(); } f = (Feature)f.GetNextFeature(); }
        return "no bend";
    }
    static string Dims(Feature f)
    {
        var sb = new StringBuilder(); var dd = (DisplayDimension)f.GetFirstDisplayDimension();
        while (dd != null) { var d = (Dimension)dd.GetDimension2(0); sb.Append(d.FullName + "=" + d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) + "  "); dd = (DisplayDimension)f.GetNextDisplayDimension(dd); }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder();
        var sm = (Feature)part.FeatureByName("Sheet-Metal1"); var bf = (Feature)part.FeatureByName("Base-Flange1");
        // restore 1.5 / 1.5 through the base-flange overrides (the working path found in probe 32)
        var b = (IBaseFlangeFeatureData)bf.GetDefinition(); b.AccessSelections(doc, null); b.OverrideRadius = true; b.BendRadius = 0.0015; b.OverrideThickness = true; b.Thickness = 0.0015;
        sb.AppendLine("restore 1.5/1.5 -> " + bf.ModifyDefinition(b, doc, null) + " | " + FirstBend(doc)); try { b.ReleaseSelectionAccess(); } catch { }
        sb.AppendLine("dimensions  Sheet-Metal1: " + Dims(sm)); sb.AppendLine("dimensions  Base-Flange1: " + Dims(bf));
        var ef = (Feature)part.FeatureByName("Edge-Flange1"); sb.AppendLine("dimensions  Edge-Flange1: " + Dims(ef));

        // variant a: direct bend allowance
        var d = (ISheetMetalFeatureData)sm.GetDefinition(); d.AccessSelections(doc, null);
        d.BendAllowanceType = (int)swBendAllowanceTypes_e.swBendAllowanceDirect; d.BendAllowance = 0.0023;
        sb.AppendLine("a Direct BA=2.3 mm -> " + sm.ModifyDefinition(d, doc, null) + " read: type=" + ((ISheetMetalFeatureData)sm.GetDefinition()).BendAllowanceType + " BA=" + ((ISheetMetalFeatureData)sm.GetDefinition()).BendAllowance + " | " + FirstBend(doc)); try { d.ReleaseSelectionAccess(); } catch { }

        // variant b: the sheet-metal definition with material parameters switched off explicitly + K
        var d2 = (ISheetMetalFeatureData)sm.GetDefinition(); d2.AccessSelections(doc, null);
        d2.UseMaterialSheetMetalParameters = false; d2.BendAllowanceType = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor; d2.KFactor = 0.42;
        sb.AppendLine("b material off + K=0.42 -> " + sm.ModifyDefinition(d2, doc, null) + " read: K=" + ((ISheetMetalFeatureData)sm.GetDefinition()).KFactor + " | " + FirstBend(doc)); try { d2.ReleaseSelectionAccess(); } catch { }

        // variant c: the bend itself (a per-bend K override)
        Feature bend = null; var f = (Feature)doc.FirstFeature();
        while (f != null && bend == null) { var s = (Feature)f.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") { bend = s; break; } s = (Feature)s.GetNextSubFeature(); } f = (Feature)f.GetNextFeature(); }
        if (bend != null)
        {
            var od = (IOneBendFeatureData)bend.GetDefinition(); od.AccessSelections(doc, null);
            sb.AppendLine("c bend " + bend.Name + " before: useDefaultBA=" + od.UseDefaultBendAllowance + " K=" + od.KFactor + " type=" + od.BendAllowanceType);
            od.UseDefaultBendAllowance = false; od.BendAllowanceType = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor; od.KFactor = 0.42;
            sb.AppendLine("c per-bend override K=0.42 -> " + bend.ModifyDefinition(od, doc, null) + " read: " + FirstBend(doc)); try { od.ReleaseSelectionAccess(); } catch { }
            sb.AppendLine("c dimensions of the bend: " + Dims(bend));
        }
        return sb.ToString();
    }
}
