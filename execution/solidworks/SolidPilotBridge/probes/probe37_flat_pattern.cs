// Probe 37: flat-pattern control through the typed API on the ACTIVE sheet-metal part.
//   1 unsuppress the Flat-Pattern feature, measure the flat blank (bbox, faces, edges)
//   2 read IFlatPatternFeatureData and flip its options one by one (CornerTreatment, SimplifyBends, MergeFace, ShowSlitInCornerRelief ...) -> what changes in the flat geometry
//   3 ExportFlatPatternView to a DXF (with and without bend lines removed)
//   4 put the flat pattern back to suppressed
// arg = folder for the DXF files (default: the temp folder)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Flat(ModelDoc2 doc)
    {
        var bs = (object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false); if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox();
        return ((x[3] - x[0]) * 1000).ToString("F3") + " x " + ((x[4] - x[1]) * 1000).ToString("F3") + " x " + ((x[5] - x[2]) * 1000).ToString("F3") + " mm, faces " + b.GetFaceCount() + ", edges " + b.GetEdgeCount();
    }
    static string Opts(IFlatPatternFeatureData d)
    {
        return "CornerTreatment=" + d.CornerTreatment + " SimplifyBends=" + d.SimplifyBends + " MergeFace=" + d.MergeFace + " ShowSlit=" + d.ShowSlitInCornerRelief + " TrimReliefType=" + d.CornerTrimReliefType + " TrimReliefDist=" + (d.CornerTrimReliefDistance * 1000).ToString("F3") + " mm BreakType=" + d.BreakCornerType + " BreakRadius=" + (d.BreakCornerRadius * 1000).ToString("F3") + " mm UseRatio=" + d.UseRatioToThickness + " ratio=" + d.CornerTrimRatioToThickness;
    }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var sb = new StringBuilder(); string folder = string.IsNullOrEmpty(arg) ? Path.GetTempPath() : arg;
        Feature fp = null; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.GetTypeName2() == "FlatPattern") fp = f; f = (Feature)f.GetNextFeature(); }
        if (fp == null) return "no Flat-Pattern feature";
        sb.AppendLine("folded    : " + Flat(doc));
        bool ok = fp.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null);
        doc.EditRebuild3(); bool w; int err = 0; try { err = fp.GetErrorCode2(out w); } catch { }
        sb.AppendLine("unsuppress flat pattern -> " + ok + " (error " + err + ", suppressed now " + fp.IsSuppressed() + ", bend state " + doc.GetBendState() + ")");
        sb.AppendLine("flat blank: " + Flat(doc));

        var d = (IFlatPatternFeatureData)fp.GetDefinition(); d.AccessSelections(doc, null); sb.AppendLine("options   : " + Opts(d)); try { d.ReleaseSelectionAccess(); } catch { }

        // flip the options one at a time
        var toggles = new Dictionary<string, Action<IFlatPatternFeatureData>>
        {
            { "CornerTreatment off", x => x.CornerTreatment = false },
            { "SimplifyBends off", x => x.SimplifyBends = false },
            { "MergeFace off", x => x.MergeFace = false },
            { "ShowSlitInCornerRelief off", x => x.ShowSlitInCornerRelief = false }
        };
        foreach (var kv in toggles)
        {
            var x = (IFlatPatternFeatureData)fp.GetDefinition(); x.AccessSelections(doc, null); kv.Value(x);
            bool m = fp.ModifyDefinition(x, doc, null); try { x.ReleaseSelectionAccess(); } catch { }
            sb.AppendLine("  " + kv.Key + " -> " + m + "  | " + Flat(doc));
            var back = (IFlatPatternFeatureData)fp.GetDefinition(); back.AccessSelections(doc, null);
            back.CornerTreatment = d.CornerTreatment; back.SimplifyBends = d.SimplifyBends; back.MergeFace = d.MergeFace; back.ShowSlitInCornerRelief = d.ShowSlitInCornerRelief;
            fp.ModifyDefinition(back, doc, null); try { back.ReleaseSelectionAccess(); } catch { }
        }
        sb.AppendLine("restored  : " + Flat(doc));

        // direct DXF export of the flat pattern: IPartDoc.ExportToDWG2 (action 1 = sheet metal) needs the model's path, so an unsaved fixture is saved to the folder first.
        // (ExportFlatPatternView raises an interactive Save As dialog on this SolidWorks - do not use it from automation.)
        if (string.IsNullOrEmpty(doc.GetPathName()))
        {
            int e1 = 0, w1 = 0; string mp = Path.Combine(folder, "probe37_fixture.SLDPRT");
            bool saved = doc.Extension.SaveAs3(mp, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e1, ref w1);
            sb.AppendLine("saved fixture -> " + saved + " (" + doc.GetPathName() + ")");
        }
        foreach (int flags in new[] { 1, 5 })
        {
            string path = Path.Combine(folder, "probe37_flat_flags" + flags + ".dxf"); if (File.Exists(path)) File.Delete(path);
            bool e = part.ExportToDWG2(path, doc.GetPathName(), 1, true, null, false, false, flags, null);
            sb.AppendLine("ExportToDWG2(flags " + flags + ") -> " + e + "  file " + (File.Exists(path) ? new FileInfo(path).Length + " bytes  " + path : "MISSING"));
        }
        fp.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); doc.EditRebuild3();
        sb.AppendLine("flat pattern suppressed again -> folded " + Flat(doc));
        return sb.ToString();
    }
}
