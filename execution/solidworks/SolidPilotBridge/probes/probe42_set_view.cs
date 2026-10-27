// Probe 42: put the active model view into a named orientation (arg: *Front *Back *Left *Right *Top *Bottom *Isometric) and zoom to fit. Used by the tests to prove that the tools' entity picking does not
// depend on the current view (SelectByID2 does: in an unlucky orientation it returned another edge, or missed).
using System;
using SolidWorks.Interop.sldworks;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var doc = (ModelDoc2)sw.ActiveDoc; if (doc == null) return "no active document";
        string name = string.IsNullOrEmpty(arg) ? "*Isometric" : arg;
        doc.ShowNamedView2(name, -1); doc.ViewZoomtofit2(); doc.GraphicsRedraw2();      // void calls: there is no status to report, the tests check the effect
        return "view " + name + " -> True";
    }
}
