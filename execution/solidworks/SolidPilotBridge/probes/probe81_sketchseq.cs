// Probe 81: why does a SECOND sketch on a STANDARD plane not stay active when it is made with the create_sketch tool (a second sketch on a reference plane does)? The exact sequence of the tool, natively:
// sketch 1 on the first plane with a line; then (as the tool does) exit it with InsertSketch(true), ClearSelection2, SelectByID2(second plane), InsertSketch(true); report ActiveSketch after every step.
// arg = "first;second" plane names (default "Top Plane;Front Plane")
using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var sb = new StringBuilder(); Action<string> L = s => sb.Append(s).Append(" ;; ");
        var parts = (arg ?? "Top Plane;Front Plane").Split(';'); var doc = (ModelDoc2)sw.ActiveDoc; var sm = doc.SketchManager;
        Func<string> act = () => { var a = sm.ActiveSketch as Feature; return a == null ? "none" : a.Name; };
        if (parts[0] == "check")      // only report the state SolidWorks is in right now (after tool calls of the caller)
        {
            L("active sketch: " + act() + "; selection count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1) + "; AddToDB " + sm.AddToDB + "; AutoInference " + sm.AutoInference + "; InteractiveMode " + sm.GetType().Name);
            var f0 = (Feature)doc.FirstFeature(); string nm = ""; while (f0 != null) { if (f0.GetTypeName2() == "ProfileFeature") nm += f0.Name + " "; f0 = (Feature)f0.GetNextFeature(); }
            L("sketches in the tree: " + nm); return sb.ToString();
        }
        doc.ClearSelection2(true); bool s1 = doc.Extension.SelectByID2(parts[0], "PLANE", 0, 0, 0, false, 0, null, 0); sm.InsertSketch(true); L("sketch 1 on " + parts[0] + ": selected " + s1 + ", active " + act());
        sm.CreateLine(0, 0, 0, 0.05, 0, 0); L("after a line: active " + act());
        sm.InsertSketch(true); L("after InsertSketch(true) (exit): active " + act());
        doc.ClearSelection2(true); bool s2 = doc.Extension.SelectByID2(parts[1], "PLANE", 0, 0, 0, false, 0, null, 0); L("selected the second plane: " + s2 + "; selection count " + ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));
        sm.InsertSketch(true); L("after InsertSketch(true) on " + parts[1] + ": active " + act());
        var f = (Feature)doc.FirstFeature(); string names = ""; while (f != null) { names += f.Name + "[" + f.GetTypeName2() + "] "; f = (Feature)f.GetNextFeature(); }
        L("features: " + names);
        return sb.ToString();
    }
}
