// Probe 48: document length units. arg = "in[,decimals]" or "mm[,decimals]": sets the ACTIVE document's linear unit and decimal places and reads them back (IModelDocExtension.SetUserPreferenceInteger with
// swUnitsLinear / swUnitsLinearDecimalPlaces). No arg: only reports. The user's standing rule: 2 decimals for mm, 3 for inches.
using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Report(ModelDocExtension x)
    {
        int unit = x.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
        int dec = x.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
        return "linear unit " + unit + " (" + (swLengthUnit_e)unit + "), decimals " + dec;
    }

    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var x = doc.Extension; string before = Report(x);
        if (string.IsNullOrEmpty(arg)) return "now: " + before;
        var parts = arg.Split(','); int dec = parts.Length > 1 ? int.Parse(parts[1]) : (parts[0] == "in" ? 3 : 2);
        int unit = parts[0] == "in" ? (int)swLengthUnit_e.swINCHES : (int)swLengthUnit_e.swMM;
        bool a = x.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, unit);
        bool b = x.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, dec);
        doc.EditRebuild3();
        return "was: " + before + " | set unit " + a + ", decimals " + b + " | now: " + Report(x);
    }
}
