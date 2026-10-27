// Probe 49: how PARAMETRIC is a part? Dumps the equations (global variables and linked dimensions), every display dimension of every feature (name = value in the document's unit) and the constrained
// status of each sketch (fully defined / under-defined ...).   Read-only; for comparing an API-built part with the user's reference.
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb;
    static void L(string s) { sb.Append(s).Append(" ;; "); }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var doc = (ModelDoc2)sw.ActiveDoc;
        int unit = doc.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
        double k = unit == (int)swLengthUnit_e.swINCHES ? 1 / 0.0254 : 1000; string u = unit == (int)swLengthUnit_e.swINCHES ? "in" : "mm";
        var eq = doc.GetEquationMgr(); int n = eq.GetCount(); L("equations: " + n + " (unit " + u + ")");
        for (int i = 0; i < n; i++) L("  [" + i + "] " + eq.get_Equation(i) + "   = " + eq.get_Value(i) + (eq.get_GlobalVariable(i) ? "  (global variable)" : ""));
        var f = (Feature)doc.FirstFeature();
        while (f != null)
        {
            string t = f.GetTypeName2(); bool interesting = t == "ProfileFeature" || t == "SheetMetal" || t == "SMBaseFlange" || t == "SMMiteredFlange" || t == "EdgeFlange" || t == "FlatPattern" || t == "SolidToSheetMetal";
            if (interesting)
            {
                var dims = new List<string>(); var dd = (DisplayDimension)f.GetFirstDisplayDimension();
                while (dd != null)
                {
                    var d = (Dimension)dd.GetDimension2(0); var v = d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[];
                    int dt = dd.Type2; double val = v != null && v.Length > 0 ? v[0] : double.NaN;
                    bool angular = dt == (int)swDimensionType_e.swAngularDimension; dims.Add(d.FullName + " = " + (angular ? (val * 180 / Math.PI).ToString("F3") + " deg" : (val * k).ToString("F4") + " " + u) + (d.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven ? " [driven]" : ""));
                    dd = (DisplayDimension)f.GetNextDisplayDimension(dd);
                }
                string status = "";
                if (t == "ProfileFeature") { var sk = f.GetSpecificFeature2() as Sketch; if (sk != null) { int cs = sk.GetConstrainedStatus(); status = "  constrained status " + cs + " (" + (swConstrainedStatus_e)cs + ")"; } }
                L(f.Name + " [" + t + "]" + status + (dims.Count > 0 ? "   dims: " + string.Join("; ", dims.ToArray()) : ""));
            }
            f = (Feature)f.GetNextFeature();
        }
        return sb.ToString();
    }
}
