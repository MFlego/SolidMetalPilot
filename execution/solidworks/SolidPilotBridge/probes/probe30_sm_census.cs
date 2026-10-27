// Probe 30: typed census of the ACTIVE sheet-metal part. Walks the feature tree and, for every sheet-metal feature type it knows, dumps the FULL typed feature definition by reflection
// (every readable property of the matching *FeatureData interface), so we can see exactly what the API exposes and holds. Read-only.  arg = optional feature-name filter (substring).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static readonly Dictionary<string, Type> Defs = new Dictionary<string, Type>
    {
        { "SheetMetal", typeof(ISheetMetalFeatureData) }, { "SMBaseFlange", typeof(IBaseFlangeFeatureData) }, { "EdgeFlange", typeof(IEdgeFlangeFeatureData) },
        { "OneBend", typeof(IOneBendFeatureData) }, { "FlatPattern", typeof(IFlatPatternFeatureData) }, { "SM3dBend", typeof(ISketchedBendFeatureData) },
        { "ClosedCorner", typeof(IClosedCornerFeatureData) }, { "CornerFeat", typeof(IClosedCornerFeatureData) }, { "BreakCorner", typeof(IBreakCornerFeatureData) }, { "CornerTrim", typeof(IBreakCornerFeatureData) },
        { "MoveFace", typeof(IMoveFaceFeatureData) }, { "SMHem", typeof(IHemFeatureData) }, { "Hem", typeof(IHemFeatureData) }, { "Jog", typeof(IJogFeatureData) }, { "SMJog", typeof(IJogFeatureData) },
        { "MiterFlange", typeof(IMiterFlangeFeatureData) }, { "SMMiteredFlange", typeof(IMiterFlangeFeatureData) }, { "Rip", typeof(IRipFeatureData) }, { "FlatPatternRip", typeof(IRipFeatureData) },
        { "CornerRelief", typeof(ICornerReliefFeatureData) }, { "ProcessBends", typeof(IBendsFeatureData) }, { "FlattenBends", typeof(IBendsFeatureData) }, { "LoftedBend", typeof(ILoftedBendsFeatureData) },
        { "SolidToSheetMetal", typeof(IConvertSolidFeatureData) }, { "UnFold", typeof(IFoldsFeatureData) }, { "Fold", typeof(IFoldsFeatureData) }
    };

    static string Fmt(object v)
    {
        if (v == null) return "null";
        var arr = v as Array;
        if (arr != null) { var parts = new List<string>(); int n = 0; foreach (var o in arr) { if (n++ >= 5) { parts.Add("..."); break; } parts.Add(Fmt(o)); } return "[" + arr.Length + ": " + string.Join(", ", parts.ToArray()) + "]"; }
        if (v is double) return ((double)v).ToString("G6");
        if (v is string) return "'" + v + "'";
        if (v.GetType().IsCOMObject)
        {
            var f = v as IFeature; if (f != null) return "Feature(" + f.Name + ")";
            if (v is IFace2) return "Face"; if (v is IEdge) return "Edge"; if (v is ISketch) return "Sketch"; return "COM";
        }
        return v.ToString();
    }

    static string Dump(object def, Type iface)
    {
        var sb = new StringBuilder();
        foreach (var p in iface.GetProperties().OrderBy(x => x.Name))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            string val; try { val = Fmt(p.GetValue(def, null)); } catch (Exception e) { val = "!" + (e.InnerException != null ? e.InnerException.Message : e.Message); }
            sb.Append("      " + p.Name + (p.CanWrite ? " (rw)" : " (ro)") + " = " + val + "\n");
        }
        return sb.ToString();
    }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var sb = new StringBuilder(); sb.AppendLine("doc " + doc.GetTitle() + "  bend state " + SafeBendState(doc));
        var f = (Feature)doc.FirstFeature(); int i = 0;
        while (f != null)
        {
            i++; string tn = f.GetTypeName2(); bool has = Defs.ContainsKey(tn);
            if (i > 4 && (has || !string.IsNullOrEmpty(arg)) && (string.IsNullOrEmpty(arg) || f.Name.Contains(arg) || tn.Contains(arg)))
            {
                bool w; int err = 0; try { err = f.GetErrorCode2(out w); } catch { }
                sb.AppendLine(i + " " + f.Name + " [" + tn + "]" + (f.IsSuppressed() ? " suppressed" : "") + (err != 0 ? " ERROR " + err : ""));
                if (has)
                {
                    try
                    {
                        object def = f.GetDefinition(); bool access = false;
                        try { var m = def.GetType().InvokeMember("AccessSelections", BindingFlags.InvokeMethod, null, def, new object[] { doc, null }); access = m is bool && (bool)m; } catch { }
                        sb.Append(Dump(def, Defs[tn]));
                        if (access) try { def.GetType().InvokeMember("ReleaseSelectionAccess", BindingFlags.InvokeMethod, null, def, null); } catch { }
                    }
                    catch (Exception e) { sb.AppendLine("      (definition unavailable: " + e.Message + ")"); }
                }
            }
            f = (Feature)f.GetNextFeature();
        }
        return sb.ToString();
    }

    static string SafeBendState(ModelDoc2 d) { try { return d.GetBendState().ToString(); } catch (Exception e) { return "n/a (" + e.Message + ")"; } }
}
