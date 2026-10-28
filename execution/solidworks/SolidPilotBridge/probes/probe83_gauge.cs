// Probe 83: SHEET-METAL GAUGE TABLES. The earlier probes (SetUseGaugeTable / IBaseFlangeFeatureData.UseGaugeTable) never got a table in. A swept flange definition holds the table parameters:
// ISweptFlangeFeatureData.GetGaugeTableParameters() -> ISheetMetalGaugeTableParameters (GetGaugeTablePath / SetGaugeTablePath / GetAvailableGaugeNumbers / GetCurrentGaugeNumber / SetCurrentGaugeNumber).
// The file location for gauge tables is ALREADY set to the install's "Sheet Metal Gauge Tables" folder (user preference 61, a plain string value - this probe never writes preferences). An empty scratch part,
// the sketches are made here; every step is logged to the file BEFORE the call. arg = "key=value;..."   keys: log=<file> (required), table=<full path or file name>, gauge=<number>, ugfirst=1 (set
// UseGaugeTable = true BEFORE asking for the parameters), create=1 (set the parameters back into the definition and CreateFeature), overrideoff=1 (do NOT set OverrideDefaultSheetMetalParameters)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder(); static string logPath = null;
    static void L(string s) { sb.Append(s).Append(" ;; "); if (logPath != null) { try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); } catch { } } }
    static string Poly(ModelDoc2 doc, string plane, double[][] pts)
    {
        doc.ClearSelection2(true); doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); doc.SketchManager.InsertSketch(true);
        for (int i = 0; i + 1 < pts.Length; i++) doc.SketchManager.CreateLine(pts[i][0], pts[i][1], 0, pts[i + 1][0], pts[i + 1][1], 0);
        string name = ((Feature)((Sketch)doc.SketchManager.ActiveSketch)).Name; doc.SketchManager.InsertSketch(true); doc.ClearSelection2(true); return name;
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (arg ?? "").Split(';')) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null; if (logPath == null) return "log=<file> is required";
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager;
        try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
        L("file location for gauge tables (preference 61): '" + sw.GetUserPreferenceStringValue(61) + "'");
        string pathName = Poly(doc, "Top Plane", new[] { new[] { 0.0, 0.0 }, new[] { 0.1, 0.0 } }), profName = Poly(doc, "Right Plane", new[] { new[] { 0.015, 0.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, 0.02 } });
        var pathFeat = (Feature)((PartDoc)doc).FeatureByName(pathName); var profFeat = (Feature)((PartDoc)doc).FeatureByName(profName); var profSk = (Sketch)profFeat.GetSpecificFeature2();
        L("about to CreateDefinition(117)"); var d = fm.CreateDefinition(117) as ISweptFlangeFeatureData;
        L("about to set Path / Profile"); d.Path = new[] { new DispatchWrapper(pathFeat) }; d.Profile = profSk;
        if (!a.ContainsKey("overrideoff")) { L("about to set OverrideDefaultSheetMetalParameters / Thickness / radius"); d.OverrideDefaultSheetMetalParameters = true; d.Thickness = 0.0015; d.UseDefaultRadius = false; d.BendRadius = 0.0015; }
        if (a.ContainsKey("ugfirst")) { L("about to set UseGaugeTable = true (first)"); d.UseGaugeTable = true; L("   UseGaugeTable " + d.UseGaugeTable); }
        L("about to GetGaugeTableParameters"); object gp = null; try { gp = d.GetGaugeTableParameters(); } catch (Exception e) { L("   THREW " + e.Message); }
        var g = gp as ISheetMetalGaugeTableParameters; L("   parameters object: " + (g == null ? "null" : "ok"));
        if (g != null)
        {
            string cur = null; L("about to GetGaugeTablePath"); try { bool ok = g.GetGaugeTablePath(ref cur); L("   -> " + ok + " '" + cur + "'"); } catch (Exception e) { L("   THREW " + e.Message); }
            if (a.ContainsKey("table"))
            {
                L("about to SetGaugeTablePath(" + a["table"] + ")"); try { L("   -> " + g.SetGaugeTablePath(a["table"])); } catch (Exception e) { L("   THREW " + e.Message); }
                L("about to GetGaugeNumberCount"); try { L("   -> " + g.GetGaugeNumberCount()); } catch (Exception e) { L("   THREW " + e.Message); }
                L("about to GetAvailableGaugeNumbers"); try { var nums = g.GetAvailableGaugeNumbers() as object[]; L("   -> " + (nums == null ? "null" : string.Join(", ", nums.Select(x => x.ToString()).ToArray()))); } catch (Exception e) { L("   THREW " + e.Message); }
                if (a.ContainsKey("gauge") || a.ContainsKey("gaugeindex"))
                {
                    string want = a.ContainsKey("gauge") ? a["gauge"] : null;      // the names carry trailing blanks ("16 Gauge "): gaugeindex=<n> takes the n-th name EXACTLY as the table spells it
                    if (a.ContainsKey("gaugeindex")) { var nums2 = g.GetAvailableGaugeNumbers() as object[]; want = nums2[int.Parse(a["gaugeindex"])].ToString(); }
                    L("about to SetCurrentGaugeNumber('" + want + "')"); try { L("   -> " + g.SetCurrentGaugeNumber(want)); } catch (Exception e) { L("   THREW " + e.Message); }
                }
                L("about to GetCurrentGaugeNumber"); try { L("   -> '" + g.GetCurrentGaugeNumber() + "'"); } catch (Exception e) { L("   THREW " + e.Message); }
                L("about to GetThickness / GetCurrentRadius / OverrideThickness / OverrideRadius");
                try { L("   thickness " + (g.GetThickness() * 1000).ToString("F4", CultureInfo.InvariantCulture) + " mm, radius " + (g.GetCurrentRadius() * 1000).ToString("F4", CultureInfo.InvariantCulture) + " mm, override thickness " + g.OverrideThickness + ", override radius " + g.OverrideRadius); } catch (Exception e) { L("   THREW " + e.Message); }
                if (a.ContainsKey("apply"))      // make the definition follow the table row: thickness and radius from the table, no override
                {
                    try { double tt = g.GetThickness(), rr = g.GetCurrentRadius(); L("about to SetThickness(" + tt + ", false) / SetRadius(" + rr + ", false)"); L("   -> " + g.SetThickness(tt, false) + " / " + g.SetRadius(rr, false)); } catch (Exception e) { L("   THREW " + e.Message); }
                    try { L("   after: thickness " + (g.GetThickness() * 1000).ToString("F4", CultureInfo.InvariantCulture) + " mm, radius " + (g.GetCurrentRadius() * 1000).ToString("F4", CultureInfo.InvariantCulture) + " mm, override thickness " + g.OverrideThickness + ", override radius " + g.OverrideRadius); } catch (Exception e) { L("   THREW " + e.Message); }
                }
            }
        }
        if (a.ContainsKey("create"))
        {
            if (!a.ContainsKey("ugfirst")) { L("about to set UseGaugeTable = true"); d.UseGaugeTable = true; }
            if (g != null) { L("about to SetGaugeTableParameters"); try { d.SetGaugeTableParameters(g); L("   done"); } catch (Exception e) { L("   THREW " + e.Message); } }
            L("about to CreateFeature"); Feature f = null; try { f = fm.CreateFeature(d) as Feature; } catch (Exception e) { L("   THREW " + e.Message); }
            L("CreateFeature -> " + (f == null ? "null" : f.Name));
            if (f != null)
            {
                Feature sm = null; var t = (Feature)doc.FirstFeature(); while (t != null) { if (t.GetTypeName2() == "SheetMetal") sm = t; t = (Feature)t.GetNextFeature(); }
                var sd = sm == null ? null : sm.GetDefinition() as ISheetMetalFeatureData;
                if (sd != null) { bool acc = sd.AccessSelections(doc, null); L("sheet metal: thickness " + (sd.Thickness * 1000).ToString("F4", CultureInfo.InvariantCulture) + " mm, radius " + (sd.BendRadius * 1000).ToString("F4", CultureInfo.InvariantCulture) + " mm"); bool use = false; string file = null; L("   GetUseGaugeTable -> " + sd.GetUseGaugeTable(out use, out file) + " use " + use + " file '" + file + "'"); sd.ReleaseSelectionAccess(); }
            }
        }
        return sb.ToString();
    }
}
