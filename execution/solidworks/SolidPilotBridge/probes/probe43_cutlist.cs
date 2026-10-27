// Probe 43: the cut-list item of a sheet-metal part carries sheet-metal custom properties (Sheet Metal Thickness, Bend Radius, K-Factor, Bounding Box Length/Width/Thickness, Rolled Out Area, ...).
// What does the API give, and do they follow the flat pattern WITHOUT unsuppressing it?   Reads the cut-list folders of the active part and dumps every property (name = value | resolved value).
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder();
    static void L(string s) { sb.Append(s).Append(" ;; "); }

    static void Dump(Feature f, string label)
    {
        var cpm = f.CustomPropertyManager; if (cpm == null) { L(label + ": no CustomPropertyManager"); return; }
        var names = cpm.GetNames() as string[]; L(label + ": " + (names == null ? 0 : names.Length) + " properties");
        if (names == null) return;
        foreach (var n in names)
        {
            string val, resolved; bool was; cpm.Get6(n, false, out val, out resolved, out was, out was);
            L("   " + n + " = '" + val + "' | '" + resolved + "'");
        }
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder();
        var doc = (ModelDoc2)sw.ActiveDoc; var f = (Feature)doc.FirstFeature(); int n = 0;
        while (f != null)
        {
            string t = f.GetTypeName2();
            if (t == "CutListFolder") { Dump(f, f.Name + " [" + t + "]"); n++; }
            var s = (Feature)f.GetFirstSubFeature();
            while (s != null) { if (s.GetTypeName2() == "CutListFolder") { Dump(s, s.Name + " [" + s.GetTypeName2() + "] (sub of " + f.Name + ")"); n++; } s = (Feature)s.GetNextSubFeature(); }
            f = (Feature)f.GetNextFeature();
        }
        if (n == 0) L("no cut-list folder found");
        var cpmFile = doc.Extension.get_CustomPropertyManager(""); var fn = cpmFile.GetNames() as string[]; L("file-level custom properties: " + (fn == null ? 0 : fn.Length));
        return sb.ToString();
    }
}
