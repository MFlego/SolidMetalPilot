// Probe 52: drive a PropertyManager control WITHOUT the tool gate - for the day a PropertyManager is stuck behind something the gate refuses (a "Rebuild Errors" bubble is a visible top-level WPF window,
// which the dialog scan counts as a modal dialog). Goes through the loaded tools assembly's internal PmUi by reflection (newest generation).
// arg = "<control name>|<op>[|<value>]", op = expand | collapse | focus | toggle | invoke | set_text | select_index; default "OnCancelButton|invoke"
using System;
using System.Linq;
using System.Reflection;
using SolidWorks.Interop.sldworks;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var parts = (string.IsNullOrEmpty(arg) ? "OnCancelButton|invoke" : arg).Split('|'); string name = parts[0], op = parts.Length > 1 ? parts[1] : "invoke", value = parts.Length > 2 ? parts[2] : null;
        var asm = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "SolidPilotBridge.Tools").LastOrDefault(); if (asm == null) return "tools assembly not loaded";
        var pm = asm.GetType("SolidworksExecution.Services.PmUi"); if (pm == null) return "PmUi not found (a tools generation older than the UI layer?)";
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var root = pm.GetMethod("FindRoot", flags).Invoke(null, new object[] { sw, "SolidToSheetMetalDVE" }); if (root == null) return "no Convert to Sheet Metal PropertyManager is open";
        var visual = root.GetType().GetProperty("Visual").GetValue(root, null);
        var findArgs = new object[] { visual, name, 0 }; var el = pm.GetMethod("Find", flags).Invoke(null, findArgs); if (el == null) return "no control named '" + name + "'";
        return (string)pm.GetMethod("Act", flags).Invoke(null, new object[] { el, op, value });
    }
}
