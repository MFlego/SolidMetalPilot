// Probe 86: FORMING TOOLS (louvers, lances, embosses, ribs, extruded flanges - the .sldftp files of the design library) through IFeatureManager.InsertFormToolFeature(Path, Flip, RotAngle, Config, OverrideDoc, ShowPunch,
// ShowProfile, ShowCenter, LinkToPart). A plate (the sm_fixture base flange, top face at z = 0) is open; the face is selected at a pick point (the placement). Every step is logged to the file BEFORE the call.
// arg = "key=value|..." (pipe separated: the paths hold ';' and spaces)  tool=<path of the .sldftp, full>  x,y,z=<pick point on the face (m)>  rot=<degrees>  flip=0|1  config=<name>  mark=<n>  log=<file>
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
    static List<Feature> Top(ModelDoc2 doc) { var l = new List<Feature>(); var f = (Feature)doc.FirstFeature(); while (f != null) { l.Add(f); f = (Feature)f.GetNextFeature(); } return l; }
    static string Size(ModelDoc2 doc)
    {
        var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox(); var mp = (double[])b.GetMassProperties(1.0);
        return ((x[3] - x[0]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " x " + ((x[4] - x[1]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " x " + ((x[5] - x[2]) * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm, " + b.GetFaceCount() + "f, vol " + (mp[3] * 1e9).ToString("F3", CultureInfo.InvariantCulture) + " mm3, cg (" + (mp[0] * 1000).ToString("F3", CultureInfo.InvariantCulture) + ", " + (mp[1] * 1000).ToString("F3", CultureInfo.InvariantCulture) + ", " + (mp[2] * 1000).ToString("F3", CultureInfo.InvariantCulture) + ") mm";
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : dflt; }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (arg ?? "").Split('|')) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null; if (logPath == null) return "log=<file> is required";
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager;
        try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
        string tool = a.ContainsKey("tool") ? a["tool"] : null; if (tool == null || !File.Exists(tool)) return "tool=<full path of a .sldftp> is required (" + tool + ")";
        double x = N(a, "x", 0.0), y = N(a, "y", 0.0), z = N(a, "z", 0.0), rot = N(a, "rot", 0.0) * Math.PI / 180; bool flip = N(a, "flip", 0) != 0; string cfg = a.ContainsKey("config") ? a["config"] : ""; int mark = (int)N(a, "mark", 0);
        L("part before: " + Size(doc));
        var before = new HashSet<string>(Top(doc).Select(ft => ft.Name)); doc.ClearSelection2(true);
        L("about to select the face at (" + x * 1000 + ", " + y * 1000 + ", " + z * 1000 + ") mm, mark " + mark); bool sel = doc.Extension.SelectByID2("", "FACE", x, y, z, false, mark, null, 0); L("   selected " + sel + ", count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));
        L("about to InsertFormToolFeature(" + tool + ", flip " + flip + ", rot " + (rot * 180 / Math.PI) + " deg, config '" + cfg + "')"); Feature f = null; string thrown = null;
        try { f = fm.InsertFormToolFeature(tool, flip, rot, cfg, false, false, false, false, false) as Feature; } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
        doc.ClearSelection2(true);
        L("InsertFormToolFeature returned " + (f == null ? "null" : f.Name + " [" + f.GetTypeName2() + "]") + (thrown != null ? "; THREW " + thrown : ""));
        var fresh = Top(doc).Where(t => !before.Contains(t.Name)).ToList(); L("new features: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(t => t.Name + "[" + t.GetTypeName2() + "]").ToArray())));
        L("part after: " + Size(doc));
        if (f != null)
        {
            bool w; L("error code " + f.GetErrorCode2(out w) + (w ? " (warning)" : ""));
            var d = f.GetDefinition() as ILibraryFormToolFeatureData;
            if (d != null) { bool acc = d.AccessSelections(doc, null); L("definition: path '" + d.FormToolPath + "', config '" + d.FormToolConfiguration + "', rotation " + (d.RotationAngle * 180 / Math.PI).ToString("F2", CultureInfo.InvariantCulture) + " deg, link " + d.LinkToFormTool); d.ReleaseSelectionAccess(); }
        }
        return sb.ToString();
    }
}
