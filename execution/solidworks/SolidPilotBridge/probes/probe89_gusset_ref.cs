// Probe 89: WHERE does SolidWorks put the gusset's reference point? After a first gusset has split the bend edge, the START vertex the next gusset's offset is measured from is not the bend's first vertex
// any more (it was -25 mm after a gusset at -30 on a bend from -50 to +50 mm; with the two faces in the other order it was the OPPOSITE end, +50 mm). Can the reference point be READ from a NEW definition
// before CreateFeature (the tool could then convert a position along the bend into the right offset)? Every step is logged to the file BEFORE the call (the reply dies with the process).
// arg = "key=value|..."  log=<file>  swap=0|1 (the two faces in the other order)  p1=x,y,z  p2=x,y,z (points ON the faces, meters)  mode=read|create  offset=<m>
//   read   : CreateDefinition(swFmSMGusset), set the faces, READ ReferencePoint / OffsetDistance - nothing is created
//   create : the same, then set the dimensions and CreateFeature, then read the reference point back from the created feature
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder(); static string logPath = null;
    static void L(string s) { sb.Append(s).Append(" ;; "); if (logPath != null) { try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); } catch { } } }
    static double[] P(string s) { return s.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray(); }
    static string F(double[] p) { return p == null ? "null" : "(" + string.Join(", ", p.Select(v => (v * 1000).ToString("0.###", CultureInfo.InvariantCulture)).ToArray()) + ") mm"; }

    static Face2 FaceAt(ModelDoc2 doc, double[] pt)
    {
        Face2 best = null; double bd = double.MaxValue;
        var bodies = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        foreach (var bo in bodies) foreach (var fo in (object[])((Body2)bo).GetFaces())
            {
                var f = (Face2)fo; var q = f.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; if (q == null) continue;
                double d = Math.Sqrt((q[0] - pt[0]) * (q[0] - pt[0]) + (q[1] - pt[1]) * (q[1] - pt[1]) + (q[2] - pt[2]) * (q[2] - pt[2])); if (d < bd) { bd = d; best = f; }
            }
        return bd < 1e-4 ? best : null;
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (arg ?? "").Split('|')) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null; if (logPath == null) return "log=<file> is required";
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager;
        string mode = a.ContainsKey("mode") ? a["mode"] : "read"; bool swap = a.ContainsKey("swap") && a["swap"] == "1"; double off = a.ContainsKey("offset") ? double.Parse(a["offset"], CultureInfo.InvariantCulture) : 0.006;
        var p1 = P(a.ContainsKey("p1") ? a["p1"] : "0,0,0"); var p2 = P(a.ContainsKey("p2") ? a["p2"] : "0,0.0285,0.012");
        var f1 = FaceAt(doc, swap ? p2 : p1); var f2 = FaceAt(doc, swap ? p1 : p2);
        L("face 1 " + (f1 == null ? "NOT FOUND" : "found, normal " + string.Join(",", ((double[])f1.Normal).Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)).ToArray())) + "; face 2 " + (f2 == null ? "NOT FOUND" : "found, normal " + string.Join(",", ((double[])f2.Normal).Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)).ToArray())));
        if (f1 == null || f2 == null) return sb.ToString();
        L("about to CreateDefinition(swFmSMGusset)"); var def = fm.CreateDefinition((int)swFeatureNameID_e.swFmSMGusset) as ISMGussetFeatureData; L("   -> " + (def == null ? "null" : "ok"));
        if (def == null) return sb.ToString();
        L("about to set SupportingFaces (swap " + swap + ")"); def.SupportingFaces = new[] { new DispatchWrapper(f1), new DispatchWrapper(f2) }; L("   done");
        L("about to READ ReferencePoint on the NEW definition"); object rp = null; try { rp = def.ReferencePoint; } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
        L("   -> " + (rp == null ? "null" : rp.GetType().FullName));
        var vtx = rp as IVertex; if (vtx != null) { try { L("   as vertex at " + F(vtx.GetPoint() as double[])); } catch (Exception e) { L("   GetPoint threw " + e.Message); } }
        L("OffsetDistance now " + def.OffsetDistance.ToString("0.#####", CultureInfo.InvariantCulture) + ", UseOffset " + def.UseOffset);
        if (mode == "create")
        {
            def.GussetType = (int)swSheetMetalRibGussetType_e.swSheetMetalRibGussetType_Flat; def.ProfileType = (int)swSheetMetalGussetProfileType_e.swSheetMetalGussetProfileType_Rib; def.GussetThickness = 0.0015;
            def.UseOffset = true; def.OffsetDistance = off; def.IndentDepth = 0.004; def.IndentWidth = 0.006; def.ProfileDimensionScheme = (int)swSheetMetalGussetProfileDimType_e.swSheetMetalGussetProfileDimType_IndentDepth;
            def.SupportingFaces = new[] { new DispatchWrapper(f1), new DispatchWrapper(f2) };
            doc.ClearSelection2(true);
            L("about to CreateFeature (offset " + off * 1000 + " mm)"); Feature feat = null; try { feat = fm.CreateFeature(def) as Feature; } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
            L("   -> " + (feat == null ? "null" : feat.Name));
            if (feat != null)
            {
                var d2 = feat.GetDefinition() as ISMGussetFeatureData; bool acc = false; try { acc = d2.AccessSelections(doc, null); } catch (Exception) { }
                try { var r2 = d2.ReferencePoint as IVertex; L("created feature's reference point " + (r2 == null ? "null" : F(r2.GetPoint() as double[])) + ", offset " + (d2.OffsetDistance * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm"); }
                finally { if (acc) { try { d2.ReleaseSelectionAccess(); } catch (Exception) { } } }
            }
        }
        return sb.ToString();
    }
}
