// Probe 76: list the members (properties with get/set, methods with their parameters) of the SolidWorks interop interfaces - and the values of the swconst enums - whose name contains one of the words in arg
// (separated by ',' ; case-insensitive), e.g. "TabAndSlot,SweptFlange". Reads the interop assemblies only - nothing in SolidWorks is touched.
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class Script
{
    static string Sig(MethodInfo m)
    {
        return m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + p.ParameterType.Name.TrimEnd('&') + " " + p.Name)) + ")";
    }

    public static string Run(SldWorks sw, string arg)
    {
        var words = (arg ?? "TabAndSlot").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        var sb = new StringBuilder();
        var asm = typeof(SldWorks).Assembly;
        if (words.Length > 0 && words[0].StartsWith("e:"))      // "e:Move,Corner": every swconst enum VALUE whose name contains one of the words (enum.value = number)
        {
            words[0] = words[0].Substring(2);
            foreach (var t in typeof(swBodyType_e).Assembly.GetTypes().Where(x => x.IsEnum).OrderBy(x => x.Name))
                foreach (var n in Enum.GetNames(t).Where(n => words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)))
                    sb.Append(t.Name).Append('.').Append(n).Append('=').Append(Convert.ToInt64(Enum.Parse(t, n))).Append(" ;; ");
            return sb.ToString();
        }
        if (words.Length > 0 && words[0].StartsWith("m:"))      // "m:Tab,Slot": every METHOD or PROPERTY of every interface whose name contains one of the words
        {
            words[0] = words[0].Substring(2);
            foreach (var t in asm.GetTypes().Where(x => x.IsInterface).OrderBy(x => x.Name))
                foreach (var m in t.GetMethods().Where(m => words.Any(w => m.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)))
                    sb.Append(t.Name).Append('.').Append(Sig(m)).Append(" ;; ");
            return sb.ToString();
        }
        foreach (var t in asm.GetTypes().Where(x => x.IsInterface && words.Any(w => x.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(x => x.Name))
        {
            sb.Append("## ").Append(t.FullName).Append(" ;; ");
            var props = t.GetProperties().Select(p => p.PropertyType.Name + " " + p.Name + (p.CanRead && p.CanWrite ? " {get;set}" : p.CanWrite ? " {set}" : " {get}"));
            var propNames = new System.Collections.Generic.HashSet<string>(t.GetProperties().SelectMany(p => new[] { "get_" + p.Name, "set_" + p.Name }));
            foreach (var s in props) sb.Append("P ").Append(s).Append(" ;; ");
            foreach (var m in t.GetMethods().Where(m => !propNames.Contains(m.Name))) sb.Append("M ").Append(Sig(m)).Append(" ;; ");
        }
        var casm = typeof(swBodyType_e).Assembly;
        foreach (var t in casm.GetTypes().Where(x => x.IsEnum && words.Any(w => x.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(x => x.Name))
        {
            sb.Append("## enum ").Append(t.Name).Append(": ");
            foreach (var n in Enum.GetNames(t)) sb.Append(n).Append('=').Append(Convert.ToInt64(Enum.Parse(t, n))).Append(' ');
            sb.Append(" ;; ");
        }
        return sb.ToString();
    }
}
