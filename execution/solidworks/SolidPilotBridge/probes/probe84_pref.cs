// Probe 84: read (and, with set=<value>, write) ONE string user preference of SolidWorks by its swUserPreferenceStringValue_e number (default 61 = file location for sheet-metal gauge tables) - always read the
// value first and write it back when you are done: these are the user's settings. arg = "pref=61;set=<value>" ; prints the plain string value and the list value.
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (arg ?? "").Split(new[] { '|' })) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1); }
        int loc = a.ContainsKey("pref") ? int.Parse(a["pref"]) : 61; var sb = new StringBuilder();
        string plain = null, list = null; try { plain = sw.GetUserPreferenceStringValue(loc); } catch (Exception e) { sb.Append("plain threw " + e.Message + " ;; "); } try { list = sw.GetUserPreferenceStringListValue(loc); } catch (Exception e) { sb.Append("list threw " + e.Message + " ;; "); }
        sb.Append("pref " + loc + " plain='" + plain + "' ;; list='" + list + "' ;; ");
        if (a.ContainsKey("set"))
        {
            bool ok = sw.SetUserPreferenceStringValue(loc, a["set"]); sb.Append("SetUserPreferenceStringValue -> " + ok + " ;; now plain='" + sw.GetUserPreferenceStringValue(loc) + "' ;; list='" + sw.GetUserPreferenceStringListValue(loc) + "' ;; ");
        }
        return sb.ToString();
    }
}
