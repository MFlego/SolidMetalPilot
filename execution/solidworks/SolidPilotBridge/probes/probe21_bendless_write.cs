// Probe 21: the bend-less Convert feature (created from the PropertyManager with only the fixed face) - can its bend list be WRITTEN through ModifyDefinition?
// Reads the definition, gathers the 12 ordered bend edges (back-plate perimeter, rim outer edges, rim inner edges) and the 4 rip sketches from the rolled-back surface body, assigns them as DispatchWrapper[],
// calls ModifyDefinition and reads back bend list, bend sub-features, feature error, solid faces and bbox.   arg = "[close]"  (close = close the scratch document unsaved at the end and reactivate the original)
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
    static bool Near(double a, double b) { return Math.Abs(a - b) < 0.02; }
    static Feature Convert(ModelDoc2 doc) { Feature cf = null; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.GetTypeName2() == "SolidToSheetMetal") cf = f; f = (Feature)f.GetNextFeature(); } return cf; }

    static string Result(ModelDoc2 doc, PartDoc part)
    {
        var cf = Convert(doc); if (cf == null) return "no Convert feature"; bool w; int ec = cf.GetErrorCode2(out w); string bends = "?";
        var d = (IConvertSolidFeatureData)cf.GetDefinition();
        try { if (d.AccessSelections(doc, null)) { var be = d.BendEdges as object[]; var rs = d.RipSketches as object[]; bends = (be == null ? "null" : be.Length.ToString()) + " bend edges, " + (rs == null ? "null" : rs.Length.ToString()) + " rip sketches"; d.ReleaseSelectionAccess(); } } catch (Exception e) { bends = "read failed " + Fail(e); }
        var bs = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false); string body = "no solid body";
        if (bs != null && bs.Length > 0) { var b = (Body2)bs[0]; var x = (double[])b.GetBodyBox(); body = "solid faces " + b.GetFaceCount() + " bbox " + ((x[3] - x[0]) * 1000).ToString("F2") + " x " + ((x[4] - x[1]) * 1000).ToString("F2") + " x " + ((x[5] - x[2]) * 1000).ToString("F2"); }
        int sub = 0; var s = (Feature)cf.GetFirstSubFeature(); while (s != null) { if (s.GetTypeName2() == "OneBend") sub++; s = (Feature)s.GetNextSubFeature(); }
        return cf.Name + ": " + bends + ", bend sub-features " + sub + ", featureError " + ec + ", " + body;
    }

    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; string scratchTitle = doc.GetTitle();
        try
        {
            sb.AppendLine("before: " + Result(doc, part));
            var cf = Convert(doc); if (cf == null) return sb + "no Convert feature";
            var d = (IConvertSolidFeatureData)cf.GetDefinition();
            sb.AppendLine("AccessSelections " + d.AccessSelections(doc, null));
            IFace2 ff = d.GetFixedFace() as IFace2; sb.AppendLine("fixed face " + (ff == null ? "null" : "present"));
            Body2 surf = ff == null ? null : (Body2)ff.GetBody(); string via = "GetFixedFace().GetBody()";
            if (surf == null) { via = "rolled-back sheet body"; foreach (Body2 b in (object[])part.GetBodies2(1, false)) { var bb = (double[])b.GetBodyBox(); if (bb[2] * 1000 > 0.01) surf = b; } }
            if (surf == null) { d.ReleaseSelectionAccess(); return sb + "no surface body"; }
            var s1 = new List<Edge>(); var s2 = new List<Edge>(); var s3 = new List<Edge>();
            foreach (Edge e in (object[])surf.GetEdges())
            {
                var m = Mid(e);
                if (Near(m[2], 1.524)) s1.Add(e);
                else if (Near(m[2], 81.026) && (Near(Math.Abs(m[0]), 150.876) || Near(Math.Abs(m[1]), 227.076))) s2.Add(e);
                else if (Near(m[2], 81.026) && (Near(Math.Abs(m[0]), 125.476) || Near(Math.Abs(m[1]), 208.026))) s3.Add(e);
            }
            sb.AppendLine("body via " + via + ": edge series " + s1.Count + "/" + s2.Count + "/" + s3.Count);
            var all = new List<Edge>(); all.AddRange(s1); all.AddRange(s2); all.AddRange(s3);
            var rips = new List<Feature>(); foreach (var n in new[] { "Sketch30", "Sketch31", "Sketch32", "Sketch33" }) { var rf = part.FeatureByName(n) as Feature; if (rf != null) rips.Add(rf); }
            sb.AppendLine("rip sketches found " + rips.Count);
            try { d.BendEdges = all.ConvertAll(e => (object)new DispatchWrapper(e)).ToArray(); sb.AppendLine("BendEdges assigned (" + all.Count + ")"); } catch (Exception e) { sb.AppendLine("BendEdges assign FAILED " + Fail(e)); }
            try { d.RipSketches = rips.ConvertAll(f => (object)new DispatchWrapper(f)).ToArray(); sb.AppendLine("RipSketches assigned (" + rips.Count + ")"); } catch (Exception e) { sb.AppendLine("RipSketches assign FAILED " + Fail(e)); }
            try { sb.AppendLine("ModifyDefinition -> " + cf.ModifyDefinition(d, doc, null)); } catch (Exception e) { sb.AppendLine("ModifyDefinition FAILED " + Fail(e)); }
            try { d.ReleaseSelectionAccess(); } catch { }
            doc.EditRebuild3();
            sb.AppendLine("after : " + Result(doc, part));
        }
        catch (Exception e) { sb.AppendLine("probe error " + Fail(e)); }
        finally
        {
            if (arg == "close")
            {
                string orig = null; foreach (string t in new[] { "enclosure_nema_cabinet_dfm_cr_steel_16ga_convert.SLDPRT" }) orig = t;
                sw.CloseDoc(scratchTitle); int e2 = 0; sw.ActivateDoc3(orig, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2);
                sb.AppendLine("scratch closed unsaved; active: " + ((ModelDoc2)sw.ActiveDoc).GetTitle());
            }
        }
        return sb.ToString();
    }
}
