// WINDOW LAYOUT (changes only where the ACTIVE document's window sits, nothing in the model): arg "shift" = restore the window and move it 300 px right and make it smaller, so the graphics area
// no longer starts at x = 0 (used to prove that picks follow the window); arg "max" = maximise every open document window again. Prints each document's window state afterwards.
using System;
using System.Text;
using SolidWorks.Interop.sldworks;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var act = sw.ActiveDoc as ModelDoc2;
        if (arg == "shift" && act != null)
        {
            var v0 = act.GetFirstModelView() as ModelView;
            if (v0 != null) { v0.FrameState = 0; v0.FrameLeft = 300; v0.FrameTop = 60; v0.FrameWidth = 1200; v0.FrameHeight = 700; }
        }
        var sb = new StringBuilder();
        var d = sw.GetFirstDocument() as ModelDoc2;
        while (d != null)
        {
            var v = d.GetFirstModelView() as ModelView;
            if (v != null)
            {
                if (arg == "max") v.FrameState = 1;
                sb.Append(d.GetTitle() + " state " + v.FrameState + " frame " + v.FrameLeft + "," + v.FrameTop + " " + v.FrameWidth + "x" + v.FrameHeight + " ;; ");
            }
            d = d.GetNext() as ModelDoc2;
        }
        return sb.ToString();
    }
}
