// sheet_metal_from_flat: build a sheet-metal part from a FLAT pattern - the inverse of the flat-pattern DXF export.
// The flat pattern is a 2D drawing: the OUTLINE (a closed chain of lines / arcs / splines, plus closed inner loops = cut-outs) and the BEND LINES (straight lines that run from the outline to the outline).
// Everything geometric is decided here from the DXF alone: the tool tells the outline from the bend lines, cuts the outline into REGIONS along the bend lines, finds the region that stays put and the TREE of
// bends that leads to every other region, and then builds - a base flange from the outline, then SKETCHED BENDS, the OUTERMOST bends first, so every bend line is still in the flat plane when its turn comes.
// SolidWorks folds a sketched bend along the whole (infinite) line, whatever the length of the sketched segment (proven live: a 20 mm line bends a 60 mm blank across its full width), so a flat only folds
// this way when no region reaches across the line of a bend that is not its own: the plan checks that BEFORE anything is created and refuses a flat whose corners are closed (Convert / closed-corner flats
// have wall edges that reach 0.3 .. 2 mm past the neighbouring bend line). What the DXF cannot say, the caller says: units (read from $INSUNITS when present), thickness, bend radius, K-factor, the bend angle,
// and the direction of each bend (SolidWorks' own API export draws the two directions in two linetypes: continuous / HIDDEN). The flat blank is invariant under sketched bends, so the closed-loop proof is
// that the part's OWN flat pattern has the size of the DXF outline. Lengths are METERS (the DXF is scaled by its units).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // the flat pattern as a drawing
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private sealed class FlatEnt
        {
            public string Kind;                 // line | arc | circle | spline
            public bool Hidden;                 // HIDDEN / DASHED line type
            public string Layer = "";
            public double[] A, B;               // end points (arc: start and end; spline: first and last sample; circle: none)
            public double Cx, Cy, R;            // arc / circle: centre and radius
            public double A0, A1;               // arc: start / end angle in radians, counter-clockwise from A to B (the DXF convention)
            public List<double[]> Pts;          // spline: samples from A to B
            public int N1 = -1, N2 = -1;        // end-point clusters of A and B
            public bool Pruned;
        }

        private sealed class FlatDxf
        {
            public int InsUnits; public int Measurement = -1;
            public List<FlatEnt> Ents = new List<FlatEnt>();
            public SortedDictionary<string, int> Seen = new SortedDictionary<string, int>();
            public SortedDictionary<string, int> Skipped = new SortedDictionary<string, int>();
        }

        private sealed class FlatLoop
        {
            public List<FlatEnt> Ents = new List<FlatEnt>(); public List<bool> Fwd = new List<bool>();
            public List<double[]> Poly = new List<double[]>(); public double Area;
        }

        private sealed class FlatBend
        {
            public FlatEnt E; public double[] A, B; public bool Hidden; public double Length;
            public double[] SA, SB;                // the chord as cut into the outline: the end points snapped onto the outline
            public int Near = -1, Far = -1, Depth; public string Dir = "up";
        }

        private sealed class FlatPlan
        {
            public string Dxf, Units; public double Scale;
            public SortedDictionary<string, int> Seen, Skipped;
            public FlatLoop Outer; public List<FlatLoop> Holes = new List<FlatLoop>();
            public List<FlatBend> Bends = new List<FlatBend>();
            public List<List<double[]>> Regions = new List<List<double[]>>();
            public int Base; public double[] Fixed; public double[] Box;      // box: minx, miny, maxx, maxy of the outline
            public List<List<FlatBend>> Groups = new List<List<FlatBend>>();
            public List<string> Warnings = new List<string>();
            public bool SplinesOrArcs;
        }

        private static string Mmf(double m) { return (m * 1000).ToString("0.###", CultureInfo.InvariantCulture); }
        private static string Pt2(double[] p) { return "(" + Mmf(p[0]) + ", " + Mmf(p[1]) + ") mm"; }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // ASCII DXF reader
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static string DxfVal(List<KeyValuePair<int, string>> e, int code)
        {
            foreach (var kv in e) if (kv.Key == code) return kv.Value;
            return null;
        }
        private static double DxfNum(List<KeyValuePair<int, string>> e, int code, double dflt)
        {
            var s = DxfVal(e, code); double v;
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : dflt;
        }
        private static List<double> DxfAll(List<KeyValuePair<int, string>> e, int code)
        {
            var l = new List<double>(); double v;
            foreach (var kv in e) if (kv.Key == code && double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) l.Add(v);
            return l;
        }

        private static List<List<KeyValuePair<int, string>>> DxfRaw(string path, out int insUnits, out int measurement)
        {
            insUnits = 0; measurement = -1; var ents = new List<List<KeyValuePair<int, string>>>();
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch (Exception ex) { throw new ToolFailure("DXF_UNREADABLE", "Cannot read '" + path + "': " + ex.Message); }
            if (lines.Length > 0 && lines[0].StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal)) throw new ToolFailure("DXF_BINARY", "A binary DXF cannot be read: save it as an ASCII DXF.");
            string section = null, headerVar = null; List<KeyValuePair<int, string>> cur = null;
            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                int code;
                if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) throw new ToolFailure("DXF_INVALID", "Line " + (i + 1) + " of the DXF is not a group code: '" + lines[i].Trim() + "'.");
                string val = lines[i + 1].Trim();
                if (code == 0 && val == "SECTION") { section = "?"; cur = null; continue; }
                if (code == 2 && section == "?") { section = val; continue; }
                if (code == 0 && val == "ENDSEC") { section = null; cur = null; continue; }
                if (section == "HEADER")
                {
                    int n;
                    if (code == 9) headerVar = val;
                    else if (headerVar == "$INSUNITS" && code == 70 && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) insUnits = n;
                    else if (headerVar == "$MEASUREMENT" && code == 70 && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) measurement = n;
                }
                else if (section == "ENTITIES")
                {
                    if (code == 0) { cur = new List<KeyValuePair<int, string>>(); ents.Add(cur); }
                    if (cur != null) cur.Add(new KeyValuePair<int, string>(code, val));
                }
            }
            return ents;
        }

        // NURBS point (rational when weights are given) by Cox-de Boor (The NURBS Book A2.1 / A2.2); null when the data cannot describe a curve
        private static List<double[]> SampleNurbs(int degree, double[] knots, List<double[]> cps, List<double> weights, int samples)
        {
            int n = cps.Count - 1, p = degree;
            if (p < 1 || n < p || knots.Length != n + p + 2) return null;
            double u0 = knots[p], u1 = knots[n + 1]; if (!(u1 > u0)) return null;
            var res = new List<double[]>();
            for (int s = 0; s <= samples; s++)
            {
                double u = s == samples ? u1 : u0 + (u1 - u0) * s / samples;
                int span;
                if (u >= knots[n + 1]) span = n;
                else
                {
                    int low = p, high = n + 1, mid = (low + high) / 2;
                    while (u < knots[mid] || u >= knots[mid + 1]) { if (u < knots[mid]) high = mid; else low = mid; mid = (low + high) / 2; }
                    span = mid;
                }
                var N = new double[p + 1]; var left = new double[p + 1]; var right = new double[p + 1]; N[0] = 1;
                for (int j = 1; j <= p; j++)
                {
                    left[j] = u - knots[span + 1 - j]; right[j] = knots[span + j] - u; double saved = 0;
                    for (int r = 0; r < j; r++) { double temp = N[r] / (right[r + 1] + left[j - r]); N[r] = saved + right[r + 1] * temp; saved = left[j - r] * temp; }
                    N[j] = saved;
                }
                double x = 0, y = 0, w = 0;
                for (int l = 0; l <= p; l++)
                {
                    int idx = span - p + l; double wt = weights != null && weights.Count == cps.Count ? weights[idx] : 1.0;
                    x += N[l] * wt * cps[idx][0]; y += N[l] * wt * cps[idx][1]; w += N[l] * wt;
                }
                res.Add(new[] { x / w, y / w });
            }
            return res;
        }

        private static FlatEnt FlatLine(double[] a, double[] b, bool hidden, string layer) { return new FlatEnt { Kind = "line", A = a, B = b, Hidden = hidden, Layer = layer }; }

        private static FlatEnt FlatArc(double cx, double cy, double r, double a0, double a1, bool hidden, string layer)
        {
            return new FlatEnt { Kind = "arc", Cx = cx, Cy = cy, R = r, A0 = a0, A1 = a1, Hidden = hidden, Layer = layer,
                A = new[] { cx + r * Math.Cos(a0), cy + r * Math.Sin(a0) }, B = new[] { cx + r * Math.Cos(a1), cy + r * Math.Sin(a1) } };
        }

        // the arc of a polyline bulge (bulge = tan(sweep / 4), positive = counter-clockwise from p to q)
        private static FlatEnt FlatBulge(double[] p, double[] q, double bulge, bool hidden, string layer)
        {
            double dx = q[0] - p[0], dy = q[1] - p[1], chord = Math.Sqrt(dx * dx + dy * dy); if (chord < 1e-12) return null;
            double theta = Math.Abs(4 * Math.Atan(bulge)), rad = chord / (2 * Math.Sin(theta / 2)), d = rad * Math.Cos(theta / 2), sgn = bulge > 0 ? 1 : -1;
            double nx = -dy / chord, ny = dx / chord, cx = (p[0] + q[0]) / 2 + sgn * nx * d, cy = (p[1] + q[1]) / 2 + sgn * ny * d;
            double ap = Math.Atan2(p[1] - cy, p[0] - cx), aq = Math.Atan2(q[1] - cy, q[0] - cx);
            return bulge > 0 ? FlatArc(cx, cy, rad, ap, aq, hidden, layer) : FlatArc(cx, cy, rad, aq, ap, hidden, layer);
        }

        private static void FlatEntities(List<List<KeyValuePair<int, string>>> raw, double scale, FlatDxf dxf)
        {
            Func<double, double> S = v => v * scale;
            foreach (var e in raw)
            {
                string type = e[0].Value.ToUpperInvariant(), lt = (DxfVal(e, 6) ?? "").ToUpperInvariant(), layer = DxfVal(e, 8) ?? "";
                bool hidden = lt.Contains("HIDDEN") || lt.Contains("DASH");
                int c; dxf.Seen.TryGetValue(type, out c); dxf.Seen[type] = c + 1;
                switch (type)
                {
                    case "LINE":
                        dxf.Ents.Add(FlatLine(new[] { S(DxfNum(e, 10, 0)), S(DxfNum(e, 20, 0)) }, new[] { S(DxfNum(e, 11, 0)), S(DxfNum(e, 21, 0)) }, hidden, layer)); break;
                    case "ARC":
                    {
                        double a0 = DxfNum(e, 50, 0) * Math.PI / 180, a1 = DxfNum(e, 51, 0) * Math.PI / 180;
                        dxf.Ents.Add(FlatArc(S(DxfNum(e, 10, 0)), S(DxfNum(e, 20, 0)), S(DxfNum(e, 40, 0)), a0, a1, hidden, layer)); break;
                    }
                    case "CIRCLE":
                        dxf.Ents.Add(new FlatEnt { Kind = "circle", Cx = S(DxfNum(e, 10, 0)), Cy = S(DxfNum(e, 20, 0)), R = S(DxfNum(e, 40, 0)), Hidden = hidden, Layer = layer }); break;
                    case "LWPOLYLINE":
                    {
                        var vx = new List<double[]>(); var bul = new List<double>(); int flag = (int)DxfNum(e, 70, 0);
                        foreach (var kv in e)
                        {
                            double v; if (!double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                            if (kv.Key == 10) { vx.Add(new[] { S(v), 0.0 }); bul.Add(0); }
                            else if (kv.Key == 20 && vx.Count > 0) vx[vx.Count - 1][1] = S(v);
                            else if (kv.Key == 42 && bul.Count > 0) bul[bul.Count - 1] = v;
                        }
                        int segs = (flag & 1) != 0 ? vx.Count : vx.Count - 1;
                        for (int i = 0; i < segs; i++)
                        {
                            var p = vx[i]; var q = vx[(i + 1) % vx.Count];
                            var ent = Math.Abs(bul[i]) > 1e-9 ? FlatBulge(p, q, bul[i], hidden, layer) : FlatLine(p, q, hidden, layer);
                            if (ent != null) dxf.Ents.Add(ent);
                        }
                        break;
                    }
                    case "SPLINE":
                    {
                        int flags = (int)DxfNum(e, 70, 0), degree = (int)DxfNum(e, 71, 3);
                        var xs = DxfAll(e, 10); var ys = DxfAll(e, 20); var cps = new List<double[]>();
                        for (int i = 0; i < Math.Min(xs.Count, ys.Count); i++) cps.Add(new[] { S(xs[i]), S(ys[i]) });
                        var fx = DxfAll(e, 11); var fy = DxfAll(e, 21); var fit = new List<double[]>();
                        for (int i = 0; i < Math.Min(fx.Count, fy.Count); i++) fit.Add(new[] { S(fx[i]), S(fy[i]) });
                        List<double[]> pts = null;
                        if (cps.Count > degree && (flags & 1) == 0) pts = SampleNurbs(degree, DxfAll(e, 40).ToArray(), cps, (flags & 4) != 0 ? DxfAll(e, 41) : null, 32);
                        if (pts == null && fit.Count >= 2) pts = fit;
                        if (pts == null || pts.Count < 2) { int k; dxf.Skipped.TryGetValue("SPLINE (unreadable or closed)", out k); dxf.Skipped["SPLINE (unreadable or closed)"] = k + 1; break; }
                        dxf.Ents.Add(new FlatEnt { Kind = "spline", Pts = pts, A = pts[0], B = pts[pts.Count - 1], Hidden = hidden, Layer = layer }); break;
                    }
                    default: { int k; dxf.Skipped.TryGetValue(type, out k); dxf.Skipped[type] = k + 1; break; }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // small plane geometry
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static double Dist2(double[] a, double[] b) { return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])); }
        private static double Orient(double[] a, double[] b, double[] c) { return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]); }
        private static double RingArea(List<double[]> r) { double s = 0; for (int i = 0; i < r.Count; i++) { var a = r[i]; var b = r[(i + 1) % r.Count]; s += a[0] * b[1] - b[0] * a[1]; } return s / 2; }

        private static double DistToSeg(double[] p, double[] a, double[] b, out double t)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], len2 = dx * dx + dy * dy;
            t = len2 < 1e-30 ? 0 : ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len2; t = Math.Max(0, Math.Min(1, t));
            double qx = a[0] + t * dx, qy = a[1] + t * dy; return Math.Sqrt((p[0] - qx) * (p[0] - qx) + (p[1] - qy) * (p[1] - qy));
        }

        private static double FlatDistToLine(double[] p, double[] a, double[] b)
        {
            double len = Dist2(a, b); return len < 1e-15 ? Dist2(p, a) : Math.Abs(Orient(a, b, p)) / len;
        }

        private static bool PointInRing(List<double[]> ring, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        private static double DistToRing(List<double[]> ring, double[] p)
        {
            double best = double.MaxValue;
            for (int i = 0; i < ring.Count; i++) { double t; double d = DistToSeg(p, ring[i], ring[(i + 1) % ring.Count], out t); if (d < best) best = d; }
            return best;
        }

        // a point of the ring (an existing vertex, or a new vertex inserted on the nearest edge) within tol of p; null when p is not on the boundary
        private static double[] EnsureVertex(List<double[]> ring, double[] p, double tol)
        {
            foreach (var v in ring) if (Dist2(v, p) <= tol) return v;
            int best = -1; double bd = double.MaxValue, bt = 0;
            for (int i = 0; i < ring.Count; i++) { double t; double d = DistToSeg(p, ring[i], ring[(i + 1) % ring.Count], out t); if (d < bd) { bd = d; best = i; bt = t; } }
            if (best < 0 || bd > tol) return null;
            var a = ring[best]; var b = ring[(best + 1) % ring.Count]; var q = new[] { a[0] + bt * (b[0] - a[0]), a[1] + bt * (b[1] - a[1]) };
            ring.Insert(best + 1, q); return q;
        }

        // the chord a-b cuts the ring into two rings; false when the chord does not run through the inside of the ring
        private static bool SplitRing(List<double[]> ring, double[] a, double[] b, double tol, out List<double[]> r1, out List<double[]> r2, out double[] sa, out double[] sb)
        {
            r1 = null; r2 = null; sa = null; sb = null;
            var rg = new List<double[]>(ring);
            var pa = EnsureVertex(rg, a, tol); var pb = EnsureVertex(rg, b, tol);
            if (pa == null || pb == null || ReferenceEquals(pa, pb)) return false;
            int ia = rg.IndexOf(pa), ib = rg.IndexOf(pb), n = rg.Count;
            var m = new[] { (pa[0] + pb[0]) / 2, (pa[1] + pb[1]) / 2 };
            if (!PointInRing(ring, m[0], m[1])) return false;
            for (int i = 0; i < n; i++)      // the chord may not cross the boundary anywhere else
            {
                var c = rg[i]; var d = rg[(i + 1) % n]; if (ReferenceEquals(c, pa) || ReferenceEquals(c, pb) || ReferenceEquals(d, pa) || ReferenceEquals(d, pb)) continue;
                if (Orient(pa, pb, c) * Orient(pa, pb, d) < 0 && Orient(c, d, pa) * Orient(c, d, pb) < 0) return false;
            }
            sa = pa; sb = pb;
            r1 = new List<double[]>(); for (int k = ia; ; k = (k + 1) % n) { r1.Add(rg[k]); if (k == ib) break; }
            r2 = new List<double[]>(); for (int k = ib; ; k = (k + 1) % n) { r2.Add(rg[k]); if (k == ia) break; }
            return r1.Count >= 3 && r2.Count >= 3;
        }

        // a point inside the ring, away from its boundary: the middle of the widest stretch of the horizontal line through the centroid
        private static double[] InteriorPoint(List<double[]> ring)
        {
            double cy = ring.Average(v => v[1]); double bestW = -1, bx = ring[0][0];
            for (int pass = 0; pass < 7; pass++)
            {
                double y = cy + (pass == 0 ? 0 : (pass % 2 == 1 ? 1 : -1) * ((pass + 1) / 2) * 0.01 * (ring.Max(v => v[1]) - ring.Min(v => v[1])));
                var xs = new List<double>();
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    double yi = ring[i][1], yj = ring[j][1]; if ((yi > y) == (yj > y)) continue;
                    xs.Add(ring[i][0] + (y - yi) * (ring[j][0] - ring[i][0]) / (yj - yi));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2) if (xs[k + 1] - xs[k] > bestW) { bestW = xs[k + 1] - xs[k]; bx = (xs[k] + xs[k + 1]) / 2; cy = y; }
                if (bestW > 0) break;
            }
            return new[] { bx, cy };
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // the plan: read the DXF, tell the outline from the bend lines, cut the outline into regions, find the tree of bends
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static List<double[]> FlatSample(FlatEnt e, bool forward)
        {
            var pts = new List<double[]>();
            if (e.Kind == "line") pts.Add(forward ? e.A : e.B);
            else if (e.Kind == "arc")
            {
                double sweep = e.A1 - e.A0; while (sweep <= 0) sweep += 2 * Math.PI;
                double step = 2 * Math.Acos(Math.Max(0, 1 - 1e-5 / Math.Max(e.R, 1e-5))); step = Math.Max(step, 0.02);
                int n = Math.Max(2, (int)Math.Ceiling(sweep / step));
                for (int i = 0; i < n; i++) { double a = forward ? e.A0 + sweep * i / n : e.A1 - sweep * i / n; pts.Add(new[] { e.Cx + e.R * Math.Cos(a), e.Cy + e.R * Math.Sin(a) }); }
            }
            else if (e.Kind == "spline") { if (forward) for (int i = 0; i < e.Pts.Count - 1; i++) pts.Add(e.Pts[i]); else for (int i = e.Pts.Count - 1; i > 0; i--) pts.Add(e.Pts[i]); }
            return pts;
        }

        // `into` (optional) is the plan object to fill: when the plan fails half way the caller keeps what was read (the outline, the bend lines) - the flange recipe starts from it
        private FlatPlan PlanFlat(JObject p, FlatPlan into = null)
        {
            string path = p.Value<string>("dxf_path");
            if (string.IsNullOrWhiteSpace(path)) throw new ToolFailure("MISSING_PARAMETER", "dxf_path is required: the full path of an ASCII DXF flat pattern.");
            if (!File.Exists(path)) throw new ToolFailure("FILE_NOT_FOUND", "No file at '" + path + "'.");
            int ins, meas; var raw = DxfRaw(path, out ins, out meas);
            string unitsArg = (p.Value<string>("units") ?? "auto").Trim().ToLowerInvariant(); string units; double scale;
            if (unitsArg == "auto")
            {
                switch (ins) { case 1: units = "in"; break; case 2: units = "ft"; break; case 4: units = "mm"; break; case 5: units = "cm"; break; case 6: units = "m"; break; default: units = null; break; }
                if (units == null) throw new ToolFailure("UNITS_UNKNOWN", "The DXF does not say its units ($INSUNITS = " + ins + "): pass units = 'mm' | 'in' | 'cm' | 'm' | 'ft'.");
            }
            else units = unitsArg;
            switch (units) { case "mm": scale = 0.001; break; case "cm": scale = 0.01; break; case "m": scale = 1; break; case "in": scale = 0.0254; break; case "ft": scale = 0.3048; break; default: throw new ToolFailure("INVALID_PARAMETER", "units must be 'auto', 'mm', 'cm', 'm', 'in' or 'ft'."); }

            double tol = p.Value<double?>("tolerance") ?? 1e-5;           // end points closer than this are one point (10 microns)
            double btol = Math.Max(tol * 5, 5e-5);                         // a bend line ends ON the outline within this (0.05 mm)
            double poke = p.Value<double?>("reach_tolerance") ?? 5e-5;     // how far a region may reach past the line of a bend that is not its own
            var dxf = new FlatDxf { InsUnits = ins, Measurement = meas }; FlatEntities(raw, scale, dxf);
            var plan = into ?? new FlatPlan(); plan.Dxf = path; plan.Units = units; plan.Scale = scale; plan.Seen = dxf.Seen; plan.Skipped = dxf.Skipped;
            if (dxf.Skipped.Count > 0) plan.Warnings.Add("ignored entities: " + string.Join(", ", dxf.Skipped.Select(kv => kv.Key + " x" + kv.Value).ToArray()) + " (text, dimensions and the like carry no geometry for the fold)");
            plan.SplinesOrArcs = dxf.Ents.Any(e => e.Kind == "arc" || e.Kind == "spline" || e.Kind == "circle");

            // ---- end points -> nodes
            var nodes = new List<double[]>();
            Func<double[], int> node = pt => { for (int i = 0; i < nodes.Count; i++) if (Math.Abs(nodes[i][0] - pt[0]) <= tol && Math.Abs(nodes[i][1] - pt[1]) <= tol) return i; nodes.Add(new[] { pt[0], pt[1] }); return nodes.Count - 1; };
            var circles = new List<FlatEnt>(); var edges = new List<FlatEnt>(); var hiddenLines = new List<FlatEnt>();
            foreach (var e in dxf.Ents)
            {
                if (e.Kind == "circle") { circles.Add(e); continue; }
                if (e.Hidden)
                {
                    if (e.Kind != "line") throw new ToolFailure("UNSUPPORTED_BEND_LINE", "A hidden-line " + e.Kind + " cannot be a bend line: bend lines are straight lines.");
                    hiddenLines.Add(e); continue;
                }
                if (e.Kind == "line" && Dist2(e.A, e.B) < tol) continue;      // a point
                e.N1 = node(e.A); e.N2 = node(e.B); edges.Add(e);
            }

            // ---- the outline = the 2-core of the continuous geometry (what lies on a closed chain); what hangs off it is a bend line
            var deg = new int[nodes.Count]; foreach (var e in edges) { deg[e.N1]++; deg[e.N2]++; }
            bool changed = true;
            while (changed) { changed = false; foreach (var e in edges) if (!e.Pruned && (deg[e.N1] < 2 || deg[e.N2] < 2)) { e.Pruned = true; deg[e.N1]--; deg[e.N2]--; changed = true; } }
            // a straight line that joins two outline corners (both ends already carry two outline pieces) is a chord, i.e. a bend line drawn continuous: it leaves the outline
            changed = true;
            while (changed)
            {
                changed = false;
                foreach (var e in edges.Where(x => !x.Pruned && x.Kind == "line" && deg[x.N1] == 3 && deg[x.N2] == 3 && x.N1 != x.N2)) { e.Pruned = true; deg[e.N1]--; deg[e.N2]--; changed = true; break; }
            }
            var core = edges.Where(e => !e.Pruned).ToList();
            var bendEnts = new List<FlatEnt>(hiddenLines);
            foreach (var e in edges.Where(e => e.Pruned))
            {
                if (e.Kind != "line") throw new ToolFailure("OPEN_OUTLINE", "A " + e.Kind + " from " + Pt2(e.A) + " to " + Pt2(e.B) + " is not part of a closed outline and is not a straight bend line.");
                bendEnts.Add(e);
            }
            if (core.Count == 0 && circles.Count == 0) throw new ToolFailure("NO_OUTLINE", "The DXF holds no closed outline (no closed chain of lines, arcs and splines).");

            // ---- loops of the outline
            var at = new Dictionary<int, List<FlatEnt>>(); foreach (var e in core) { foreach (int nd in new[] { e.N1, e.N2 }) { List<FlatEnt> l; if (!at.TryGetValue(nd, out l)) { l = new List<FlatEnt>(); at[nd] = l; } l.Add(e); } }
            foreach (var kv in at) if (kv.Value.Count != 2) throw new ToolFailure("AMBIGUOUS_OUTLINE", kv.Value.Count + " outline pieces meet at " + Pt2(nodes[kv.Key]) + ": the outline must be simple closed chains (a bend line that ends on an outline corner needs one more point of its own).");
            var loops = new List<FlatLoop>(); var seen = new HashSet<FlatEnt>();
            foreach (var start in core)
            {
                if (seen.Contains(start)) continue;
                var lp = new FlatLoop(); var cur = start; int enter = start.N1;
                while (true)
                {
                    seen.Add(cur); bool fwd = cur.N1 == enter; lp.Ents.Add(cur); lp.Fwd.Add(fwd);
                    int next = fwd ? cur.N2 : cur.N1; var nxt = at[next].First(x => !ReferenceEquals(x, cur) || (cur.N1 == cur.N2));
                    if (ReferenceEquals(nxt, start)) break;
                    cur = nxt; enter = next; if (seen.Contains(cur)) break;
                }
                for (int i = 0; i < lp.Ents.Count; i++) lp.Poly.AddRange(FlatSample(lp.Ents[i], lp.Fwd[i]));
                lp.Area = RingArea(lp.Poly); loops.Add(lp);
            }
            foreach (var c in circles)
            {
                var lp = new FlatLoop(); lp.Ents.Add(c); lp.Fwd.Add(true);
                for (int i = 0; i < 64; i++) { double a = 2 * Math.PI * i / 64; lp.Poly.Add(new[] { c.Cx + c.R * Math.Cos(a), c.Cy + c.R * Math.Sin(a) }); }
                lp.Area = RingArea(lp.Poly); loops.Add(lp);
            }
            loops = loops.OrderByDescending(l => Math.Abs(l.Area)).ToList();
            plan.Outer = loops[0]; if (plan.Outer.Area < 0) plan.Outer.Poly.Reverse();
            for (int i = 1; i < loops.Count; i++)
            {
                var h = loops[i]; if (!PointInRing(plan.Outer.Poly, h.Poly[0][0], h.Poly[0][1])) throw new ToolFailure("MULTIPLE_PARTS", "The DXF holds more than one separate outline (a second closed loop lies outside the first): one part per file.");
                plan.Holes.Add(h);
            }
            plan.Box = new[] { plan.Outer.Poly.Min(v => v[0]), plan.Outer.Poly.Min(v => v[1]), plan.Outer.Poly.Max(v => v[0]), plan.Outer.Poly.Max(v => v[1]) };

            // ---- the bend lines
            var seenBends = new List<FlatBend>();
            foreach (var e in bendEnts)
            {
                double len = Dist2(e.A, e.B); if (len < 1e-4) { plan.Warnings.Add("a bend line shorter than 0.1 mm at " + Pt2(e.A) + " was ignored"); continue; }
                if (seenBends.Any(o => (Dist2(o.A, e.A) <= btol && Dist2(o.B, e.B) <= btol) || (Dist2(o.A, e.B) <= btol && Dist2(o.B, e.A) <= btol))) { plan.Warnings.Add("a duplicate bend line at " + Pt2(e.A) + " was ignored"); continue; }
                seenBends.Add(new FlatBend { E = e, A = e.A, B = e.B, Hidden = e.Hidden, Length = len });
            }
            plan.Bends = seenBends;

            // ---- regions: cut the outline along every bend line (a line that starts on another bend line waits until that one is cut)
            var rings = new List<List<double[]>>();
            var outerRing = new List<double[]>(); foreach (var v in plan.Outer.Poly) if (outerRing.Count == 0 || Dist2(outerRing[outerRing.Count - 1], v) > tol * 0.5) outerRing.Add(new[] { v[0], v[1] });
            if (outerRing.Count > 1 && Dist2(outerRing[0], outerRing[outerRing.Count - 1]) <= tol * 0.5) outerRing.RemoveAt(outerRing.Count - 1);
            rings.Add(outerRing);
            var pending = new List<FlatBend>(plan.Bends); string lastFailure = null;
            while (pending.Count > 0)
            {
                bool progress = false;
                foreach (var bd in pending.ToList())
                {
                    var mid = new[] { (bd.A[0] + bd.B[0]) / 2, (bd.A[1] + bd.B[1]) / 2 }; int host = -1;
                    for (int r = 0; r < rings.Count; r++) if (DistToRing(rings[r], bd.A) <= btol && DistToRing(rings[r], bd.B) <= btol && PointInRing(rings[r], mid[0], mid[1])) { host = r; break; }
                    if (host < 0) { lastFailure = "bend line " + Pt2(bd.A) + " -> " + Pt2(bd.B); continue; }
                    List<double[]> r1, r2; double[] sa, sb;
                    if (!SplitRing(rings[host], bd.A, bd.B, btol, out r1, out r2, out sa, out sb)) { lastFailure = "bend line " + Pt2(bd.A) + " -> " + Pt2(bd.B) + " leaves the sheet"; continue; }
                    bd.SA = sa; bd.SB = sb; rings[host] = r1; rings.Add(r2); pending.Remove(bd); progress = true;
                }
                if (!progress)
                {
                    var worstLine = pending.OrderByDescending(b => Math.Max(rings.Min(rg => DistToRing(rg, b.A)), rings.Min(rg => DistToRing(rg, b.B)))).First();
                    double da = rings.Min(rg => DistToRing(rg, worstLine.A)), db = rings.Min(rg => DistToRing(rg, worstLine.B)), dmax = Math.Max(da, db);
                    throw new ToolFailure("BEND_LINE_NOT_ON_OUTLINE", "The " + lastFailure + " does not run from the outline to the outline (both ends must lie on the outline or on another bend line, and the line must lie inside the sheet; a bend line that crosses another one is not foldable)."
                        + (dmax > btol && dmax < 0.003 ? " Its ends stop " + Mmf(da) + " and " + Mmf(db) + " mm short of the outline: that is how a flat with CLOSED corners (Convert to Sheet Metal, closed-corner flanges) ends its bend lines - sketched bends cannot fold it, give the flat corner reliefs." : ""));
                }
            }
            plan.Regions = rings;

            // ---- which regions does each bend join: the two regions beside the middle of its line
            double eps = Math.Max(2 * tol, 2e-5); var joins = new List<int[]>();
            Func<double, double, int> regionAt = (x, y) => { for (int r = 0; r < rings.Count; r++) if (PointInRing(rings[r], x, y)) return r; return -1; };
            foreach (var bd in plan.Bends)
            {
                double dx = bd.SB[0] - bd.SA[0], dy = bd.SB[1] - bd.SA[1], len = Math.Sqrt(dx * dx + dy * dy), nx = -dy / len, ny = dx / len; int ra = -1, rb = -1;
                foreach (double f in new[] { 0.5, 0.25, 0.75, 0.4, 0.6 })
                {
                    double px = bd.SA[0] + f * dx, py = bd.SA[1] + f * dy; ra = regionAt(px + nx * eps, py + ny * eps); rb = regionAt(px - nx * eps, py - ny * eps);
                    if (ra >= 0 && rb >= 0 && ra != rb) break;
                }
                if (ra < 0 || rb < 0 || ra == rb) throw new ToolFailure("BEND_LINE_NOT_ON_OUTLINE", "The bend line " + Pt2(bd.A) + " -> " + Pt2(bd.B) + " does not separate two parts of the sheet (beside its middle: regions " + ra + " and " + rb + " of " + rings.Count + "; region areas mm2: " + string.Join(", ", rings.Select(r => (Math.Abs(RingArea(r)) * 1e6).ToString("0.#", CultureInfo.InvariantCulture)).ToArray()) + "; outline area " + (Math.Abs(RingArea(outerRing)) * 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "; ring " + ra + " box " + (ra < 0 ? "-" : Pt2(new[] { rings[ra].Min(v => v[0]), rings[ra].Min(v => v[1]) }) + " .. " + Pt2(new[] { rings[ra].Max(v => v[0]), rings[ra].Max(v => v[1]) })) + ").");
                joins.Add(new[] { ra, rb });
            }
            int nr = rings.Count; if (plan.Bends.Count != nr - 1) throw new ToolFailure("BEND_GRAPH_NOT_A_TREE", "The bend lines cut the sheet into " + nr + " regions with " + plan.Bends.Count + " bends: a flat can only be folded when every region hangs on the others by exactly one chain of bends.");

            // ---- the region that stays put: the one under fixed_point, else the biggest
            plan.Base = 0; double bestA = -1; var areas = rings.Select(r => Math.Abs(RingArea(r))).ToArray();
            var fp = p["fixed_point"];
            if (fp != null && fp.Type != JTokenType.Null)
            {
                var fa = AsArray(fp, "fixed_point"); if (fa.Count < 2) throw new ToolFailure("INVALID_PARAMETER", "fixed_point is [x, y] in meters (the DXF coordinates scaled by its units).");
                plan.Fixed = new[] { fa[0].Value<double>(), fa[1].Value<double>() }; plan.Base = regionAt(plan.Fixed[0], plan.Fixed[1]);
                if (plan.Base < 0) throw new ToolFailure("FIXED_POINT_OUTSIDE", "fixed_point " + Pt2(plan.Fixed) + " lies outside the outline.");
            }
            else { for (int r = 0; r < nr; r++) if (areas[r] > bestA) { bestA = areas[r]; plan.Base = r; } plan.Fixed = InteriorPoint(rings[plan.Base]); }
            // a point of the fixed region must not sit on a hole: the fixed face is picked at it
            foreach (var h in plan.Holes) if (PointInRing(h.Poly, plan.Fixed[0], plan.Fixed[1])) { var ip = InteriorPoint(rings[plan.Base]); if (!PointInRing(h.Poly, ip[0], ip[1])) plan.Fixed = ip; }

            // ---- the tree from the fixed region (breadth first): depth of every bend = depth of the region it leads to
            var depthOf = Enumerable.Repeat(-1, nr).ToArray(); var parentBend = Enumerable.Repeat(-1, nr).ToArray(); depthOf[plan.Base] = 0;
            var queue = new Queue<int>(); queue.Enqueue(plan.Base);
            while (queue.Count > 0)
            {
                int r = queue.Dequeue();
                for (int k = 0; k < joins.Count; k++)
                {
                    int other = joins[k][0] == r ? joins[k][1] : joins[k][1] == r ? joins[k][0] : -1; if (other < 0 || depthOf[other] >= 0) continue;
                    depthOf[other] = depthOf[r] + 1; parentBend[other] = k; plan.Bends[k].Near = r; plan.Bends[k].Far = other; plan.Bends[k].Depth = depthOf[other]; queue.Enqueue(other);
                }
            }
            if (depthOf.Any(d => d < 0)) throw new ToolFailure("BEND_GRAPH_NOT_A_TREE", "Some regions of the flat are not reached from the fixed region through the bend lines.");

            // ---- direction of every bend: from the caller, else from the two linetypes of the DXF, else all the same way
            var dirs = p["directions"]; JArray dirList = null; JObject dirMap = null;
            if (dirs != null && dirs.Type != JTokenType.Null)
            {
                if (dirs.Type == JTokenType.Object) dirMap = (JObject)dirs;
                else if (dirs.Type == JTokenType.String && ((string)dirs).TrimStart().StartsWith("{", StringComparison.Ordinal)) dirMap = AsObject(dirs, "directions");
                else dirList = AsArray(dirs, "directions");
            }
            string hiddenDir = (p.Value<string>("hidden_direction") ?? "up").ToLowerInvariant(); if (hiddenDir != "up" && hiddenDir != "down") throw new ToolFailure("INVALID_PARAMETER", "hidden_direction must be 'up' or 'down'.");
            bool mixed = plan.Bends.Any(b => b.Hidden) && plan.Bends.Any(b => !b.Hidden);
            foreach (var bd in plan.Bends)
            {
                string d = null; string depthKey = bd.Depth.ToString(CultureInfo.InvariantCulture);
                if (dirList != null && dirList.Count > 0) d = (string)dirList[Math.Min(bd.Depth - 1, dirList.Count - 1)];
                else if (dirMap != null && dirMap[depthKey] != null) d = (string)dirMap[depthKey];
                else if (mixed) d = bd.Hidden ? hiddenDir : (hiddenDir == "up" ? "down" : "up");
                else d = (hiddenDir == "down" && bd.Hidden) ? "down" : "up";
                d = (d ?? "up").Trim().ToLowerInvariant(); if (d != "up" && d != "down") throw new ToolFailure("INVALID_PARAMETER", "A direction must be 'up' or 'down' (toward +Z or -Z of the sketch plane), not '" + d + "'.");
                bd.Dir = d;
            }
            if (!mixed && dirList == null && dirMap == null && plan.Bends.Count > 0)
                plan.Warnings.Add("the DXF draws every bend line in one linetype, so it says nothing about the direction of the bends: every bend folds " + plan.Bends[0].Dir + " - pass directions (one 'up' / 'down' per depth, innermost first) when some bends fold the other way (a lip that turns back)");

            // ---- a sketched bend folds along the WHOLE line: the regions beyond it must be exactly the regions that hang on it
            //      (bends on the same line, of the same depth and direction, are ONE feature: their regions may lie beyond each other's line)
            var children = new List<int>[nr]; for (int r = 0; r < nr; r++) children[r] = new List<int>();
            for (int k = 0; k < plan.Bends.Count; k++) children[plan.Bends[k].Near].Add(plan.Bends[k].Far);
            Func<int, HashSet<int>> subtree = root => { var s = new HashSet<int> { root }; var st = new Stack<int>(); st.Push(root); while (st.Count > 0) foreach (int c in children[st.Pop()]) if (s.Add(c)) st.Push(c); return s; };
            var reach = new List<string>();
            for (int k = 0; k < plan.Bends.Count; k++)
            {
                var bd = plan.Bends[k]; var far = subtree(bd.Far); double len = bd.Length;
                foreach (var other in plan.Bends)      // the same line, drawn as another bend of the same group
                {
                    if (ReferenceEquals(other, bd) || other.Depth != bd.Depth || other.Dir != bd.Dir) continue;
                    if (FlatDistToLine(other.A, bd.A, bd.B) <= poke && FlatDistToLine(other.B, bd.A, bd.B) <= poke) far.UnionWith(subtree(other.Far));
                }
                var fpt = InteriorPoint(rings[bd.Far]); double sgn = Orient(bd.A, bd.B, fpt) > 0 ? 1 : -1;     // + = the far side of the line
                for (int r = 0; r < nr; r++)
                {
                    bool beyond = far.Contains(r); double worst = 0; double[] spot = null;
                    foreach (var v in rings[r])
                    {
                        double sd = sgn * Orient(bd.A, bd.B, v) / len;       // signed distance to the infinite line, positive on the far side
                        double wrong = beyond ? -sd : sd;                    // how far the vertex lies on the wrong side
                        if (wrong > worst) { worst = wrong; spot = v; }
                    }
                    if (worst > poke) reach.Add("bend " + (k + 1) + " (" + Pt2(bd.A) + " -> " + Pt2(bd.B) + "): " + (beyond ? "a region that hangs on it lies " : "a region that does not hang on it reaches ") + Mmf(worst) + " mm " + (beyond ? "behind" : "beyond") + " the line at " + Pt2(spot));
                }
            }
            if (reach.Count > 0 && !(p.Value<bool?>("allow_unfoldable") ?? false))
                throw new ToolFailure("BEND_NOT_FOLDABLE", "A sketched bend folds along the whole line, and in this flat the regions are not cut cleanly by it: " + string.Join("; ", reach.Take(6).ToArray())
                    + (reach.Count > 6 ? "; ... (" + reach.Count + " in all)" : "") + ". This is a flat with CLOSED corners (the neighbouring wall edges lie within the bend zone): it needs corner reliefs wider than the bend zone, or a part built with flanges or Convert to Sheet Metal. (allow_unfoldable = true tries anyway.)");
            foreach (var msg in reach) plan.Warnings.Add("not cleanly foldable: " + msg);

            // ---- the build order: deepest first, one feature per (depth, direction)
            plan.Groups = plan.Bends.GroupBy(b => new { b.Depth, b.Dir }).OrderByDescending(g => g.Key.Depth).ThenBy(g => g.Key.Dir).Select(g => g.ToList()).ToList();
            return plan;
        }

        private static JObject PlanJson(FlatPlan plan)
        {
            var seen = new JObject(); foreach (var kv in plan.Seen) seen[kv.Key] = kv.Value;
            var bends = new JArray();
            for (int i = 0; i < plan.Bends.Count; i++)
            {
                var b = plan.Bends[i];
                bends.Add(new JObject { ["index"] = i, ["from"] = new JArray(b.A[0], b.A[1]), ["to"] = new JArray(b.B[0], b.B[1]), ["length"] = Math.Round(b.Length, 9), ["linetype"] = b.Hidden ? "hidden" : "continuous", ["depth"] = b.Depth, ["direction"] = b.Dir, ["near_region"] = b.Near, ["far_region"] = b.Far });
            }
            var regions = new JArray();
            for (int r = 0; r < plan.Regions.Count; r++) regions.Add(new JObject { ["index"] = r, ["area"] = Math.Round(Math.Abs(RingArea(plan.Regions[r])), 12), ["vertices"] = plan.Regions[r].Count, ["fixed"] = r == plan.Base });
            var groups = new JArray();
            foreach (var g in plan.Groups) groups.Add(new JObject { ["depth"] = g[0].Depth, ["direction"] = g[0].Dir, ["bends"] = new JArray(g.Select(b => (object)plan.Bends.IndexOf(b)).ToArray()) });
            var kinds = new JObject(); foreach (var grp in plan.Outer.Ents.GroupBy(e => e.Kind)) kinds[grp.Key] = grp.Count();
            return new JObject
            {
                ["dxf_path"] = plan.Dxf, ["units"] = plan.Units, ["scale_to_meters"] = plan.Scale, ["entities"] = seen,
                ["outline"] = new JObject { ["pieces"] = plan.Outer.Ents.Count, ["kinds"] = kinds, ["holes"] = plan.Holes.Count, ["box"] = new JArray(plan.Box[0], plan.Box[1], plan.Box[2], plan.Box[3]), ["size"] = new JArray(plan.Box[2] - plan.Box[0], plan.Box[3] - plan.Box[1]) },
                ["bends"] = bends, ["regions"] = regions, ["fixed_point"] = new JArray(plan.Fixed[0], plan.Fixed[1]), ["build_order"] = groups, ["warnings"] = new JArray(plan.Warnings.ToArray())
            };
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        // the tool
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        public ExecutionResponse SheetMetalFromFlat(ToolRequest request)
        {
            var p = request.Params as JObject ?? new JObject();
            if (p.Value<bool?>("plan_only") ?? false)
            {
                // the plan needs no SolidWorks and no document: it only reads the DXF
                try
                {
                    MiterRecipe recipe; string route; ToolFailure refusal; var plan = PlanRoute(p, out recipe, out route, out refusal);
                    var res = route == "miter_flange" ? RecipePlanJson(plan, recipe, refusal) : PlanJson(plan); res["route"] = route; res["plan_only"] = true; res["verified"] = true; int sv = _guard.GetCurrentStateVersion();
                    return new ExecutionResponse { OperationId = request.OperationId, Status = "COMPLETED", Verified = true, StateVersion = sv, CadState = new CadState { StateVersion = sv, ActiveDocument = "", DocumentType = "NONE", Features = new List<string>(), Dimensions = new List<string>() }, ResultGeometry = res };
                }
                catch (ToolFailure f) { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), f.Code, f.Message); }
                catch (Exception ex) { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.GetType().Name + ": " + ex.Message); }
            }
            return Typed(request, true, (doc, prm) => BuildFromFlat(doc, prm));
        }

        // one existing tool, called the way a client would (own operation id, the current state version); a failure stops the build with the step named
        private JObject FlatStep(string tool, JObject prm, string label)
        {
            var req = new ToolRequest { OperationId = Guid.NewGuid().ToString("N"), Tool = tool, StateVersion = _guard.GetCurrentStateVersion(), Params = prm };
            var resp = ToolDispatcher.Dispatch(this, req);
            if (resp == null) throw new ToolFailure("BUILD_STEP_FAILED", label + ": unknown tool '" + tool + "'.");
            if (resp.Status != "COMPLETED") throw new ToolFailure("BUILD_STEP_FAILED", label + ": " + (resp.Error == null ? resp.Status : resp.Error.Code + " - " + resp.Error.Message));
            var g = resp.ResultGeometry as JObject; if (g != null) return g;
            try { return resp.ResultGeometry == null ? new JObject() : JObject.FromObject(resp.ResultGeometry); } catch (Exception) { return new JObject(); }
        }

        private static JArray FlatRecords(FlatLoop loop)
        {
            var a = new JArray();
            foreach (var e in loop.Ents)
            {
                switch (e.Kind)
                {
                    case "line": a.Add(new JObject { ["entity_type"] = "line", ["x1"] = e.A[0], ["y1"] = e.A[1], ["x2"] = e.B[0], ["y2"] = e.B[1] }); break;
                    case "arc": a.Add(new JObject { ["entity_type"] = "arc_center", ["cx"] = e.Cx, ["cy"] = e.Cy, ["x1"] = e.A[0], ["y1"] = e.A[1], ["x2"] = e.B[0], ["y2"] = e.B[1], ["direction"] = 1 }); break;
                    case "circle": a.Add(new JObject { ["entity_type"] = "circle", ["cx"] = e.Cx, ["cy"] = e.Cy, ["radius"] = e.R }); break;
                    case "spline": { var flat = new JArray(); foreach (var q in e.Pts) { flat.Add(q[0]); flat.Add(q[1]); } a.Add(new JObject { ["entity_type"] = "spline", ["points"] = flat }); break; }
                }
            }
            return a;
        }

        // the features of the part that are not in `keep` (top level, newest first), deleted with their children: a build that fails half way leaves the part as it found it
        private int FlatRollback(IModelDoc2 doc, HashSet<string> keep)
        {
            int removed = 0;
            try { if (doc.SketchManager.ActiveSketch != null) doc.SketchManager.InsertSketch(true); } catch (Exception) { }
            foreach (var f in TopFeatures(doc).Where(x => !keep.Contains(x.Name)).Reverse().ToList())
            {
                try
                {
                    doc.ClearSelection2(true);
                    if (f.Select2(false, 0) && doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed | (int)swDeleteSelectionOptions_e.swDelete_Children)) removed++;
                }
                catch (Exception) { }
            }
            doc.ClearSelection2(true); try { doc.EditRebuild3(); } catch (Exception) { }
            return removed;
        }

        // a feature this call made that reports a rebuild error is a failed step (SolidWorks keeps such a feature in the tree, red): the build stops there and is rolled back
        private static void FlatCheckNew(IModelDoc2 doc, HashSet<string> names0, string label)
        {
            foreach (var f in TopFeatures(doc))
            {
                if (names0.Contains(f.Name)) continue;
                int err = FeatureError(f);
                if (err != 0) throw new ToolFailure("BUILD_STEP_FAILED", label + ": SolidWorks built " + f.Name + " but it reports rebuild error " + err + " (a bend radius or flange that does not fit the sheet is the usual cause).");
            }
        }

        private JObject BuildFromFlat(IModelDoc2 doc, JObject p)
        {
            var part = doc as IPartDoc; if (part == null) throw new ToolFailure("WRONG_DOCUMENT_TYPE", "sheet_metal_from_flat needs an active, EMPTY part (open_new_part).");
            var names0 = TopNames(doc);
            try { return BuildFromFlatSteps(doc, part, p, names0); }
            catch (Exception ex)
            {
                if (p.Value<bool?>("keep_on_failure") ?? false) throw;
                bool ours = ex is ToolFailure; string code = ours ? ((ToolFailure)ex).Code : "UNEXPECTED_ERROR", msg = ours ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                bool started = TopFeatures(doc).Any(f => !names0.Contains(f.Name));      // nothing built yet (a refusal before the first step): nothing to take out
                if (!started) throw new ToolFailure(code, msg);
                int removed = FlatRollback(doc, names0);
                // strategy auto: SolidWorks refused a sketched bend of a flat the plan thought foldable (a mitre flat with a wide rip gap does that); a mitre-flange box has its flange recipe as a second way
                if (ours && code == "BUILD_STEP_FAILED" && string.Equals(p.Value<string>("strategy"), "auto", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var p2 = (JObject)p.DeepClone(); p2["strategy"] = "miter_flange";
                        var res2 = BuildFromFlatSteps(doc, part, p2, names0);
                        res2["sketched_bends_failure"] = new JObject { ["code"] = code, ["message"] = msg + " [rolled back: " + removed + " features removed]" };
                        return res2;
                    }
                    catch (Exception ex2)
                    {
                        int removed2 = TopFeatures(doc).Any(f => !names0.Contains(f.Name)) ? FlatRollback(doc, names0) : 0;
                        throw new ToolFailure(code, msg + " [rolled back: " + removed + " features removed] The flange recipe was tried next and failed too: " + (ex2 is ToolFailure ? ex2.Message : ex2.GetType().Name + ": " + ex2.Message) + (removed2 > 0 ? " [rolled back: " + removed2 + " features removed]" : "") + " [the part is empty again]");
                    }
                }
                throw new ToolFailure(code, msg + " [rolled back: " + removed + " features removed, the part is empty again; keep_on_failure = true keeps them for a look]");
            }
        }

        private JObject BuildFromFlatSteps(IModelDoc2 doc, IPartDoc part, JObject p, HashSet<string> names0)
        {
            Func<string, double> need = key => { var v = p.Value<double?>(key); if (v == null || v.Value <= 0) throw new ToolFailure("MISSING_PARAMETER", key + " is required (meters, > 0): the DXF does not say it."); return v.Value; };
            double t = need("thickness"), r = need("bend_radius"); double k = p.Value<double?>("k_factor") ?? 0.5, angle = p.Value<double?>("angle") ?? 90;
            if (k <= 0 || k >= 1) throw new ToolFailure("INVALID_PARAMETER", "k_factor must lie between 0 and 1.");
            if (angle <= 0 || angle >= 180) throw new ToolFailure("INVALID_PARAMETER", "angle must lie between 0 and 180 degrees.");
            MiterRecipe recipe; string route; ToolFailure refusal;
            var plan = PlanRoute(p, out recipe, out route, out refusal);      // everything that can be wrong with the DXF is refused here, before the first feature
            if (route == "miter_flange") return BuildMiterRecipe(doc, part, p, names0, plan, recipe, refusal);
            var bodies0 = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
            if ((bodies0 != null && bodies0.Length > 0) || TopFeatures(doc).Any(f => f.GetTypeName2() == "ProfileFeature")) throw new ToolFailure("NOT_EMPTY_PART", "The active part is not empty: open a new part for the flat (open_new_part).");
            const string plane = "Front Plane";      // the flat is drawn on the Front Plane: the DXF x / y are the plane's x / y, the sheet folds toward +Z / -Z

            var steps = new JArray();
            // ---- the outline = the base flange (the holes are the inner loops of the same sketch)
            var records = FlatRecords(plan.Outer); foreach (var h in plan.Holes) foreach (var rec in FlatRecords(h)) records.Add(rec);
            FlatStep("create_sketch", new JObject { ["plane"] = plane }, "outline sketch");
            FlatStep("add_sketch_entities", new JObject { ["segments"] = records }, "outline entities");
            FlatStep("sheet_metal_feature", new JObject { ["feature_type"] = "base_flange", ["thickness"] = t, ["bend_radius"] = r, ["k_factor"] = k }, "base flange"); FlatCheckNew(doc, names0, "base flange");
            steps.Add("base flange from the outline (" + plan.Outer.Ents.Count + " pieces, " + plan.Holes.Count + " holes)");
            var bs = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; var box = bs == null || bs.Length == 0 ? null : BoxOf((IBody2)bs[0]);
            if (box == null) throw new ToolFailure("BUILD_STEP_FAILED", "The base flange made no solid body.");
            double fz = (Math.Abs(box[5]) < 1e-5 || Math.Abs(box[2]) < 1e-5) ? 0 : box[5];      // the face of the sheet that lies in the sketch plane (or its top)

            // ---- the bends, deepest first; one sketched bend per (depth, direction)
            int made = 0;
            foreach (var g in plan.Groups)
            {
                FlatStep("create_sketch", new JObject { ["plane"] = plane }, "bend sketch (depth " + g[0].Depth + ")");
                var lines = new JArray(); foreach (var b in g) lines.Add(new JObject { ["entity_type"] = "line", ["x1"] = b.A[0], ["y1"] = b.A[1], ["x2"] = b.B[0], ["y2"] = b.B[1] });
                FlatStep("add_sketch_entities", new JObject { ["segments"] = lines }, "bend lines (depth " + g[0].Depth + ")");
                FlatStep("sheet_metal_feature", new JObject { ["feature_type"] = "sketched_bend", ["angle"] = angle, ["bend_radius"] = r, ["bend_position"] = "centerline", ["fixed_x"] = plan.Fixed[0], ["fixed_y"] = plan.Fixed[1], ["fixed_z"] = fz, ["flip"] = g[0].Dir == "down" },
                    "sketched bend, depth " + g[0].Depth + ", " + g[0].Dir + " (" + g.Count + " lines)");
                FlatCheckNew(doc, names0, "sketched bend, depth " + g[0].Depth + ", " + g[0].Dir);
                made += g.Count; steps.Add("sketched bend depth " + g[0].Depth + " " + g[0].Dir + ": " + g.Count + " line" + (g.Count == 1 ? "" : "s"));
            }
            doc.EditRebuild3();

            // ---- proof: the part's own flat pattern has the size of the DXF outline, and it has all the bends
            var problems = new JArray(); var res = new JObject { ["route"] = "sketched_bends", ["plan"] = PlanJson(plan), ["steps"] = steps, ["thickness"] = t, ["bend_radius"] = r, ["k_factor"] = k, ["angle"] = angle };
            var created = TopFeatures(doc).Where(f => !names0.Contains(f.Name) && f.GetTypeName2() != "ProfileFeature").Select(f => f.Name).ToList(); res["features"] = new JArray(created.ToArray());
            foreach (var f in TopFeatures(doc)) { int err = FeatureError(f); if (err != 0 && !names0.Contains(f.Name)) problems.Add("feature " + f.Name + " reports rebuild error " + err); }
            int bendsIn = OneBendFeatures(doc).Count; res["bends"] = bendsIn; res["bends_expected"] = plan.Bends.Count;
            if (bendsIn != plan.Bends.Count) problems.Add("the part has " + bendsIn + " bends, the DXF has " + plan.Bends.Count + " bend lines");
            var fl = FlatStep("flat_pattern_control", new JObject(), "flat pattern");
            var size = fl["flat"] == null || fl["flat"]["size"] == null ? null : fl["flat"]["size"].Select(x => x.Value<double>()).OrderByDescending(x => x).ToArray();
            if (size != null)
            {
                var want = new[] { plan.Box[2] - plan.Box[0], plan.Box[3] - plan.Box[1] }.OrderByDescending(x => x).ToArray();
                double tolFlat = plan.SplinesOrArcs ? 2e-4 : 1e-4;      // the body box is conservative by a few microns where curves reach the outside
                double dev = Math.Max(Math.Abs(size[0] - want[0]), Math.Abs(size[1] - want[1]));
                res["flat_size"] = new JArray(size[0], size[1], size[2]); res["dxf_size"] = new JArray(want[0], want[1]); res["flat_deviation"] = Math.Round(dev, 9);
                if (dev > tolFlat) problems.Add("the part's flat pattern is " + Mmf(size[0]) + " x " + Mmf(size[1]) + " mm, the DXF outline " + Mmf(want[0]) + " x " + Mmf(want[1]) + " mm");
            }
            else problems.Add("the flat pattern could not be measured");
            var folded = SolidSize(part); if (folded != null) res["folded_size"] = new JArray(folded.Select(v => (object)Math.Round(v, 9)).ToArray());
            if (problems.Count > 0) res["problems"] = problems;
            res["verified"] = problems.Count == 0;
            return res;
        }
    }
}
