// Probe 74: the swFeatureNameID_e members (CreateDefinition ids) whose names contain any of the given fragments, with what IFeatureManager.CreateDefinition(id) answers (the *FeatureData interface it returns, or null).
// arg = "Jog,Gusset,Slot,Swept,Loft,Cross,Rip,Move,Corner"        (the definitions are only created, never turned into features)
using System;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var frags = (arg ?? "Jog").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); var sb = new StringBuilder();
        var doc = (ModelDoc2)sw.ActiveDoc; var fm = doc == null ? null : doc.FeatureManager;
        foreach (var name in Enum.GetNames(typeof(swFeatureNameID_e)))
        {
            if (!frags.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            int id = (int)Enum.Parse(typeof(swFeatureNameID_e), name); string what = "(no document)";
            if (fm != null)
            {
                try
                {
                    object def = fm.CreateDefinition(id);
                    if (def == null) what = "null";
                    else
                    {
                        // the most specific FeatureData interface the definition answers to
                        Type best = null; int score = -1;
                        foreach (var it in typeof(Feature).Assembly.GetTypes()) { if (!it.IsInterface || it.Name.IndexOf("FeatureData", StringComparison.Ordinal) < 0 || !it.IsInstanceOfType(def)) continue; int sc = it.GetProperties().Length; if (sc > score) { best = it; score = sc; } }
                        what = best == null ? "COM object" : best.Name + " (" + string.Join(",", best.GetProperties().Select(p => p.Name).Take(14).ToArray()) + ")";
                    }
                }
                catch (Exception e) { what = "threw " + (e.InnerException != null ? e.InnerException.Message : e.Message); }
            }
            sb.Append(name + " = " + id + " -> " + what + " ;; ");
        }
        return sb.ToString();
    }
}
