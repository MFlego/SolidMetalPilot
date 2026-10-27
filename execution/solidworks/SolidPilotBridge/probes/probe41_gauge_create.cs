// Probe 41: gauge tables at BASE-FLANGE CREATION. Setting them afterwards is refused (ISheetMetalFeatureData.SetUseGaugeTable -> 2 = NotEnabledOnTemplate; the base flange's UseGaugeTable /
// GaugeTablePath writes are ignored), so: does CreateDefinition(swFmBaseFlange) + UseGaugeTable / GaugeTablePath / ThicknessTableName / TableRadius BEFORE CreateFeature build a part driven by the table?
// Needs: a NEW part with the profile sketch still ACTIVE (open_new_part, create_sketch, add_sketch_entities).   arg = full path of the gauge table .xlsx
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void L(string s) { sb.Append(s).Append(" ;; "); }
    static string Fmt(object v)
    {
        if (v == null) return "null"; var arr = v as Array;
        if (arr != null) { var parts = new System.Collections.Generic.List<string>(); foreach (var o in arr) parts.Add(Fmt(o)); return "[" + string.Join(", ", parts.ToArray()) + "]"; }
        if (v is double) return ((double)v).ToString("G7"); if (v is string) return "'" + v + "'"; return v.ToString();
    }
    static void Try(string label, Func<object> f) { try { L(label + " -> " + Fmt(f())); } catch (Exception e) { L(label + " THREW " + (e.InnerException != null ? e.InnerException.Message : e.Message)); } }
    static string Size(ModelDoc2 doc)
    {
        var bs = (object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false); if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox();
        return ((x[3] - x[0]) * 1000).ToString("F3") + " x " + ((x[4] - x[1]) * 1000).ToString("F3") + " x " + ((x[5] - x[2]) * 1000).ToString("F3") + " mm";
    }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var fm = doc.FeatureManager; string gt = arg;
        if (doc.SketchManager.ActiveSketch == null) return "no active sketch: run open_new_part, create_sketch, add_sketch_entities first";
        string skName = ((Feature)doc.SketchManager.ActiveSketch).Name;
        doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true);
        L("select sketch " + skName + " -> " + doc.Extension.SelectByID2(skName, "SKETCH", 0, 0, 0, false, 0, null, 0));
        var bf = (IBaseFlangeFeatureData)fm.CreateDefinition((int)swFeatureNameID_e.swFmBaseFlange);
        L("fresh definition: UseGaugeTable=" + bf.UseGaugeTable + " path=" + Fmt(bf.GaugeTablePath) + " table=" + Fmt(bf.ThicknessTableName) + " thickness=" + bf.Thickness + " radius=" + bf.BendRadius + " tableK=" + bf.TableKFactor);
        Try("GetTableThicknessesCount (no path yet)", () => bf.GetTableThicknessesCount());
        Try("set GaugeTablePath", () => { bf.GaugeTablePath = gt; return bf.GaugeTablePath; });
        Try("GetTableThicknessesCount", () => bf.GetTableThicknessesCount());
        Try("GetTableThicknesses", () => bf.GetTableThicknesses());
        Try("set UseGaugeTable", () => { bf.UseGaugeTable = true; return bf.UseGaugeTable; });
        Try("GetTableThicknessesCount (after UseGaugeTable)", () => bf.GetTableThicknessesCount());
        Try("GetTableThicknesses (after UseGaugeTable)", () => bf.GetTableThicknesses());
        var names = bf.GetTableThicknesses() as string[]; string pick = null;
        if (names != null) { pick = names.FirstOrDefault(n => n.IndexOf("16", StringComparison.Ordinal) >= 0) ?? names.FirstOrDefault(); L("table entries: " + string.Join(" | ", names)); }
        if (pick != null)
        {
            Try("GetTableRadiiCount('" + pick + "')", () => bf.GetTableRadiiCount(pick));
            Try("GetTableRadii('" + pick + "')", () => bf.GetTableRadii(pick));
            Try("set ThicknessTableName", () => { bf.ThicknessTableName = pick; return bf.ThicknessTableName; });
            L("after the name: TableThickness=" + bf.TableThickness + " TableRadius=" + bf.TableRadius + " TableK=" + bf.TableKFactor + " Thickness=" + bf.Thickness + " BendRadius=" + bf.BendRadius);
            var radii = bf.GetTableRadii(pick) as double[]; if (radii != null && radii.Length > 0) Try("set TableRadius = first radius", () => { bf.TableRadius = radii[0]; return bf.TableRadius; });
        }
        Feature f = null;
        try { f = (Feature)fm.CreateFeature(bf); } catch (Exception e) { L("CreateFeature THREW " + e.Message); }
        L("CreateFeature -> " + (f == null ? "null" : f.Name));
        if (f != null)
        {
            bool w; int err = 0; try { err = f.GetErrorCode2(out w); } catch { }
            L("error code " + err + ", body " + Size(doc));
            var back = (IBaseFlangeFeatureData)f.GetDefinition(); back.AccessSelections(doc, null);
            L("read back: UseGaugeTable=" + back.UseGaugeTable + " path=" + Fmt(back.GaugeTablePath) + " table=" + Fmt(back.ThicknessTableName) + " TableThickness=" + back.TableThickness + " TableRadius=" + back.TableRadius + " TableK=" + back.TableKFactor + " Thickness=" + back.Thickness + " BendRadius=" + back.BendRadius + " OverrideThickness=" + back.OverrideThickness);
            try { back.ReleaseSelectionAccess(); } catch { }
            Feature s = null; var ft = (Feature)doc.FirstFeature(); while (ft != null) { if (ft.GetTypeName2() == "SheetMetal") s = ft; ft = (Feature)ft.GetNextFeature(); }
            if (s != null)
            {
                var d = (ISheetMetalFeatureData)s.GetDefinition(); d.AccessSelections(doc, null); bool g; string file = ""; int rc = d.GetUseGaugeTable(out g, out file);
                L("Sheet-Metal1: thickness=" + d.Thickness + " radius=" + d.BendRadius + " K=" + d.KFactor + " allowanceType=" + d.BendAllowanceType + " GetUseGaugeTable rc=" + rc + " use=" + g + " file=" + Fmt(file));
                try { d.ReleaseSelectionAccess(); } catch { }
            }
        }
        return sb.ToString();
    }
}
