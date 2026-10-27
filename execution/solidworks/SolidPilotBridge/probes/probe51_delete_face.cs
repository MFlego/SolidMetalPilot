// Probe 51: Delete Face on the planar face of the first SOLID body that is farthest along an axis, turning the box into an open SURFACE body (the base shape of a Convert to Sheet Metal part).
// arg = "z+" (default: the face with the largest z), "z-", "x+", "x-", "y+", "y-". IModelDoc.InsertDeleteFace() is only on the old interface: late-bound through IDispatch.
using System;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; var part = (PartDoc)doc; string a = string.IsNullOrEmpty(arg) ? "z+" : arg.Trim().ToLowerInvariant();
        int axis = a[0] == 'x' ? 0 : a[0] == 'y' ? 1 : 2; bool max = a.Length < 2 || a[1] == '+';
        var bodies = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false); if (bodies == null || bodies.Length == 0) return "no solid body";
        Face2 pick = null; double best = max ? double.MinValue : double.MaxValue;
        foreach (Face2 f in (object[])((Body2)bodies[0]).GetFaces())
        {
            var s = (Surface)f.GetSurface(); if (!s.IsPlane()) continue; var bx = (double[])f.GetBox();
            if (Math.Abs(bx[axis] - bx[axis + 3]) > 1e-6) continue;                 // planar and perpendicular to the axis
            double v = bx[axis]; if ((max && v > best) || (!max && v < best)) { best = v; pick = f; }
        }
        if (pick == null) return "no planar face perpendicular to the axis";
        doc.ClearSelection2(true); ((Entity)pick).Select4(false, null);
        ((object)doc).GetType().InvokeMember("InsertDeleteFace", BindingFlags.InvokeMethod, null, doc, null);
        doc.ClearSelection2(true); doc.EditRebuild3();
        var sheets = (object[])part.GetBodies2((int)swBodyType_e.swSheetBody, false); var solids = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false);
        return "deleted the face at " + a + " (" + (best * 1000).ToString("F2") + " mm); solid bodies " + (solids == null ? 0 : solids.Length) + ", surface bodies " + (sheets == null ? 0 : sheets.Length);
    }
}
