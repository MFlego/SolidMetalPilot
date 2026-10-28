// Probe 85: a BASE FLANGE with a gauge table, through CreateDefinition(swFmBaseFlange = 34) -> IBaseFlangeFeatureData. The swept flange took a table once UseGaugeTable was set BEFORE the table parameters were asked for
// and the FULL path was given (probe 83). Here the base flange's own table properties: UseGaugeTable, GaugeTablePath, ThicknessTableName (the gauge, spelled exactly as the table spells it - with its trailing blank),
// TableThickness, OverrideThickness, TableRadius, OverrideRadius, TableKFactor, OverrideKFactor, GetTableThicknesses(), GetTableRadii(name). A 100 x 60 mm rectangle on the Front Plane (made here) is the profile.
// Every step is logged to the file BEFORE the call. arg = "key=value;..."  log=<file> (required), table=<full path>, gaugeindex=<n>, create=1, radiusindex=<n>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder(); static string logPath = null;
    static void L(string s) { sb.Append(s).Append(" ;; "); if (logPath != null) { try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); } catch { } } }
    static string Mm(double v) { return (v * 1000).ToString("F4", CultureInfo.InvariantCulture); }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (arg ?? "").Split(';')) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null; if (logPath == null) return "log=<file> is required";
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager;
        try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
        doc.ClearSelection2(true); doc.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0); doc.SketchManager.InsertSketch(true);
        doc.SketchManager.CreateCornerRectangle(-0.05, -0.03, 0, 0.05, 0.03, 0); string skName = ((Feature)((Sketch)doc.SketchManager.ActiveSketch)).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true);
        L("profile sketch " + skName);
        doc.Extension.SelectByID2(skName, "SKETCH", 0, 0, 0, false, 0, null, 0);
        L("about to CreateDefinition(34)"); var d = fm.CreateDefinition(34) as IBaseFlangeFeatureData; L("   typed " + (d != null));
        if (d == null) return sb.ToString();
        string tbl = a.ContainsKey("table") ? a["table"] : null;
        if (tbl != null)
        {
            L("about to set UseGaugeTable = true"); d.UseGaugeTable = true; L("   -> " + d.UseGaugeTable);
            L("about to set GaugeTablePath"); d.GaugeTablePath = tbl; L("   -> '" + d.GaugeTablePath + "'");
            L("about to GetTableThicknessesCount"); try { L("   -> " + d.GetTableThicknessesCount()); } catch (Exception e) { L("   THREW " + e.Message); }
            L("about to GetTableThicknesses"); string[] names = null; try { var o = d.GetTableThicknesses() as object[]; names = o == null ? null : o.Select(x => x.ToString()).ToArray(); L("   -> " + (names == null ? "null" : string.Join(" | ", names))); } catch (Exception e) { L("   THREW " + e.Message); }
            if (names != null && a.ContainsKey("gaugeindex"))
            {
                string gname = names[int.Parse(a["gaugeindex"])]; L("about to set ThicknessTableName = '" + gname + "'"); d.ThicknessTableName = gname; L("   -> '" + d.ThicknessTableName + "'");
                L("   TableThickness " + Mm(d.TableThickness) + " mm, OverrideThickness " + d.OverrideThickness + ", TableRadius " + Mm(d.TableRadius) + " mm, OverrideRadius " + d.OverrideRadius + ", TableKFactor " + d.TableKFactor + ", KFactor " + d.KFactor + ", OverrideKFactor " + d.OverrideKFactor);
                L("about to GetTableRadii"); try { var rr = d.GetTableRadii(gname) as object[]; L("   -> " + (rr == null ? "null" : string.Join(", ", rr.Select(x => Mm(Convert.ToDouble(x))).ToArray()) + " mm")); } catch (Exception e) { L("   THREW " + e.Message); }
            }
        }
        if (a.ContainsKey("create"))
        {
            L("about to CreateFeature"); Feature f = null; try { f = fm.CreateFeature(d) as Feature; } catch (Exception e) { L("   THREW " + e.Message); }
            L("CreateFeature -> " + (f == null ? "null" : f.Name + " [" + f.GetTypeName2() + "]"));
        }
        return sb.ToString();
    }
}
