// Probe 36: close ALL FOUR corners of the tray through the typed API, then set the corner definition the way the DFM rules want it (Overlap, ratio 1, gap 0.0762 mm = 0.003 in).
// For corner (sx, sy) the face to extend is the end face of the +-x flange wall at y = sy * 27 mm, normal (0, sy, 0). Corners that already have a Closed Corner are skipped (by feature count of CornerFeat).
// arg: gap in mm (default 0.0762)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static Face2 FindEndFace(ModelDoc2 doc, int sx, int sy)
    {
        var body = (Body2)((object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false))[0]; Face2 best = null; double bestD = 1e9;
        foreach (Face2 f in (object[])body.GetFaces())
        {
            var s = (Surface)f.GetSurface(); if (!s.IsPlane()) continue; double a = f.GetArea() * 1e6; if (a < 15 || a > 80) continue;
            var n = (double[])f.Normal; if (Math.Abs(n[0]) > 0.2 || Math.Abs(n[1] - sy) > 0.2) continue;          // normal points toward the neighbouring (y) flange
            var bx = (double[])f.GetBox(); double cx = (bx[0] + bx[3]) / 2 * 1000, cy = (bx[1] + bx[4]) / 2 * 1000;
            double d = Math.Abs(cx - sx * 49.25) + Math.Abs(cy - sy * 27.0); if (d < bestD) { bestD = d; best = f; }
        }
        return bestD < 3 ? best : null;
    }
    static int CornerFeats(ModelDoc2 doc) { int n = 0; var f = (Feature)doc.FirstFeature(); while (f != null) { if (f.GetTypeName2() == "CornerFeat") n++; f = (Feature)f.GetNextFeature(); } return n; }
    static string Geo(ModelDoc2 doc) { var b = (Body2)((object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false))[0]; return "faces " + b.GetFaceCount() + " edges " + b.GetEdgeCount(); }

    public static string Run(SldWorks sw, string arg)
    {
        double gapMm; if (!double.TryParse(arg, out gapMm)) gapMm = 0.0762;
        var doc = (ModelDoc2)sw.ActiveDoc; var sm = (SelectionMgr)doc.SelectionManager; var sb = new StringBuilder();
        sb.AppendLine("start: " + Geo(doc) + ", closed corners " + CornerFeats(doc));
        int[][] corners = { new[] { -1, -1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { 1, 1 } };
        for (int k = 0; k < 4; k++)
        {
            int sx = corners[k][0], sy = corners[k][1]; var face = FindEndFace(doc, sx, sy);
            if (face == null) { sb.AppendLine("corner (" + sx + "," + sy + "): no end face left (already closed?)"); continue; }
            int before = CornerFeats(doc); doc.ClearSelection2(true); ((Entity)face).Select4(false, sm.CreateSelectData());
            try { doc.InsertSheetMetalClosedCorner(); } catch (Exception e) { sb.AppendLine("corner (" + sx + "," + sy + "): THREW " + e.Message); continue; }
            sb.AppendLine("corner (" + sx + "," + sy + "): closed corners " + before + " -> " + CornerFeats(doc) + "   " + Geo(doc));
        }
        doc.ClearSelection2(true);

        // now the DFM settings on every Closed Corner feature
        var f = (Feature)doc.FirstFeature();
        while (f != null)
        {
            if (f.GetTypeName2() == "CornerFeat")
            {
                var d = (IClosedCornerFeatureData)f.GetDefinition(); bool acc = d.AccessSelections(doc, null);
                string was = "type " + d.CornerType + " gap " + (d.GapDistance * 1000).ToString("F4") + " mm ratio " + d.OverlapUnderlapRatio + " openBend " + d.OpenBendRegion;
                d.CornerType = (int)swClosedCornerTypes_e.swClosedCornerTypeOverlap; d.GapDistance = gapMm / 1000.0; d.OverlapUnderlapRatio = 1.0; d.OpenBendRegion = false;
                bool ok = f.ModifyDefinition(d, doc, null); try { d.ReleaseSelectionAccess(); } catch { }
                var r = (IClosedCornerFeatureData)f.GetDefinition(); bool w; int err = 0; try { err = f.GetErrorCode2(out w); } catch { }
                sb.AppendLine(f.Name + ": was [" + was + "] -> ModifyDefinition " + ok + " -> now type " + r.CornerType + " gap " + (r.GapDistance * 1000).ToString("F4") + " mm ratio " + r.OverlapUnderlapRatio + (err != 0 ? "  ERROR " + err : "  ok"));
            }
            f = (Feature)f.GetNextFeature();
        }
        sb.AppendLine("end: " + Geo(doc) + ", closed corners " + CornerFeats(doc));
        return sb.ToString();
    }
}
