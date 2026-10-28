// Probe 71: the swCommands_e ids (for ISldWorks.RunCommand) whose names contain any of the given fragments, e.g. arg = "Jog,Gusset,CrossBreak,TabAndSlot,SweptFlange,LoftedBend". The swcommands interop
// is not referenced by the probe compiler, so it is loaded from the SolidWorks installation (api\redist) and read by reflection.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;

public static class Script
{
    public static string Run(SldWorks sw, string arg)
    {
        var frags = (arg ?? "Jog").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); var sb = new StringBuilder();
        string dll = Path.Combine(sw.GetExecutablePath(), "SolidWorks.Interop.swcommands.dll");      // GetExecutablePath is the folder that holds SLDWORKS.exe
        if (!File.Exists(dll)) return "not found: " + dll;
        var asm = Assembly.LoadFrom(dll); var t = asm.GetType("SolidWorks.Interop.swcommands.swCommands_e"); if (t == null) return "swCommands_e not in " + dll;
        foreach (var name in Enum.GetNames(t))
            if (frags.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) sb.Append(name + " = " + Convert.ToInt32(Enum.Parse(t, name)) + " ;; ");
        return sb.ToString();
    }
}
