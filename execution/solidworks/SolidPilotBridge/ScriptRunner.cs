// In-process C# script runner: compile a small C# file and run it INSIDE SolidWorks on its UI thread with typed access to the whole API (ISldWorks, ModelDoc2, selection manager,
// feature data ...). Concept inspired by the "ScriptRunner" add-in in AngelSix's solidworks-api repository (MIT, https://github.com/angelsix/solidworks-api) - no code is copied.
// Why: every change to the bridge DLL needs a SolidWorks restart (the DLL is locked while loaded); scripts compile per call, so API experiments need no restart, and the go/no-go
// question "does typed in-process API access beat out-of-process late-bound COM?" can be answered with a few small files.
//
// Script contract (C# 5, the .NET Framework compiler):
//     using SolidWorks.Interop.sldworks; using SolidWorks.Interop.swconst; using SolidPilot.Bridge;
//     public static class Script { public static string Run(SldWorks sw, string arg) { ... return "text"; } }
// Pipe command:  run_cs <path to .cs>[|argument]
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using SolidWorks.Interop.sldworks;

namespace SolidPilot.Bridge
{
    public static class ScriptRunner
    {
        private static readonly Dictionary<string, Assembly> Cache = new Dictionary<string, Assembly>();

        public static string Run(SldWorks sw, string spec, int timeoutMs)
        {
            var parts = spec.Split(new[] { '|' }, 2); var path = parts[0].Trim(); var arg = parts.Length > 1 ? parts[1] : "";
            if (!File.Exists(path)) return "script not found: " + path;
            var src = File.ReadAllText(path);
            var key = path + "#" + src.GetHashCode();
            Assembly asm;
            lock (Cache)
            {
                if (!Cache.TryGetValue(key, out asm))
                {
                    var cp = new CompilerParameters { GenerateInMemory = true, GenerateExecutable = false, TreatWarningsAsErrors = false };
                    foreach (var r in new[] { "System.dll", "System.Core.dll", "System.Windows.Forms.dll", "System.Drawing.dll", "System.Xml.dll" }) cp.ReferencedAssemblies.Add(r);
                    foreach (var t in new[] { typeof(SldWorks), typeof(SolidWorks.Interop.swconst.swDocumentTypes_e), typeof(SolidWorks.Interop.swpublished.ISwAddin), typeof(ScriptRunner) })
                        cp.ReferencedAssemblies.Add(t.Assembly.Location);
                    var res = new CSharpCodeProvider().CompileAssemblyFromSource(cp, src);
                    if (res.Errors.HasErrors)
                    {
                        var sb = new StringBuilder("COMPILE ERRORS:");
                        foreach (CompilerError e in res.Errors) if (!e.IsWarning) sb.Append(" ;; line " + e.Line + ": " + e.ErrorNumber + " " + e.ErrorText);
                        return sb.ToString();
                    }
                    asm = res.CompiledAssembly; Cache[key] = asm;
                }
            }
            var type = asm.GetType("Script"); var m = type == null ? null : type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (m == null) return "script must define: public static class Script { public static string Run(SldWorks sw, string arg) }";
            return (string)UiThread.Run(() =>
            {
                try { return m.Invoke(null, new object[] { sw, arg }); }
                catch (TargetInvocationException ex) { return "SCRIPT EXCEPTION: " + ex.InnerException; }
            }, timeoutMs);
        }
    }
}
