// ONE fillet feature with a radius of its own per edge (Fillet PropertyManager: "Multiple radius fillet") - IFeatureManager.FeatureFillet3 with the Radii array. The user's v4 rounds the twelve outline
// corners of the UNFOLDED blank with one such feature (10 / 5 / 15 mm sets of four); a fillet cannot be mirrored, so it has to be a single feature (probes 11 and 16 proved the call on their reference).
// Edges are found by GEOMETRY - a point ON each edge (meters; any curve type), or a stable index from analyze_model(edges) - and selected as objects with mark 1; a point that lies on two edges is refused
// instead of guessed. The result is read back from ISimpleFilletFeatureData2: every requested edge must carry its requested radius. Reached through add_edge_feature when `radii` is given.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        // A sheet-metal part keeps its Flat-Pattern feature LAST; a feature made through the API has to go in BEFORE it (a cut made with the bar after it silently returns null). The selection has to be made
        // inside `make`: rolling the bar back clears it.
        private T BeforeFlatPattern<T>(IModelDoc2 doc, Func<T> make)
        {
            var flat = FindFlatPattern(doc); bool rolled = false;
            if (flat != null) { try { rolled = doc.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, flat.Name); } catch (COMException) { rolled = false; } }
            try { return make(); }
            finally { if (rolled) { try { doc.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); } catch (COMException) { } } }
        }

        private static double[] EdgeMid(IEdge e)
        {
            var c = e.GetCurve() as ICurve; double s, en; bool closed, periodic; c.GetEndParams(out s, out en, out closed, out periodic);
            var m = c.Evaluate2((s + en) / 2, 0) as double[]; return new[] { m[0], m[1], m[2] };
        }

        // every edge (of any curve type: line, arc, spline - an unfolded blank's corner edges are not always tagged as lines) within the pick tolerance of the point (more than one = the point sits on a corner)
        private List<IEdge> EdgesNear(IModelDoc2 doc, double[] pt)
        {
            var hits = new List<IEdge>();
            foreach (var e in FlattenEdges(doc as IPartDoc))
            {
                try
                {
                    var q = e.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; if (q == null || q.Length < 3) continue;
                    double px = q[0] - pt[0], py = q[1] - pt[1], pz = q[2] - pt[2];
                    if (Math.Sqrt(px * px + py * py + pz * pz) < PickTolerance) hits.Add(e);
                }
                catch (COMException) { }
            }
            return hits;
        }

        public ExecutionResponse FilletMultiRadius(ToolRequest request)
        {
            return Typed(request, true, (doc, p) =>
            {
                var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "A multi-radius fillet needs an active part.");
                string ftype = (p.Value<string>("feature_type") ?? "fillet").ToLowerInvariant(); if (ftype != "fillet") throw new ToolFailure("INVALID_PARAMETER", "radii (one radius per edge) belongs to feature_type 'fillet'.");
                var rt = AsArray(p["radii"] ?? p["radii_json"], "radii"); if (rt == null || rt.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "radii is required: [r1, r2, ...] meters, one per edge in the order the edges are listed.");
                var radii = new List<double>(); foreach (var t in rt) { double r = t.Value<double>(); if (r <= 0 || r > 0.1) throw new ToolFailure("INVALID_PARAMETER", "each radius is in METERS (0 < r <= 100 mm)."); radii.Add(r); }

                // edges: stable indices first (edge_indices, or edges), then points ON straight edges (points, or edges_json = [{ex,ey,ez}, ...])
                var edges = new List<IEdge>(); var all = FlattenEdges(part);
                var idx = AsArray(p["edge_indices"] ?? p["edges"], "edge_indices");
                if (idx != null)
                    foreach (var t in idx)
                    {
                        int i = t.Value<int>(); if (i < 0 || i >= all.Count) throw new ToolFailure("EDGE_NOT_FOUND", "Edge index " + i + " out of range (part has " + all.Count + " edges). Call analyze_model(edges).");
                        edges.Add(all[i]);
                    }
                var pts = AsArray(p["points"], "points"); var ej = AsArray(p["edges_json"], "edges_json");
                var pointList = new List<double[]>();
                if (pts != null) foreach (var t in pts) pointList.Add(ToPoint(t, "each point"));
                if (ej != null) foreach (var t in ej) { var o = t as JObject; pointList.Add(o != null ? new[] { o.Value<double>("ex"), o.Value<double>("ey"), o.Value<double>("ez") } : ToPoint(t, "each edges_json item")); }
                foreach (var pt in pointList)
                {
                    var hits = EdgesNear(doc, pt); string at = "(" + string.Join(", ", pt.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)).ToArray()) + ")";
                    if (hits.Count == 0) throw new ToolFailure("EDGE_NOT_FOUND", "No edge within 0.1 mm of " + at + " m.");
                    if (hits.Count > 1) throw new ToolFailure("EDGE_AMBIGUOUS", hits.Count + " edges pass within 0.1 mm of " + at + " m (a corner?): give a point in the middle of the edge or its index from analyze_model(edges).");
                    edges.Add(hits[0]);
                }
                if (edges.Count == 0) throw new ToolFailure("MISSING_PARAMETER", "edge_indices (from analyze_model(edges)) and/or points ([[x,y,z], ...] ON straight edges, meters) are required.");
                if (edges.Count != radii.Count) throw new ToolFailure("INVALID_PARAMETER", "radii has " + radii.Count + " values for " + edges.Count + " edges: one radius per edge, in the order the edges are listed (indices first, then points).");
                var mids = edges.Select(EdgeMid).ToList();
                for (int a = 0; a < mids.Count; a++) for (int b = a + 1; b < mids.Count; b++)
                    if (Math.Abs(mids[a][0] - mids[b][0]) + Math.Abs(mids[a][1] - mids[b][1]) + Math.Abs(mids[a][2] - mids[b][2]) < 1e-7) throw new ToolFailure("INVALID_PARAMETER", "edges " + a + " and " + b + " are the same edge.");

                if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true);
                var names0 = TopNames(doc); bool propagate = p.Value<bool?>("propagate") ?? false;
                IFeature feat = BeforeFlatPattern(doc, () =>
                {
                    doc.ClearSelection2(true);
                    var sd = ((ISelectionMgr)doc.SelectionManager).CreateSelectData(); sd.Mark = 1;
                    foreach (var e in edges) if (!((IEntity)e).Select4(true, sd)) throw new ToolFailure("SELECTION_FAILED", "Could not select one of the edges.");
                    // Options 0 + a Radii array = "multiple radius fillet"; R1 is the default radius of the items
                    return doc.FeatureManager.FeatureFillet3(propagate ? (int)swFeatureFilletOptions_e.swFeatureFilletPropagate : 0, radii[0], 0, 0, (int)swFeatureFilletType_e.swFeatureFilletType_Simple, 0, 0,
                        radii.ToArray(), null, null, null, null, null, null) as IFeature;
                });
                doc.ClearSelection2(true);
                if (feat == null) throw new ToolFailure("FILLET_FAILED", "FeatureFillet3 returned null: a radius is too large for the faces around an edge, or the edges cannot be filleted together.");
                doc.EditRebuild3();
                var created = TopFeatures(doc).FirstOrDefault(f => !names0.Contains(f.Name) && f.GetTypeName2() == "Fillet") ?? feat;

                // read back: every requested edge must carry its radius
                var problems = new JArray(); var report = new JArray(); int err = FeatureError(created);
                if (err != 0) problems.Add("the Fillet feature reports rebuild error " + err);
                var def = created.GetDefinition() as ISimpleFilletFeatureData2; bool multi = false; int count = 0;
                if (def != null)
                {
                    bool acc = false; try { acc = def.AccessSelections(doc, null); } catch (COMException) { }
                    try
                    {
                        multi = def.IsMultipleRadius; count = def.GetEdgeCount();
                        var seen = new bool[edges.Count];
                        for (int k = 0; k < count; k++)
                        {
                            var item = def.GetFilletItemAtIndex(k); double r = def.GetRadius(item); var ie = item as IEdge; var m = ie == null ? null : EdgeMid(ie);
                            int match = -1; double best = double.MaxValue;
                            if (m != null) for (int j = 0; j < mids.Count; j++) { double d = Math.Abs(m[0] - mids[j][0]) + Math.Abs(m[1] - mids[j][1]) + Math.Abs(m[2] - mids[j][2]); if (d < best) { best = d; match = j; } }
                            report.Add(new JObject { ["mid"] = m == null ? null : new JArray(Math.Round(m[0], 9), Math.Round(m[1], 9), Math.Round(m[2], 9)), ["radius"] = Math.Round(r, 9) });
                            if (match >= 0 && best < 1e-6) { seen[match] = true; if (Math.Abs(r - radii[match]) > 1e-9) problems.Add("edge " + match + " carries r = " + (r * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm, not " + (radii[match] * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " mm"); }
                        }
                        for (int j = 0; j < seen.Length; j++) if (!seen[j]) problems.Add("requested edge " + j + " is not in the fillet feature");
                    }
                    finally { if (acc) { try { def.ReleaseSelectionAccess(); } catch (COMException) { } } }
                }
                else problems.Add("the Fillet feature's definition could not be read back");
                if (radii.Distinct().Count() > 1 && !multi) problems.Add("the feature is not a multiple-radius fillet");
                var res = new JObject { ["feature"] = created.Name, ["edges"] = edges.Count, ["multi_radius"] = multi, ["edges_in_feature"] = count, ["radii_read_back"] = report, ["feature_error"] = err };
                if (problems.Count > 0) res["problems"] = problems;
                res["verified"] = err == 0 && problems.Count == 0;
                return res;
            });
        }
    }
}
