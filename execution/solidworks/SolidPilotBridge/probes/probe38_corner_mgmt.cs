// Probe 38: Corner Management on the ACTIVE sheet-metal part: the Corner-Management folder(s), the corner-treatment features inside them (Simple / Two-Member / Complex) and their typed definitions
// (CornerTreatmentTrimType, PlanarTrimOption, WeldTrimGap, MiterTrimPlanePoint ...). Read-only unless arg = "write": then every corner treatment is switched to mitre trim with a 0.0762 mm weld gap and read back.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Fmt(object v)
    {
        if (v == null) return "null"; var arr = v as Array;
        if (arr != null) return "[" + arr.Length + " items]";
        if (v is double) return ((double)v).ToString("G6"); if (v is string) return "'" + v + "'";
        if (v.GetType().IsCOMObject) { var f = v as IFeature; return f != null ? "Feature(" + f.Name + ")" : "COM"; }
        return v.ToString();
    }
    static string Dump(object def, Type iface)
    {
        var sb = new StringBuilder();
        foreach (var p in iface.GetProperties().OrderBy(x => x.Name))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue; string val;
            try { val = Fmt(p.GetValue(def, null)); } catch (Exception e) { val = "!" + (e.InnerException != null ? e.InnerException.Message : e.Message); }
            sb.Append("      " + p.Name + (p.CanWrite ? " (rw)" : " (ro)") + " = " + val + "\n");
        }
        return sb.ToString();
    }
    static void Walk(Feature f, int depth, StringBuilder sb, List<Feature> found)
    {
        while (f != null)
        {
            string tn = f.GetTypeName2(); sb.AppendLine(new string(' ', depth * 3) + f.Name + " [" + tn + "]"); found.Add(f);
            var sub = (Feature)f.GetFirstSubFeature(); if (sub != null && depth < 3) Walk(sub, depth + 1, sb, found);
            f = depth == 0 ? null : (Feature)f.GetNextSubFeature();
        }
    }

    static List<string> TopTypes(ModelDoc2 doc) { var types = new SortedDictionary<string, int>(); var tf = (Feature)doc.FirstFeature(); while (tf != null) { var t = tf.GetTypeName2(); types[t] = types.ContainsKey(t) ? types[t] + 1 : 1; tf = (Feature)tf.GetNextFeature(); } return types.Select(kv => kv.Key + "x" + kv.Value).ToList(); }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var fm = doc.FeatureManager; var sb = new StringBuilder();
        Feature flat = null; bool wasSuppressed = false;
        if (arg != null && arg.StartsWith("flat"))
        {
            var ff0 = (Feature)doc.FirstFeature(); while (ff0 != null) { if (ff0.GetTypeName2() == "FlatPattern") flat = ff0; ff0 = (Feature)ff0.GetNextFeature(); }
            if (flat != null) { wasSuppressed = flat.IsSuppressed(); if (wasSuppressed) { flat.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); doc.EditRebuild3(); } }
            sb.AppendLine("flat pattern unsuppressed; walking its sub-features:");
            var all = new List<Feature>(); var sub = (Feature)flat.GetFirstSubFeature();
            while (sub != null)
            {
                sb.AppendLine("   " + sub.Name + " [" + sub.GetTypeName2() + "]"); all.Add(sub);
                var sub2 = (Feature)sub.GetFirstSubFeature(); while (sub2 != null) { sb.AppendLine("      " + sub2.Name + " [" + sub2.GetTypeName2() + "]"); all.Add(sub2); sub2 = (Feature)sub2.GetNextSubFeature(); }
                sub = (Feature)sub.GetNextSubFeature();
            }
            try { var ffold = fm.GetFlatPatternFolder() as Feature; sb.AppendLine("GetFlatPatternFolder -> " + (ffold == null ? "null" : ffold.Name + " [" + ffold.GetTypeName2() + "]")); } catch (Exception e) { sb.AppendLine("GetFlatPatternFolder threw " + e.Message); }
            sb.AppendLine("flat-state top-level types: " + string.Join(", ", TopTypes(doc).ToArray()));
        }
        object folders = null; try { folders = fm.GetCornerManagementFolders(); } catch (Exception e) { sb.AppendLine("GetCornerManagementFolders threw " + e.Message); }
        var arr = folders as object[]; sb.AppendLine("corner-management folders: " + (arr == null ? "none" : arr.Length.ToString()));
        var found = new List<Feature>();
        if (arr != null)
            foreach (var o in arr)
            {
                var ff = o as Feature; if (ff == null) continue; sb.AppendLine("folder " + ff.Name + " [" + ff.GetTypeName2() + "]");
                var c = (Feature)ff.GetFirstSubFeature(); while (c != null) { sb.AppendLine("   " + c.Name + " [" + c.GetTypeName2() + "]"); found.Add(c); c = (Feature)c.GetNextSubFeature(); }
            }
        // also look at every top-level feature type name once, to learn the type strings
        var types = new SortedDictionary<string, int>(); var tf = (Feature)doc.FirstFeature(); while (tf != null) { var t = tf.GetTypeName2(); types[t] = types.ContainsKey(t) ? types[t] + 1 : 1; tf = (Feature)tf.GetNextFeature(); }
        sb.AppendLine("top-level feature types: " + string.Join(", ", types.Select(kv => kv.Key + "x" + kv.Value).ToArray()));

        foreach (var c in found)
        {
            object def = null; try { def = c.GetDefinition(); } catch { }
            if (def == null) { sb.AppendLine(c.Name + ": no definition"); continue; }
            Type iface = null;
            if (def is ISimpleCornerTreatmentFeatureData) iface = typeof(ISimpleCornerTreatmentFeatureData);
            else if (def is ITwoMemberCornerTreatmentFeatureData) iface = typeof(ITwoMemberCornerTreatmentFeatureData);
            else if (def is IComplexCornerTreatmentFeatureData) iface = typeof(IComplexCornerTreatmentFeatureData);
            var baseDef = def as ICornerTreatmentFeatureData;
            sb.AppendLine(c.Name + ": " + (iface == null ? "(not a corner-treatment definition)" : iface.Name) + (baseDef != null ? "  cornerType " + baseDef.CornerType + " ignore=" + baseDef.IgnoreCornerTreatment + " allowExt=" + baseDef.AllowExtension : ""));
            if (iface != null) sb.Append(Dump(def, iface));
            if (arg == "write" && iface != null)
            {
                try
                {
                    def.GetType().InvokeMember("AccessSelections", BindingFlags.InvokeMethod, null, def, new object[] { doc, null });
                    if (def is ISimpleCornerTreatmentFeatureData) { var d = (ISimpleCornerTreatmentFeatureData)def; d.CornerTreatmentTrimType = (int)swCornerTreatmentTrimType_e.swCornerTreatmentTrim_MiterTrim; d.WeldTrimGap = 0.0000762; }
                    else if (def is ITwoMemberCornerTreatmentFeatureData) { var d = (ITwoMemberCornerTreatmentFeatureData)def; d.CornerTreatmentTrimType = (int)swCornerTreatmentTrimType_e.swCornerTreatmentTrim_MiterTrim; d.WeldTrimGap = 0.0000762; }
                    bool ok = c.ModifyDefinition(def, doc, null); bool w; int err = 0; try { err = c.GetErrorCode2(out w); } catch { }
                    sb.AppendLine("   write miter trim + 0.0762 mm weld gap -> " + ok + (err != 0 ? "  ERROR " + err : "  ok"));
                    var back = c.GetDefinition(); sb.Append(Dump(back, iface));
                }
                catch (Exception e) { sb.AppendLine("   write failed: " + e.Message); }
            }
        }
        if (flat != null && wasSuppressed) { flat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); doc.EditRebuild3(); sb.AppendLine("flat pattern suppressed again"); }
        return sb.ToString();
    }
}
