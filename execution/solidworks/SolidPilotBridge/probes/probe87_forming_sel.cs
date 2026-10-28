// Probe 87: FORMING TOOLS, the selection route that does not depend on the view. Probe 86 selected the face with SelectByID2(x, y, z) (it picks THROUGH THE CURRENT VIEW). Here the face is found by GEOMETRY
// (the closest face to the point) and selected as an OBJECT: Select4(false, SelectData) where the SelectData carries the pick point (X, Y, Z - the placement of the tool) and the mark. The result is compared
// by the part's centre of mass with the SelectByID2 route of probe 86. Every step is logged to the file BEFORE the call.
// arg = "key=value|..."  tool=<full .sldftp path>  x,y,z=<point on the face (m)>  rot=<degrees>  flip=0|1  config=<name>  mark=<n>  sel=obj|objnopt|id  log=<file>
//   sel=obj     Select4 with SelectData.X/Y/Z = the point          sel=objnopt  Select4 with SelectData but no X/Y/Z          sel=id  SelectByID2 (probe 86)
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
        string sel = a.ContainsKey("sel") ? a["sel"] : "obj";
        L("part before: " + Size(doc));
        var before = new HashSet<string>(Top(doc).Select(ft => ft.Name)); doc.ClearSelection2(true);
        L("about to select (" + sel + ") the face at (" + x * 1000 + ", " + y * 1000 + ", " + z * 1000 + ") mm, mark " + mark); bool ok;
        if (sel == "id") ok = doc.Extension.SelectByID2("", "FACE", x, y, z, false, mark, null, 0);
        else
        {
            Face2 best = null; double bd = double.MaxValue;
            var bodies = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            foreach (var bo in bodies) foreach (var fo in (object[])((Body2)bo).GetFaces())
                {
                    var f = (Face2)fo; var q = f.GetClosestPointOn(x, y, z) as double[]; if (q == null) continue;
                    double d = Math.Sqrt((q[0] - x) * (q[0] - x) + (q[1] - y) * (q[1] - y) + (q[2] - z) * (q[2] - z)); if (d < bd) { bd = d; best = f; }
                }
            L("   closest face at distance " + (bd * 1000).ToString("F4", CultureInfo.InvariantCulture) + " mm");
            if (best == null || bd > 1e-4) return sb.ToString() + "no face near the point";
            var sd = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = mark;
            if (sel == "obj") { sd.X = x; sd.Y = y; sd.Z = z; }
            ok = ((Entity)best).Select4(false, sd);
        }
        L("   selected " + ok + ", count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));
        L("about to InsertFormToolFeature(" + tool + ", flip " + flip + ", rot " + (rot * 180 / Math.PI) + " deg, config '" + cfg + "')"); Feature fe = null; string thrown = null;
        try { fe = fm.InsertFormToolFeature(tool, flip, rot, cfg, false, false, false, false, false) as Feature; } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
        doc.ClearSelection2(true);
        L("InsertFormToolFeature returned " + (fe == null ? "null" : fe.Name + " [" + fe.GetTypeName2() + "]") + (thrown != null ? "; THREW " + thrown : ""));
        L("part after: " + Size(doc));
        if (fe != null)
        {
            bool w; L("error code " + fe.GetErrorCode2(out w) + (w ? " (warning)" : ""));
            var d = fe.GetDefinition() as ILibraryFormToolFeatureData;
            if (d != null)
            {
                bool acc = d.AccessSelections(doc, null);
                try
                {
                    L("definition: path '" + d.FormToolPath + "', config '" + d.FormToolConfiguration + "', rotation " + d.RotationAngle.ToString("F6", CultureInfo.InvariantCulture) + " rad, link " + d.LinkToFormTool + ", override " + d.OverrideSettings);
                    object pp = d.PickPoint; L("PickPoint: " + (pp == null ? "null" : pp.GetType().FullName));
                    var arr = pp as double[]; if (arr != null) L("   as double[]: " + string.Join(", ", arr.Select(v => (v * 1000).ToString("F4", CultureInfo.InvariantCulture)).ToArray()) + " mm");
                    var mp = pp as MathPoint; if (mp != null) { var ad = (double[])mp.ArrayData; L("   as MathPoint: " + string.Join(", ", ad.Select(v => (v * 1000).ToString("F4", CultureInfo.InvariantCulture)).ToArray()) + " mm"); }
                    object pf = d.PlacementFace; L("PlacementFace: " + (pf == null ? "null" : pf.GetType().FullName));
                    var face = pf as Face2; if (face != null) { var nrm = (double[])face.Normal; L("   face normal " + string.Join(", ", nrm.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)).ToArray()) + " area " + (face.GetArea() * 1e6).ToString("F3", CultureInfo.InvariantCulture) + " mm2"); }
                }
                finally { if (acc) d.ReleaseSelectionAccess(); }
            }
        }
        return sb.ToString();
    }
}
