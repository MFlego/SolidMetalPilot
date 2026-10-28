// Probe 77: the TAB AND SLOT feature through CreateDefinition(swFmTabAndSlot = 88) -> ITabAndSlotFeatureData -> SelectionAddNewGroup() -> ITabAndSlotGroupData, on a scratch two-body part: a plate (100 x 60 x 1.5 mm,
// z -1.5 .. 0) and a wall standing on it (80 x 1.5 x 30 mm, x -40 .. 40, y -0.75 .. 0.75, z 0 .. 30) made as a SEPARATE body (FeatureExtrusion3 with Merge = false). The wall's bottom edge carries the tabs, the plate's
// top face gets the slots. arg = "mode;key=value;..."   modes: build (the two bodies only) | tab (build + the feature) | info (list the bodies, edges and faces of the open part).
// Progress goes to the file given as log=<path> after every step (a crash of SolidWorks loses the returned text, not the file).
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
    static string F(double v) { return (v * 1000).ToString("F3", CultureInfo.InvariantCulture); }
    static List<Feature> Top(ModelDoc2 doc) { var l = new List<Feature>(); var f = (Feature)doc.FirstFeature(); while (f != null) { l.Add(f); f = (Feature)f.GetNextFeature(); } return l; }
    static List<Body2> Bodies(ModelDoc2 doc) { var bs = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; return bs == null ? new List<Body2>() : bs.Cast<Body2>().ToList(); }
    static string BoxOf(Body2 b) { var x = (double[])b.GetBodyBox(); return "x " + F(x[0]) + ".." + F(x[3]) + ", y " + F(x[1]) + ".." + F(x[4]) + ", z " + F(x[2]) + ".." + F(x[5]) + " mm, " + b.GetFaceCount() + " faces"; }

    // a rectangle on a plane, extruded as its own body; returns the feature
    static Feature Slab(ModelDoc2 doc, string plane, double x1, double y1, double x2, double y2, double depth, bool reverse, bool merge)
    {
        doc.ClearSelection2(true); doc.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); doc.SketchManager.InsertSketch(true);
        doc.SketchManager.CreateCornerRectangle(x1, y1, 0, x2, y2, 0);
        string name = ((Feature)((Sketch)doc.SketchManager.ActiveSketch)).Name; doc.SketchManager.InsertSketch(true);
        doc.ClearSelection2(true); doc.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0);
        return (Feature)doc.FeatureManager.FeatureExtrusion3(true, false, reverse, 0, 0, depth, 0, false, false, false, false, 0, 0, false, false, false, false, merge, true, true, 0, 0, false);
    }

    static Edge FindEdge(Body2 body, double x1, double y1, double z1, double x2, double y2, double z2, double tol)
    {
        foreach (var o in (object[])body.GetEdges())
        {
            var e = (Edge)o; var c = e.GetCurve() as Curve; if (c == null || !c.IsLine()) continue;
            var sv = (Vertex)e.GetStartVertex(); var ev = (Vertex)e.GetEndVertex(); if (sv == null || ev == null) continue;
            var p = (double[])sv.GetPoint(); var q = (double[])ev.GetPoint();
            Func<double[], double, double, double, bool> at = (v, x, y, z) => Math.Abs(v[0] - x) < tol && Math.Abs(v[1] - y) < tol && Math.Abs(v[2] - z) < tol;
            if ((at(p, x1, y1, z1) && at(q, x2, y2, z2)) || (at(p, x2, y2, z2) && at(q, x1, y1, z1))) return e;
        }
        return null;
    }
    static Face2 FindFace(Body2 body, int axis, double sign, double at, double tol)       // the planar face with normal sign * axis at the coordinate "at"
    {
        foreach (var o in (object[])body.GetFaces())
        {
            var f = (Face2)o; var s = f.GetSurface() as Surface; if (s == null || !s.IsPlane()) continue; var n = (double[])f.Normal; var bx = (double[])f.GetBox();
            if (Math.Abs(n[axis] - sign) < 1e-6 && Math.Abs(bx[axis] - at) < tol && Math.Abs(bx[3 + axis] - at) < tol) return f;
        }
        return null;
    }

    public static string Run(SldWorks sw, string arg)
    {
        sb = new StringBuilder(); var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var parts = (arg ?? "build").Split(';'); string mode = parts[0].Trim().ToLowerInvariant();
        foreach (var p in parts) { int i = p.IndexOf('='); if (i > 0) a[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim(); }
        logPath = a.ContainsKey("log") ? a["log"] : null;
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document";
        var fm = doc.FeatureManager; L("mode " + mode + "; bodies now " + Bodies(doc).Count);

        if (mode == "build" || mode == "tab")
        {
            if (Bodies(doc).Count == 0)
            {
                var plate = Slab(doc, "Front Plane", -0.05, -0.03, 0.05, 0.03, 0.0015, true, true); L("plate: " + (plate == null ? "null" : plate.Name));
                var wall = Slab(doc, "Front Plane", -0.04, -0.00075, 0.04, 0.00075, 0.03, false, false); L("wall: " + (wall == null ? "null" : wall.Name));
            }
            foreach (var b in Bodies(doc)) L("body: " + BoxOf(b));
        }
        if (mode == "info")
        {
            foreach (var b in Bodies(doc))
            {
                L("body: " + BoxOf(b));
                foreach (var o in (object[])b.GetEdges())
                {
                    var e = (Edge)o; var sv = e.GetStartVertex() as Vertex; var ev = e.GetEndVertex() as Vertex; if (sv == null || ev == null) continue;
                    var p = (double[])sv.GetPoint(); var q = (double[])ev.GetPoint(); L("   edge (" + F(p[0]) + "," + F(p[1]) + "," + F(p[2]) + ") -> (" + F(q[0]) + "," + F(q[1]) + "," + F(q[2]) + ")");
                }
            }
        }
        if (mode == "tabsweep")
        {
            // every cheap combination of the inputs that could be what CreateFeature wants (NO AccessSelections and NO vertex reference points - both killed SolidWorks): the tab edge (wall bottom edge on +y / -y),
            // the tab face (wall +y side, wall -y side, wall bottom), the slot face (plate top), the height type 0 / 1 / 2, the offset flag; stops at the first feature
            var bodies = Bodies(doc); if (bodies.Count != 2) return sb + "need two bodies";
            Body2 plateB = bodies.OrderBy(b => ((double[])b.GetBodyBox())[2]).First(), wallB = bodies.OrderByDescending(b => ((double[])b.GetBodyBox())[5]).First();
            var edges = new[] { FindEdge(wallB, -0.04, 0.00075, 0, 0.04, 0.00075, 0, 1e-6), FindEdge(wallB, -0.04, -0.00075, 0, 0.04, -0.00075, 0, 1e-6) };
            var tabFaces = new[] { FindFace(wallB, 1, 1, 0.00075, 1e-6), FindFace(wallB, 1, -1, -0.00075, 1e-6), FindFace(wallB, 2, -1, 0, 1e-6) };
            var slotFaces = new[] { FindFace(plateB, 2, 1, 0, 1e-6), FindFace(plateB, 2, -1, -0.0015, 1e-6) };
            L("edges " + edges.Count(x => x != null) + ", tab faces " + tabFaces.Count(x => x != null) + ", slot faces " + slotFaces.Count(x => x != null));
            int tried = 0; bool built = false;
            for (int ei = 0; ei < 2 && !built; ei++)
                for (int ti = 0; ti < 3 && !built; ti++)
                    for (int si = 0; si < 2 && !built; si++)
                        for (int ht = 0; ht < 3 && !built; ht++)
                            foreach (bool offs in new[] { true, false })
                            {
                                if (built) break;
                                if (edges[ei] == null || tabFaces[ti] == null || slotFaces[si] == null) continue;
                                var before = new HashSet<string>(Top(doc).Select(x => x.Name)); Feature f = null; string thrown = null; tried++;
                                try
                                {
                                    var def = fm.CreateDefinition(88); var fd = def as ITabAndSlotFeatureData; var g = fd.SelectionAddNewGroup() as ITabAndSlotGroupData;
                                    g.SelectionTabEdge = edges[ei]; g.SelectionSlotFace = slotFaces[si]; g.TabFace = tabFaces[ti];
                                    g.TabLength = 0.01; g.TabThickness = 0.0015; g.TabHeightType = ht; g.TabHeightValue = 0.0015; g.SpacingType = 0; g.SpacingNumberOfInstances = 3;
                                    g.Offset = offs; if (offs) { g.D1OffsetFromStart = 0.005; g.D2OffsetFromEnd = 0.005; } g.SlotClearance = 0.0002; g.TabEdgesType = 0;
                                    f = fm.CreateFeature(def) as Feature;
                                }
                                catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
                                if (thrown != null) L("edge " + ei + " tabface " + ti + " slotface " + si + " height type " + ht + " offset " + offs + ": THREW " + thrown);
                                if (f != null) { built = true; L("BUILT with edge " + ei + ", tab face " + ti + ", slot face " + si + ", height type " + ht + ", offset " + offs + ": " + f.Name + " [" + f.GetTypeName2() + "]"); foreach (var b in Bodies(doc)) L("body: " + BoxOf(b)); }
                            }
            L("combinations tried: " + tried + (built ? "" : "; none built a feature"));
            return sb.ToString();
        }
        if (mode == "tab")
        {
            var bodies = Bodies(doc); if (bodies.Count != 2) return sb + "need two bodies";
            Body2 plateB = bodies.OrderBy(b => ((double[])b.GetBodyBox())[2]).First(), wallB = bodies.OrderByDescending(b => ((double[])b.GetBodyBox())[5]).First();
            var edge = FindEdge(wallB, -0.04, 0.00075, 0, 0.04, 0.00075, 0, 1e-6); var slotFace = FindFace(plateB, 2, 1, 0, 1e-6);
            var tabFace = (a.ContainsKey("tabface") && a["tabface"] == "side") ? FindFace(wallB, 1, 1, 0.00075, 1e-6) : FindFace(wallB, 2, -1, 0, 1e-6);       // default: the wall's BOTTOM face
            L("tab edge " + (edge != null) + ", slot face (plate top) " + (slotFace != null) + ", tab face " + (tabFace != null));
            if (edge == null || slotFace == null || tabFace == null) return sb.ToString();
            var before = new HashSet<string>(Top(doc).Select(x => x.Name));
            var def = fm.CreateDefinition(88); var fd = def as ITabAndSlotFeatureData; L("definition " + (def == null ? "null" : "ok"));
            if (fd == null) return sb.ToString();
            var g = fd.SelectionAddNewGroup() as ITabAndSlotGroupData; if (g == null) return sb.ToString();
            Func<string, Action, bool> set = (label, act) => { try { act(); L("set " + label + " ok"); return true; } catch (Exception e) { L("set " + label + " THREW " + (e.InnerException != null ? e.InnerException : e).Message); return false; } };
            set("TabEdge", () => g.SelectionTabEdge = edge); set("SlotFace", () => g.SelectionSlotFace = slotFace); set("TabFace", () => g.TabFace = tabFace);
            set("TabLength", () => g.TabLength = N(a, "len", 0.01)); set("TabThickness", () => g.TabThickness = N(a, "tt", 0.0015));
            set("TabHeightType", () => g.TabHeightType = (int)N(a, "ht", 0)); set("TabHeightValue", () => g.TabHeightValue = N(a, "h", 0.0015)); set("TabOffsetValue", () => g.TabOffsetValue = N(a, "toff", 0.0));
            if (N(a, "order", 0) != 0) { set("Spacing", () => g.Spacing = N(a, "sp", 0.02)); set("SpacingType", () => g.SpacingType = (int)N(a, "st", 0)); if (N(a, "skipn", 0) == 0) set("SpacingNumberOfInstances", () => g.SpacingNumberOfInstances = (int)N(a, "n", 3)); }
            else { set("SpacingType", () => g.SpacingType = (int)N(a, "st", 0)); if (N(a, "skipn", 0) == 0) set("SpacingNumberOfInstances", () => g.SpacingNumberOfInstances = (int)N(a, "n", 3)); set("Spacing", () => g.Spacing = N(a, "sp", 0.02)); }
            set("Offset", () => g.Offset = N(a, "offs", 1) != 0); set("D1OffsetFromStart", () => g.D1OffsetFromStart = N(a, "d1", 0.005)); set("D2OffsetFromEnd", () => g.D2OffsetFromEnd = N(a, "d2", 0.005));
            set("SlotClearance", () => g.SlotClearance = N(a, "clr", 0.0002)); set("TabEdgesType", () => g.TabEdgesType = (int)N(a, "et", 0)); if (a.ContainsKey("fil")) set("TabFilletEdgeTreatmentValue", () => g.TabFilletEdgeTreatmentValue = N(a, "fil", 0.0005)); if (a.ContainsKey("cham")) set("TabChamferEdgeTreatmentValue", () => g.TabChamferEdgeTreatmentValue = N(a, "cham", 0.0005)); set("TabReverseDirection", () => g.TabReverseDirection = N(a, "rev", 0) != 0);
            L("calling CreateFeature");
            Feature f = null; string thrown = null;
            try { f = fm.CreateFeature(def) as Feature; } catch (Exception e) { thrown = (e.InnerException != null ? e.InnerException : e).Message; }
            L("CreateFeature -> " + (f == null ? "null" : f.Name + " [" + f.GetTypeName2() + "]") + (thrown != null ? "; THREW " + thrown : ""));
            var fresh = Top(doc).Where(x => !before.Contains(x.Name)).ToList(); L("new features: " + (fresh.Count == 0 ? "none" : string.Join(", ", fresh.Select(x => x.Name + "[" + x.GetTypeName2() + "]").ToArray())));
            if (f != null) { bool w; L("feature error code " + f.GetErrorCode2(out w) + (w ? " (warning)" : "")); }
            foreach (var b in Bodies(doc)) L("body: " + BoxOf(b));
            if (f != null && N(a, "readback", 0) != 0)
            {
                // the definition of the CREATED feature (the supported use of AccessSelections), step by step in the log
                L("reading the definition back");
                var d2 = f.GetDefinition() as ITabAndSlotFeatureData; L("GetDefinition -> " + (d2 != null));
                if (d2 != null)
                {
                    bool acc = d2.AccessSelections(doc, null); L("AccessSelections -> " + acc);
                    var groups = d2.SelectionGetGroups() as object[]; L("groups: " + (groups == null ? "null" : groups.Length.ToString()));
                    if (groups != null)
                        foreach (var go in groups)
                        {
                            var gd = go as ITabAndSlotGroupData; if (gd == null) { L("group is not ITabAndSlotGroupData: " + (go == null ? "null" : go.GetType().Name)); continue; }
                            L("group: TabLength " + F(gd.TabLength) + ", TabThickness " + F(gd.TabThickness) + ", HeightType " + gd.TabHeightType + ", HeightValue " + F(gd.TabHeightValue) + ", TabOffsetValue " + F(gd.TabOffsetValue) + ", SpacingType " + gd.SpacingType + ", N " + gd.SpacingNumberOfInstances + ", Spacing " + F(gd.Spacing) + ", Offset " + gd.Offset + ", D1 " + F(gd.D1OffsetFromStart) + ", D2 " + F(gd.D2OffsetFromEnd) + ", SlotClearance " + F(gd.SlotClearance) + ", EdgesType " + gd.TabEdgesType + ", Reverse " + gd.TabReverseDirection);
                        }
                    d2.ReleaseSelectionAccess(); L("ReleaseSelectionAccess done");
                }
            }
        }
        return sb.ToString();
    }
    static double N(Dictionary<string, string> a, string key, double dflt) { string v; return a.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : dflt; }
}
