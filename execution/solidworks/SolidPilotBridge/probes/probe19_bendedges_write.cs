// go/no-go probe 19: can the Convert-to-Sheet-Metal bend list be WRITTEN through ModifyDefinition with FRESH edge objects (taken from the rolled-back surface body, not the feature's own references)?
// This is the "edge selection through the API" question: if yes, the Convert PropertyManager picks could be replaced by a typed write. Run in-process (bridge run_cs) and out-of-process (oop_host.ps1) and compare.
// Works on a scratch COPY of the cabinet part (post-Convert, 12 bends / 4 rips), opened silent and closed unsaved.  arg = path of the scratch copy
// Cases: A same order / B reversed order / C first 4 edges only / D full list restored.   After every case: ModifyDefinition result or exception, bend count read back, feature error, solid faces, bbox.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Fail(Exception e) { var ce = e as COMException; return e.GetType().Name + (ce != null ? " 0x" + ((uint)ce.ErrorCode).ToString("X8") : "") + ": " + e.Message; }
    static double[] Mid(Edge e) { var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per); var p = (double[])c.Evaluate2((s + en) / 2, 0); return new[] { p[0] * 1000, p[1] * 1000, p[2] * 1000 }; }
    static string Fmt(double[] m) { return "(" + m[0].ToString("F1") + "," + m[1].ToString("F1") + "," + m[2].ToString("F1") + ")"; }
    static double Dist(double[] a, double[] b) { return Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]); }

    static Feature Convert(ModelDoc2 doc) { Feature cf = null; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.GetTypeName2() == "SolidToSheetMetal") cf = f; f = (Feature)f.GetNextFeature(); } return cf; }

    static string Result(ModelDoc2 doc, PartDoc part)
    {
        var cf = Convert(doc); bool w; int ec = cf.GetErrorCode2(out w); string bends = "?";
        var d = (IConvertSolidFeatureData)cf.GetDefinition();
        try { if (d.AccessSelections(doc, null)) { var be = d.BendEdges as object[]; bends = be == null ? "null" : be.Length + " [first " + Fmt(Mid((Edge)be[0])) + " last " + Fmt(Mid((Edge)be[be.Length - 1])) + "]"; d.ReleaseSelectionAccess(); } } catch (Exception e) { bends = "read failed " + Fail(e); }
        var bs = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false); string body = "no solid body";
        if (bs != null && bs.Length > 0) { var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox(); body = "solid faces " + b.GetFaceCount() + " bbox " + ((x[3] - x[0]) * 1000).ToString("F2") + " x " + ((x[4] - x[1]) * 1000).ToString("F2") + " x " + ((x[5] - x[2]) * 1000).ToString("F2"); }
        int sub = 0; var s = (Feature)cf.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") sub++; s = (Feature)s.GetNextSubFeature(); }
        return "bendList " + bends + ", bend sub-features " + sub + ", featureError " + ec + ", " + body;
    }

    static string Case(ModelDoc2 doc, PartDoc part, string name, Func<List<Edge>, List<Edge>> pick)
    {
        var cf = Convert(doc); var d = (IConvertSolidFeatureData)cf.GetDefinition(); var sb = new StringBuilder(name + ": ");
        try
        {
            if (!d.AccessSelections(doc, null)) return name + ": AccessSelections false";
            var own = (object[])d.BendEdges; var rips = (object[])d.RipSketches;
            // fresh edge objects: the offset SURFACE body (sheet body whose box starts above z = 0) of the rolled-back model, matched to the feature's edges by midpoint
            Body2 surf = null; foreach (Body2 b in (object[])part.GetBodies2(1, false)) { var bb = (double[])b.GetBodyBox(); if (bb[2] * 1000 > 0.01) surf = b; }
            if (surf == null) { d.ReleaseSelectionAccess(); return name + ": offset surface body not found"; }
            var fresh = new List<Edge>(); var all = (object[])surf.GetEdges();
            foreach (var o in own) { var m = Mid((Edge)o); Edge hit = null; foreach (Edge e in all) if (Dist(Mid(e), m) < 0.05) { hit = e; break; } if (hit != null) fresh.Add(hit); }
            sb.Append("fresh edges matched " + fresh.Count + "/" + own.Length + "; ");
            var chosen = pick(fresh);
            d.BendEdges = chosen.ConvertAll(e => (object)new DispatchWrapper(e)).ToArray();
            d.RipSketches = Array.ConvertAll(rips, o => (object)new DispatchWrapper(o));
            sb.Append("assigned " + chosen.Count + " (first " + Fmt(Mid(chosen[0])) + ", last " + Fmt(Mid(chosen[chosen.Count - 1])) + "); ");
            try { sb.Append("ModifyDefinition -> " + cf.ModifyDefinition(d, doc, null) + "; "); }
            catch (Exception e) { sb.Append("ModifyDefinition FAILED " + Fail(e) + "; "); }
            try { d.ReleaseSelectionAccess(); } catch { }
        }
        catch (Exception e) { sb.Append("case error " + Fail(e) + "; "); }
        doc.EditRebuild3();
        try { sb.Append(Result(doc, part)); } catch (Exception e) { sb.Append("result read failed " + Fail(e)); }
        return sb.ToString();
    }

    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); var original = sw.ActiveDoc as ModelDoc2; string originalTitle = original == null ? null : original.GetTitle();
        int err = 0, warn = 0;
        var doc = sw.OpenDoc6(arg, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        if (doc == null) return "open failed err=" + err + " warn=" + warn;
        try
        {
            var part = (PartDoc)doc;
            sb.AppendLine("start : " + Result(doc, part));
            sb.AppendLine(Case(doc, part, "A same order ", l => l));
            sb.AppendLine(Case(doc, part, "B reversed   ", l => { var r = new List<Edge>(l); r.Reverse(); return r; }));
            sb.AppendLine(Case(doc, part, "C first 4 only", l => l.GetRange(0, 4)));
            sb.AppendLine(Case(doc, part, "D full again  ", l => l));
        }
        finally
        {
            sw.CloseDoc(doc.GetTitle());
            if (originalTitle != null) { int e2 = 0; sw.ActivateDoc3(originalTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2); }
        }
        return sb.ToString();
    }
}
