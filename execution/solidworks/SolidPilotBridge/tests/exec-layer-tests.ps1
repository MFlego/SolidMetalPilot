# Offline unit tests for the execution layer's safety executor (StaExecutor timeout / busy / recovery / ordering). No SolidWorks needed: the delegates are plain C#.
# (If the bridge happens to be running, the pre-flight dialog query is exercised harmlessly - it finds no dialog.)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File exec-layer-tests.ps1 [-Exe <path to SolidworksExecution.exe>]
param([string]$Exe = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) '..\SolidworksExecution\bin\Debug\SolidworksExecution.exe'))
$Exe = (Resolve-Path $Exe).Path
# Add-Type cannot reference an .exe directly on this host: reference a copy renamed to .dll (the assembly is identical), loaded first with LoadFrom.
$ref = Join-Path $env:TEMP 'swexec_under_test.dll'; Copy-Item $Exe $ref -Force; [void][Reflection.Assembly]::LoadFrom($ref)
Add-Type -ReferencedAssemblies $ref -TypeDefinition @"
using System; using System.Collections.Generic; using System.Diagnostics; using System.Threading; using System.Threading.Tasks;
using SolidworksExecution.Infrastructure;
public static class ExecTests {
  public static List<string> Results = new List<string>();
  static void Check(string name, Func<string> body) {
    var sw = Stopwatch.StartNew(); string msg = null;
    try { msg = body(); } catch (Exception e) { msg = e.GetType().Name + ": " + e.Message; }
    sw.Stop(); Results.Add((msg == null ? "PASS" : "FAIL") + "  " + name.PadRight(60) + " " + sw.ElapsedMilliseconds.ToString().PadLeft(6) + " ms  " + (msg ?? ""));
  }
  public static void RunAll() {
    var ex = StaExecutor.Instance;
    Check("fast call returns its value", () => ex.Run(() => 41 + 1, 5) == 42 ? null : "wrong value");
    Check("worker thread is STA", () => ex.Run(() => Thread.CurrentThread.GetApartmentState().ToString(), 5) == "STA" ? null : "not STA");
    Check("exception is wrapped as InvalidOperationException('STA execution failed')", () => {
      try { ex.Run<int>(() => { throw new ArgumentException("inner"); }, 5); return "no exception"; }
      catch (InvalidOperationException e) { return e.Message.StartsWith("STA execution failed") && e.InnerException is ArgumentException ? null : "wrong wrapping: " + e.Message; } });
    Check("calls from 50 threads are serialised and all return", () => {
      int running = 0, maxRunning = 0; var tasks = new List<Task<int>>();
      for (int i = 0; i < 50; i++) { int n = i; tasks.Add(Task.Run(() => ex.Run(() => { int r = Interlocked.Increment(ref running); if (r > maxRunning) maxRunning = r; Thread.Sleep(5); Interlocked.Decrement(ref running); return n; }, 30))); }
      Task.WaitAll(tasks.ToArray()); for (int i = 0; i < 50; i++) if (tasks[i].Result != i) return "wrong result at " + i;
      return maxRunning == 1 ? null : "overlap: max concurrent = " + maxRunning; });
    Check("timeout raises SolidWorksBlockedException STA_TIMEOUT within ~1.3 s", () => {
      var sw = Stopwatch.StartNew();
      try { ex.Run<int>(() => { Thread.Sleep(5000); return 1; }, 1); return "no exception"; }
      catch (SolidWorksBlockedException e) { if (e.Code != "STA_TIMEOUT") return "code " + e.Code; return sw.ElapsedMilliseconds < 1800 ? null : "took " + sw.ElapsedMilliseconds + " ms"; } });
    Check("while the abandoned call still runs, a new call fails fast with SW_BUSY", () => {
      var sw = Stopwatch.StartNew();
      try { ex.Run<int>(() => 2, 1); return "no exception"; }
      catch (SolidWorksBlockedException e) { if (e.Code != "SW_BUSY") return "code " + e.Code; return sw.ElapsedMilliseconds < 800 ? null : "took " + sw.ElapsedMilliseconds + " ms"; } });
    Check("after the stuck call ends the executor recovers", () => {
      Thread.Sleep(5000); return ex.Run(() => 3, 5) == 3 ? null : "wrong value"; });
    Check("TimeoutSeconds honours SWMCP_STA_TIMEOUT_SEC", () => {
      Environment.SetEnvironmentVariable("SWMCP_STA_TIMEOUT_SEC", "7"); int v = StaExecutor.TimeoutSeconds; Environment.SetEnvironmentVariable("SWMCP_STA_TIMEOUT_SEC", null);
      return v == 7 && StaExecutor.TimeoutSeconds == 120 ? null : "got " + v; });
    Check("BridgeClient.Query returns null quickly when the pipe does not exist", () => {
      var sw = Stopwatch.StartNew(); var r = BridgeClient.Query("ping_nonexistent_pipe_test", 300);
      return sw.ElapsedMilliseconds < 3000 ? null : "took " + sw.ElapsedMilliseconds + " ms (r=" + r + ")"; });
  }
}
"@
[ExecTests]::RunAll()
[ExecTests]::Results
$fail = @([ExecTests]::Results | Where-Object { $_ -like 'FAIL*' }).Count
""; "{0} tests, {1} failed" -f [ExecTests]::Results.Count, $fail
exit $fail
