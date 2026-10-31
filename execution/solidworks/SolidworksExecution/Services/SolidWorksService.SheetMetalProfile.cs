// profile_limits (sheet_metal_feature edge_flange): SHORTEN the profile SolidWorks generates for an edge flange along its edge - a lip that stops short of a corner, a return flange stopped before its bends.
// In the UI this is Edit Flange Profile + a dimension; here it is done through the API, inside the SAME feature (a partial flange is never a second flange).
// What was measured:
//   - the generated profile is 4 lines; its BASELINE is a converted model edge (sketch relation USEEDGE = 32), so SetCoords on the baseline's end points answers false and the solver pulls a moved top point back;
//   - what works: edit the profile sketch, delete the USEEDGE relation, move the baseline's end points (the end lines are vertical: the top points follow), exit the sketch, rebuild. The flange rebuilds with the shorter profile
//     (the mitre / relief geometry at the ends is made again by SolidWorks) and keeps one feature, its bends and its edges;
//   - what BREAKS the flange: sketch_fully_define on that profile sketch (it deleted dimensions inside the flange's own sketch and the feature failed to rebuild, 66 faces -> 22, then the next sheet_metal_params call killed
//     SolidWorks): the profile sketches are NOT given to the fully-define tool (it refuses them), and the result of this trim is checked by the feature's own error code, with the old extent put back when the flange fails.
// Input: [{"point":[x,y,z] (any point ON the flange's edge, METERS), "from":[x,y,z], "to":[x,y,z]}] - the stretch of the edge that KEEPS its flange (two points along the edge); the profile never grows.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        private static double[] ProfileVec3(JToken t)
        {
            var a = t as JArray; if (a == null || a.Count != 3) return null;
            return new double[] { (double)a[0], (double)a[1], (double)a[2] };
        }

        private static bool ProfileExtent(ISketch sk, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            var pts = sk.GetSketchPoints2() as object[]; if (pts == null) return false;
            foreach (object po in pts) { var sp = po as ISketchPoint; if (sp == null) continue; if (sp.X < min) min = sp.X; if (sp.X > max) max = sp.X; }
            return max > min;
        }

        // moves the profile's ends to [lo, hi] in sketch x (the relation USEEDGE of the baseline is released first); returns false when SolidWorks would not take a point
        private bool ProfileSetExtent(IModelDoc2 doc, IFeature sketchFeature, double oldMin, double oldMax, double lo, double hi, out string why)
        {
            why = null;
            doc.ClearSelection2(true);
            if (!sketchFeature.Select2(false, 0)) { why = "could not select the profile sketch " + sketchFeature.Name; return false; }
            doc.EditSketch();
            var active = doc.SketchManager.ActiveSketch as ISketch;
            if (active == null) { why = "the profile sketch " + sketchFeature.Name + " would not open for editing"; return false; }
            bool ok = true;
            try
            {
                var rm = active.RelationManager as SketchRelationManager;
                var rels = rm == null ? null : rm.GetRelations(0) as object[];
                if (rels != null)
                    foreach (object ro in rels)
                    {
                        var r = ro as SketchRelation;
                        if (r != null && r.GetRelationType() == (int)swConstraintType_e.swConstraintType_USEEDGE) rm.DeleteRelation(r);
                    }
                var pts = active.GetSketchPoints2() as object[];
                if (pts != null)
                {
                    foreach (object po in pts)
                    {
                        var sp = po as ISketchPoint; if (sp == null) continue;
                        if (Math.Abs(sp.X - oldMin) < 1e-7 && Math.Abs(lo - oldMin) > 1e-7) { if (!sp.SetCoords(lo, sp.Y, sp.Z)) { ok = false; why = "SolidWorks would not move a profile point to " + lo; } }
                        else if (Math.Abs(sp.X - oldMax) < 1e-7 && Math.Abs(hi - oldMax) > 1e-7) { if (!sp.SetCoords(hi, sp.Y, sp.Z)) { ok = false; why = "SolidWorks would not move a profile point to " + hi; } }
                    }
                }
            }
            finally { doc.SketchManager.InsertSketch(true); }
            doc.EditRebuild3();
            return ok;
        }

        // returns one report entry per limit; every problem goes into `problems` (the caller turns verified false)
        private JArray ApplyProfileLimits(IModelDoc2 doc, IFeature flange, JArray limits, JArray problems)
        {
            var report = new JArray();
            var profiles = new List<IFeature>();
            var sub = flange.GetFirstSubFeature() as IFeature;
            while (sub != null) { if (sub.GetTypeName2() == "ProfileFeature") profiles.Add(sub); sub = sub.GetNextSubFeature() as IFeature; }
            var mu = _solidWorks.GetMathUtility() as IMathUtility;
            foreach (var tok in limits)
            {
                var lim = tok as JObject;
                double[] pt = lim == null ? null : ProfileVec3(lim["point"]), from = lim == null ? null : ProfileVec3(lim["from"]), to = lim == null ? null : ProfileVec3(lim["to"]);
                if (pt == null || from == null || to == null) { problems.Add("profile_limits: each entry needs point, from and to as [x,y,z] (meters)"); continue; }
                IFeature target = null; ISketch tsk = null; double[] toA = null, fromA = null;
                foreach (var sf in profiles)
                {
                    var sk = sf.GetSpecificFeature2() as ISketch; if (sk == null) continue;
                    var xf = sk.ModelToSketchTransform as IMathTransform; if (xf == null) continue;
                    Func<double[], double[]> toSketch = p3 => (double[])((IMathPoint)((IMathPoint)mu.CreatePoint(p3)).MultiplyTransform(xf)).ArrayData;
                    var loc = toSketch(pt);
                    if (Math.Abs(loc[1]) < 2e-4 && Math.Abs(loc[2]) < 2e-4) { target = sf; tsk = sk; fromA = toSketch(from); toA = toSketch(to); break; }      // the baseline of a generated profile lies on the sketch's x axis
                }
                if (target == null) { problems.Add("profile_limits: no profile sketch of " + flange.Name + " has its baseline through the point " + string.Join(",", pt)); continue; }
                double oldMin, oldMax;
                if (!ProfileExtent(tsk, out oldMin, out oldMax)) { problems.Add("profile_limits: could not read the extent of " + target.Name); continue; }
                double lo = Math.Min(fromA[0], toA[0]), hi = Math.Max(fromA[0], toA[0]);
                const double tol = 1e-6;
                if (lo < oldMin - tol || hi > oldMax + tol) { problems.Add("profile_limits: " + target.Name + " spans " + Math.Round(oldMin, 6) + ".." + Math.Round(oldMax, 6) + " m, the requested stretch " + Math.Round(lo, 6) + ".." + Math.Round(hi, 6) + " m would make it longer (a profile only shrinks)"); continue; }
                if (hi - lo < 1e-4) { problems.Add("profile_limits: the stretch for " + target.Name + " is shorter than 0.1 mm"); continue; }
                lo = Math.Max(lo, oldMin); hi = Math.Min(hi, oldMax);
                var entry = new JObject { ["sketch"] = target.Name, ["old_min"] = Math.Round(oldMin, 6), ["old_max"] = Math.Round(oldMax, 6), ["new_min"] = Math.Round(lo, 6), ["new_max"] = Math.Round(hi, 6) };
                string why;
                bool moved = ProfileSetExtent(doc, target, oldMin, oldMax, lo, hi, out why);
                bool warn; int err = flange.GetErrorCode2(out warn);
                double nMin = 0, nMax = 0; var after = target.GetSpecificFeature2() as ISketch;
                bool read = after != null && ProfileExtent(after, out nMin, out nMax);
                bool kept = moved && err == 0 && read && Math.Abs(nMin - lo) < tol && Math.Abs(nMax - hi) < tol;
                entry["kept"] = kept;
                if (!kept)
                {
                    // put the old extent back (the converted baseline stays released, the geometry is as it was) and say so
                    string why2; ProfileSetExtent(doc, target, nMin, nMax, oldMin, oldMax, out why2);
                    bool w2; int err2 = flange.GetErrorCode2(out w2);
                    entry["restored"] = err2 == 0;
                    problems.Add("profile_limits: " + target.Name + " could not be shortened to " + Math.Round(lo, 6) + ".." + Math.Round(hi, 6) + " m (" + (why ?? ("the flange reports rebuild error " + err + " / extent read " + Math.Round(nMin, 6) + ".." + Math.Round(nMax, 6))) + "); the old extent was put back" + (err2 == 0 ? "" : " BUT the flange still reports error " + err2));
                }
                report.Add(entry);
            }
            return report;
        }
    }
}
