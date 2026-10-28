// Probe 88: CORNER RELIEF (swFmCornerRelief = 120) - ICornerReliefFeatureData / ISMCornerReliefData, and the older route IFeatureManager.AddCornerReliefCorner / AddCornerReliefType / FinishCornerRelief.
// A sheet-metal part with corners where flanges meet is open (probes\sm_fixture.ps1 -Flanges 4 = a tray with four 25 mm walls). Every step is logged to the file BEFORE the call (the reply dies with the process).
// arg = "key=value|..."  mode=survey|create|old  cornertype=0|1 (0 two-bend, 1 three-bend)  relief=<swCornerReliefType_e 0..9>  scope=0|1 (SetBodyScope with the body)  log=<file>
//   survey : CreateDefinition(120), Initialize(cornertype), CollectAllCorners(relief) for every relief type, the corner data read - NOTHING is created
//   create : survey + set the relief type on the corners collected and CreateFeature (stops after the first success, measures the part before / after)
//   old    : AddCornerReliefCorner() + AddCornerReliefType(index, ...) + FinishCornerRelief() with the faces selected (marks 1 .. 3 are tried)
//   sweep  : (probe 88 finding: SetBodyScope(the body) is REQUIRED - without it every CollectAllCorners answers 2 = InvalidBody; with it a four-wall tray gives 4 two-bend corners) a fresh definition per try:
//            relief type x dimensions x options set on every collected corner, CreateFeature; STOPS at the first feature (a second creation right after a success is how SolidWorks has died before)
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
    static string F(double v) { return (v * 1000).ToString("0.###", CultureInfo.InvariantCulture); }
    static List<Feature> Top(ModelDoc2 doc) { var l = new List<Feature>(); var f = (Feature)doc.FirstFeature(); while (f != null) { l.Add(f); f = (Feature)f.GetNextFeature(); } return l; }
    static string State(ModelDoc2 doc)
    {
        var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; if (bs == null || bs.Length == 0) return "no body";
        var b = (Body2)bs[0]; var mp = (double[])b.GetMassProperties(1.0); return b.GetFaceCount() + "f, " + b.GetEdgeCount() + "e, vol " + (mp[3] * 1e9).ToString("F3", CultureInfo.InvariantCulture) + " mm3";
    }
    static string Describe(object c)
    {
        var d = c as ISMCornerReliefData; if (d == null) return c == null ? "null" : "not an ISMCornerReliefData: " + c.GetType().FullName;
        var sbb = new StringBuilder("corner " + d.CornerIndex + ": relief " + d.ReliefType + ", slot " + F(d.SlotLength) + " x " + F(d.SlotWidth) + " mm, centre-on-bend " + d.CenterOnBendLines + ", ratio " + d.RatioToThickness + ", tangent " + d.TangentToBend + ", fillet " + d.AddFilletedCorners + " (" + F(d.CornerFilletDiameter) + " mm), suitcase " + d.SuitCaseType);
        try { object f1, f2, f3; d.GetFaces(out f1, out f2, out f3); sbb.Append(", faces " + (f1 != null) + "/" + (f2 != null) + "/" + (f3 != null)); } catch (Exception e) { sbb.Append(", GetFaces threw " + e.Message); }
        return sbb.ToString();
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (arg ?? "").Split('|')) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null; if (logPath == null) return "log=<file> is required";
        string mode = a.ContainsKey("mode") ? a["mode"] : "survey"; int ct = a.ContainsKey("cornertype") ? int.Parse(a["cornertype"]) : 0; int relief = a.ContainsKey("relief") ? int.Parse(a["relief"]) : 0; bool scope = a.ContainsKey("scope") && a["scope"] == "1";
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document"; var fm = doc.FeatureManager;
        try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
        L("part: " + State(doc)); L("tree: " + string.Join("; ", Top(doc).Select(f => f.Name + "[" + f.GetTypeName2() + "]").ToArray()));
        var body = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];

        if (mode == "one")           // ONE creation with exactly the given settings: relief=<type> slotlen= slotwidth= fillet= (mm) ratio=1 tangent=1 centre=1 filleted=1 suitcase=<n> remove=<corner indices, comma>
        {
            Func<string, double> mm = key => a.ContainsKey(key) ? double.Parse(a[key], CultureInfo.InvariantCulture) / 1000.0 : -1.0;
            string v0 = State(doc); var names0 = new HashSet<string>(Top(doc).Select(f => f.Name));
            var d = fm.CreateDefinition((int)swFeatureNameID_e.swFmCornerRelief) as ICornerReliefFeatureData; if (d == null) { L("CreateDefinition returned null"); return sb.ToString(); }
            if (body != null && body.Length > 0) d.SetBodyScope(body[0]);
            bool init = d.Initialize(ct); object cs = null; int n = d.CollectAllCorners(relief, out cs); var arr = cs as object[];
            L("collected " + (arr == null ? 0 : arr.Length) + " corners for relief " + relief + " (error " + n + ", Initialize " + init + ")");
            // GUARD (two SolidWorks deaths, 2026-10-27): never touch the corners unless CollectAllCorners answered 0 - relief 2 on two-bend corners answered error 4 WITH an array, and setting its ReliefType killed SolidWorks
            if (arr == null || n != 0) { L("refusing to touch the corners: CollectAllCorners answered error " + n); return sb.ToString(); }
            if (ct == 0 && !(relief == 0 || relief == 1 || relief == 3 || relief == 5 || relief == 9) && !a.ContainsKey("unsafe")) { L("refusing: relief " + relief + " is not one of the types that have built on two-bend corners (0, 1, 3, 9; 5 is untried)"); return sb.ToString(); }
            if (a.ContainsKey("remove"))
                foreach (int ri in a["remove"].Split(',').Select(int.Parse).OrderByDescending(x => x)) { bool rm = d.RemoveCorner(ri); L("RemoveCorner(" + ri + ") -> " + rm + ", CornerCount now " + d.CornerCount); }
            // corner indices are 1-BASED (the collected corners print as 1 .. n; index 0 -> error 5). After RemoveCorner the objects of the REMOVED corners are dangling: touching them killed SolidWorks (2026-10-27 23:18) -
            // so a subset is read back ONLY through GetCornerAtIndex, never from the array that was collected before the removal.
            var left = new List<ISMCornerReliefData>(); int total = arr.Length;
            if (a.ContainsKey("remove")) { for (int ci = 1; ci <= total; ci++) { object one; int e = d.GetCornerAtIndex(ci, out one); var sd = one as ISMCornerReliefData; if (e == 0 && sd != null) left.Add(sd); else L("GetCornerAtIndex(" + ci + ") -> error " + e); } }
            else foreach (var co in arr) { var sd = co as ISMCornerReliefData; if (sd != null) left.Add(sd); }
            foreach (var sd in left)
            {
                try
                {
                    sd.ReliefType = relief; if (mm("slotlen") > 0) sd.SlotLength = mm("slotlen"); if (mm("slotwidth") > 0) sd.SlotWidth = mm("slotwidth"); if (mm("fillet") > 0) sd.CornerFilletDiameter = mm("fillet");
                    if (a.ContainsKey("ratio")) sd.RatioToThickness = a["ratio"] == "1"; if (a.ContainsKey("tangent")) sd.TangentToBend = a["tangent"] == "1"; if (a.ContainsKey("centre")) sd.CenterOnBendLines = a["centre"] == "1";
                    if (a.ContainsKey("filleted")) sd.AddFilletedCorners = a["filleted"] == "1"; if (a.ContainsKey("suitcase")) sd.SuitCaseType = int.Parse(a["suitcase"]);
                }
                catch (Exception e) { L("   set threw " + (e.InnerException != null ? e.InnerException : e).Message); }
            }
            foreach (var sd in left) L("   " + Describe(sd));
            L("about to CreateFeature on " + left.Count + " corners"); Feature feat = null;
            try { feat = fm.CreateFeature(d) as Feature; } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
            L("   -> " + (feat == null ? "null" : feat.Name + " [" + feat.GetTypeName2() + "]"));
            if (feat != null)
            {
                bool w; L("rebuild error code " + feat.GetErrorCode2(out w) + (w ? " (warning)" : "")); L("part before " + v0 + " / after " + State(doc));
                var back = feat.GetDefinition() as ICornerReliefFeatureData;
                if (back != null) { bool acc = false; try { acc = back.AccessSelections(doc, null); L("created definition: CornerCount " + back.CornerCount); var gc = back.GetCorners(relief) as object[]; if (gc != null) foreach (var g in gc) L("   " + Describe(g)); } catch (Exception e) { L("   readback threw " + e.Message); } finally { if (acc) { try { back.ReleaseSelectionAccess(); } catch (Exception) { } } } }
            }
            return sb.ToString();
        }

        if (mode == "sweep")
        {
            int[] types = a.ContainsKey("types") ? a["types"].Split(',').Select(int.Parse).ToArray() : new[] { 0, 1, 3, 5, 9 };
            double[][] dims = { new[] { -1.0, -1.0, -1.0 }, new[] { 0.003, 0.003, 0.003 }, new[] { 0.003, 0.0015, 0.003 }, new[] { 0.004, 0.002, 0.004 }, new[] { 0.0015, 0.0015, 0.0015 }, new[] { 0.006, 0.006, 0.006 } };
            string v0 = State(doc); var names0 = new HashSet<string>(Top(doc).Select(f => f.Name)); bool built = false;
            foreach (int rt in types)
            {
                foreach (var dm in dims)
                {
                    for (int flags = 0; flags < 4 && !built; flags++)         // 0 none, 1 ratio to thickness, 2 tangent to bend, 3 centre on bend lines
                    {
                        var d = fm.CreateDefinition((int)swFeatureNameID_e.swFmCornerRelief) as ICornerReliefFeatureData; if (d == null) { L("CreateDefinition returned null"); return sb.ToString(); }
                        if (body != null && body.Length > 0) d.SetBodyScope(body[0]);
                        bool init = d.Initialize(ct); object cs = null; int n = d.CollectAllCorners(rt, out cs); var arr = cs as object[];
                        string tag = "type " + rt + ", dims " + (dm[0] < 0 ? "as collected" : F(dm[0]) + " x " + F(dm[1]) + " (fillet " + F(dm[2]) + ")") + ", flags " + flags;
                        if (arr == null || arr.Length == 0) { L(tag + ": " + (init ? "" : "Initialize false, ") + "no corners (error " + n + ")"); continue; }
                        foreach (var co in arr)
                        {
                            var sd = co as ISMCornerReliefData; if (sd == null) continue;
                            try
                            {
                                sd.ReliefType = rt; if (dm[0] > 0) { sd.SlotLength = dm[0]; sd.SlotWidth = dm[1]; sd.CornerFilletDiameter = dm[2]; }
                                if (flags == 1) sd.RatioToThickness = true; if (flags == 2) sd.TangentToBend = true; if (flags == 3) sd.CenterOnBendLines = true;
                            }
                            catch (Exception e) { L("   set threw " + (e.InnerException != null ? e.InnerException : e).Message); }
                        }
                        L("about to CreateFeature: " + tag + " on " + arr.Length + " corners"); Feature feat = null;
                        try { feat = fm.CreateFeature(d) as Feature; } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
                        L("   -> " + (feat == null ? "null" : feat.Name + " [" + feat.GetTypeName2() + "]"));
                        if (feat != null)
                        {
                            built = true; L("part before " + v0 + " / after " + State(doc));
                            var fresh = Top(doc).Where(f => !names0.Contains(f.Name)).Select(f => f.Name + "[" + f.GetTypeName2() + "]").ToArray(); L("new features: " + string.Join(", ", fresh));
                            bool w; L("rebuild error code " + feat.GetErrorCode2(out w) + (w ? " (warning)" : ""));
                            var back = feat.GetDefinition() as ICornerReliefFeatureData;
                            if (back != null) { bool acc = false; try { acc = back.AccessSelections(doc, null); L("created definition: CornerCount " + back.CornerCount + ", CornerType " + back.CornerType); var gc = back.GetCorners(rt) as object[]; if (gc != null) foreach (var g in gc) L("   " + Describe(g)); } catch (Exception e) { L("   readback threw " + e.Message); } finally { if (acc) { try { back.ReleaseSelectionAccess(); } catch (Exception) { } } } }
                        }
                        if (built) break;
                    }
                    if (built) break;
                }
                if (built) break;
            }
            if (!built) L("no combination built a feature");
            return sb.ToString();
        }

        if (mode == "survey" || mode == "create")
        {
            L("about to CreateDefinition(120)"); var def = fm.CreateDefinition((int)swFeatureNameID_e.swFmCornerRelief) as ICornerReliefFeatureData; L("   -> " + (def == null ? "null" : "ICornerReliefFeatureData"));
            if (def == null) return sb.ToString();
            if (scope && body != null && body.Length > 0) { L("about to SetBodyScope(body)"); int se = def.SetBodyScope(body[0]); L("   -> " + se); }
            L("about to Initialize(" + ct + ")"); bool init = def.Initialize(ct); L("   -> " + init + ", CornerType " + def.CornerType + ", CornerCount " + def.CornerCount);
            var gathered = new Dictionary<int, object[]>();
            for (int rt = 0; rt <= 9; rt++)
            {
                L("about to CollectAllCorners(" + rt + ")"); object cs = null; int n; try { n = def.CollectAllCorners(rt, out cs); } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); continue; }
                var arr = cs as object[]; L("   -> " + n + " (error code), array " + (arr == null ? "null" : arr.Length.ToString()) + ", CornerCount now " + def.CornerCount);
                if (arr != null && arr.Length > 0) { gathered[rt] = arr; foreach (var c in arr) L("      " + Describe(c)); }
            }
            L("about to GetCorners for every relief type");
            for (int rt = 0; rt <= 9; rt++) { try { var g = def.GetCorners(rt) as object[]; if (g != null && g.Length > 0) { L("   GetCorners(" + rt + ") -> " + g.Length); foreach (var c in g) L("      " + Describe(c)); } } catch (Exception e) { L("   GetCorners(" + rt + ") threw " + e.Message); } }
            if (mode == "create")
            {
                string v0 = State(doc); var names0 = new HashSet<string>(Top(doc).Select(f => f.Name));
                foreach (var kv in gathered)
                {
                    foreach (var c in kv.Value) { var d = c as ISMCornerReliefData; if (d != null) { try { d.ReliefType = relief; } catch (Exception e) { L("   set ReliefType threw " + e.Message); } } }
                    L("about to CreateFeature (corners collected with relief type " + kv.Key + ", then set to " + relief + ")"); Feature feat = null; try { feat = fm.CreateFeature(def) as Feature; } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
                    L("   -> " + (feat == null ? "null" : feat.Name + " [" + feat.GetTypeName2() + "]")); L("part before " + v0 + " / after " + State(doc));
                    var fresh = Top(doc).Where(f => !names0.Contains(f.Name)).Select(f => f.Name + "[" + f.GetTypeName2() + "]").ToArray(); L("new features: " + (fresh.Length == 0 ? "none" : string.Join(", ", fresh)));
                    break;       // ONE creation per run
                }
            }
            return sb.ToString();
        }

        if (mode == "old")
        {
            L("old route: AddCornerReliefCorner()"); int idx = -1; try { idx = fm.AddCornerReliefCorner(); } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
            L("   -> index " + idx);
            if (idx >= 0)
            {
                L("about to AddCornerReliefType(" + idx + ", " + relief + ", 2 mm, 2 mm, 0, ...)"); bool ok = false; try { ok = fm.AddCornerReliefType(idx, relief, 0.002, 0.002, 0.0, false, false, false, false, false); } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
                L("   -> " + ok); string v0 = State(doc);
                L("about to FinishCornerRelief()"); Feature feat = null; try { feat = fm.FinishCornerRelief() as Feature; } catch (Exception e) { L("   threw " + (e.InnerException != null ? e.InnerException : e).Message); }
                L("   -> " + (feat == null ? "null" : feat.Name + " [" + feat.GetTypeName2() + "]")); L("part before " + v0 + " / after " + State(doc));
            }
            return sb.ToString();
        }
        return "mode must be survey, create or old";
    }
}
