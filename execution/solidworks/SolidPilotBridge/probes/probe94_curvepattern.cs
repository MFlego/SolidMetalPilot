// Probe 94 (round 7): can a CURVE-DRIVEN BODY PATTERN be created by the API (the stairs: the user's CrvPattern1 patterns ONE tread 12 times along 3DSketch1)? Round 5 crashed SolidWorks twice; this probe logs
// every step to a file BEFORE the risky call (arg log=<path>). Run ONLY on a scratch copy.
// arg = "log=C:\...\curvepat.log;step=read|create;ref=CrvPattern1;seed=<body name>;count=12;spacing=0.4500995677726981"
//   step=read   : reads the reference feature's definition (every property incl. the objects' types) and the bodies of the part.
//   step=create : suppresses the reference pattern, then creates a NEW curve pattern with the reference's curve, alignment, curve method and face normal on the named seed body.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static StringBuilder sb = new StringBuilder(); static string logPath;
    static void Add(string s) { sb.Append(s).Append(" ;; "); }
    static void L(string s) { if (logPath != null) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + System.Environment.NewLine); }
    static Dictionary<string, string> Parse(string a) { var d = new Dictionary<string, string>(); foreach (var kv in (a ?? "").Split(';')) { int i = kv.IndexOf('='); if (i > 0) d[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); } return d; }
    static string G(Dictionary<string, string> a, string k, string dflt) { return a.ContainsKey(k) ? a[k] : dflt; }

    public static string Run(SldWorks sw, string arg)
    {
        sb.Clear(); var a = Parse(arg); logPath = G(a, "log", null); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; var fm = doc.FeatureManager;
        string step = G(a, "step", "read"), refName = G(a, "ref", "CrvPattern1");
        Feature rf = null; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.Name == refName) rf = f; f = (Feature)f.GetNextFeature(); }
        if (rf == null) return "no feature " + refName;
        L("step " + step + " reference " + refName);
        var rd = rf.GetDefinition() as ICurveDrivenPatternFeatureData; if (rd == null) return "reference has no curve pattern definition";
        rd.AccessSelections(doc, null);
        Add("reference: BodyPattern " + rd.BodyPattern + " count " + rd.D1InstanceCount + " spacing " + rd.D1Spacing + " equal " + rd.D1IsEqualSpaced + " curveMethod " + rd.D1CurveMethod + " align " + rd.D1AlignmentMethod + " reverse " + rd.D1ReverseDirection);
        object dir = rd.D1Direction; Add("D1Direction type " + (dir == null ? "null" : dir.GetType().Name + " " + DescribeEnt(dir)));
        object fn = rd.D1FaceNormal; Add("D1FaceNormal type " + (fn == null ? "null" : fn.GetType().Name));
        var pb = rd.PatternBodyArray as object[]; Add("PatternBodyArray " + (pb == null ? "null" : pb.Length + " item(s): " + (pb.Length > 0 ? ((Body2)pb[0]).Name + " type " + ((Body2)pb[0]).GetType() : "")));
        var all = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[]; Add("bodies in the part (visible+hidden): " + (all == null ? 0 : all.Length));
        if (all != null) foreach (Body2 b in all) Add("  body " + b.Name + " type " + b.GetType());
        if (step == "read") { rd.ReleaseSelectionAccess(); return sb.ToString(); }

        // ---- create
        string seedName = G(a, "seed", ""); Body2 seed = null; if (all != null) foreach (Body2 b in all) if (b.Name == seedName) seed = b;
        if (seed == null) { rd.ReleaseSelectionAccess(); return sb.ToString() + "seed body '" + seedName + "' not found"; }
        L("suppress the reference pattern"); Add("suppress reference: " + rf.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null));
        L("CreateDefinition(swFmCurvePattern)");
        var nd = fm.CreateDefinition((int)swFeatureNameID_e.swFmCurvePattern) as ICurveDrivenPatternFeatureData;
        if (nd == null) { rd.ReleaseSelectionAccess(); return sb.ToString() + "CreateDefinition returned null"; }
        L("AccessSelections on the new definition");
        bool acc = nd.AccessSelections(doc, null); Add("AccessSelections " + acc);
        L("set properties");
        nd.BodyPattern = true; nd.D1InstanceCount = int.Parse(G(a, "count", "12")); nd.D1Spacing = double.Parse(G(a, "spacing", "0.4500995677726981"), CultureInfo.InvariantCulture); nd.D1IsEqualSpaced = false;
        nd.D1CurveMethod = rd.D1CurveMethod; nd.D1AlignmentMethod = rd.D1AlignmentMethod; nd.D1ReverseDirection = rd.D1ReverseDirection; nd.GeometryPattern = false;
        L("set D1Direction"); nd.D1Direction = dir;
        L("set D1FaceNormal"); if (fn != null) nd.D1FaceNormal = fn;
        L("set PatternBodyArray"); nd.PatternBodyArray = new object[] { seed };
        rd.ReleaseSelectionAccess();
        L("CreateFeature");
        var nf = fm.CreateFeature(nd) as Feature;
        L("CreateFeature returned " + (nf == null ? "null" : nf.Name));
        Add("CreateFeature -> " + (nf == null ? "null" : nf.Name));
        try { nd.ReleaseSelectionAccess(); } catch { }
        try { doc.EditRebuild3(); } catch { }
        var after = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[]; Add("bodies after: " + (after == null ? 0 : after.Length));
        return sb.ToString();
    }
    static string DescribeEnt(object o)
    {
        try { var ft = o as Feature; if (ft != null) return "feature " + ft.Name; var e = o as Edge; if (e != null) return "edge"; var c = o as Curve; if (c != null) return "curve"; return o.GetType().FullName; } catch { return "?"; }
    }
}

