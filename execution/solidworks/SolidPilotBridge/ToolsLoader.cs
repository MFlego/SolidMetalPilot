// Loads the tool implementations (SolidPilotBridge.Tools.dll) into SolidWorks from BYTES: the file is never locked, so a new build replaces the running "generation" with one pipe command
// (reload_tools) instead of a SolidWorks restart. No AppDomain is used - the maintained SolidDna fork (CADBooster) dropped its separate AppDomain in 2.0.0 because it exposed SolidWorks bugs.
// Old generations stay loaded (a few MB per reload) but are no longer called. The shell, the SolidWorks interops and anything else already loaded are shared, not duplicated.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SolidPilot.Bridge
{
    public static class ToolsLoader
    {
        private static readonly object Gate = new object();
        private static bool _hooked;
        public static string ToolsFolder { get; private set; }
        public static IToolsHost Current { get; private set; }
        public static int Generation { get; private set; }
        public static string LoadedFrom { get; private set; }
        public static DateTime LoadedAtUtc { get; private set; }
        public static string LastError { get; private set; }

        public static void Init(string toolsFolder) { ToolsFolder = toolsFolder; HookResolver(); }

        // The tools assembly is loaded from bytes (no probing path of its own), so every reference it makes is answered here.
        private static void HookResolver()
        {
            if (_hooked) return; _hooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    var want = new AssemblyName(e.Name); var shell = typeof(ToolsLoader).Assembly;
                    if (want.Name == shell.GetName().Name) return shell;                                            // the tools reference the shell's contract
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())                                      // exact identity already loaded (SolidWorks interops, Newtonsoft ...)
                    { var n = a.GetName(); if (n.Name == want.Name && n.Version == want.Version) return a; }
                    foreach (var dir in new[] { ToolsFolder, Path.GetDirectoryName(shell.Location) })               // otherwise from beside the tools / the shell
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        var f = Path.Combine(dir, want.Name + ".dll");
                        if (File.Exists(f)) return Assembly.LoadFrom(f);
                    }
                }
                catch (Exception ex) { BridgeLog.Error("AssemblyResolve " + e.Name, ex); }
                return null;
            };
        }

        /// <summary>Load (or reload) the newest build from <see cref="ToolsFolder"/>. The caller guarantees no tool call is running (ToolGate.IsBusy). Returns a one-line status.</summary>
        public static string Load(IBridgeServices services)
        {
            lock (Gate)
            {
                try
                {
                    var dll = Path.Combine(ToolsFolder ?? "", "SolidPilotBridge.Tools.dll");
                    if (!File.Exists(dll)) { LastError = "not found: " + dll; return "tools NOT loaded - " + LastError; }
                    var pdb = Path.ChangeExtension(dll, ".pdb");
                    var asm = File.Exists(pdb) ? Assembly.Load(File.ReadAllBytes(dll), File.ReadAllBytes(pdb)) : Assembly.Load(File.ReadAllBytes(dll));
                    Type type;
                    try { type = asm.GetTypes().FirstOrDefault(t => !t.IsAbstract && typeof(IToolsHost).IsAssignableFrom(t)); }
                    catch (ReflectionTypeLoadException rtl) { throw new InvalidOperationException("cannot load types: " + string.Join("; ", rtl.LoaderExceptions.Select(x => x.Message).Distinct())); }
                    if (type == null) { LastError = "no IToolsHost implementation in " + dll; return "tools NOT loaded - " + LastError; }
                    var host = (IToolsHost)Activator.CreateInstance(type);
                    host.Initialize(services);
                    var old = Current;
                    Current = host; Generation++; LoadedFrom = dll; LoadedAtUtc = DateTime.UtcNow; LastError = null;
                    if (old != null) { try { old.Shutdown(); } catch (Exception ex) { BridgeLog.Error("previous tools Shutdown", ex); } }
                    var msg = "tools generation " + Generation + " loaded (" + host.Version + ") from " + dll;
                    BridgeLog.Info(msg); return msg;
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message; BridgeLog.Error("tools load", ex);
                    return "tools NOT loaded - " + LastError;       // the previous generation (if any) keeps serving
                }
            }
        }

        public static string InfoJson()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"loaded\":").Append(Current != null ? "true" : "false");
            sb.Append(",\"generation\":").Append(Generation);
            sb.Append(",\"version\":").Append(Json.Str(Current == null ? null : Current.Version));
            sb.Append(",\"loadedFrom\":").Append(Json.Str(LoadedFrom));
            sb.Append(",\"loadedAtUtc\":").Append(Json.Str(Generation == 0 ? null : LoadedAtUtc.ToString("o")));
            sb.Append(",\"folder\":").Append(Json.Str(ToolsFolder));
            sb.Append(",\"lastError\":").Append(Json.Str(LastError));
            return sb.Append('}').ToString();
        }
    }
}
