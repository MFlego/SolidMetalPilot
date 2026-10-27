// REST façade inside SolidWorks: the same four endpoints and JSON shapes the out-of-process execution layer serves, so the Python adapter, the compiler and the IR round trip work
// unchanged against the in-process tools (point EXECUTION_BASE_URL at this port).
//   GET /api/tool/state   POST /api/tool/execute   GET /health   POST /ensure_ready
// BCL only (HttpListener): OWIN / Web API / Newtonsoft stay out of SolidWorks' default AppDomain. Binds to localhost only. Browsers must never be able to drive CAD, so a request that carries
// an Origin header (CSRF), a foreign Host header (DNS rebinding) or a non-JSON content type on POST is refused.
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SolidPilot.Bridge
{
    public sealed class HttpFacade
    {
        public const int DefaultPort = 5001;     // 5000 belongs to the out-of-process execution layer until it is retired
        private const int MaxBodyBytes = 32 * 1024 * 1024;
        private readonly Func<string> _bridgeStateJson;
        private HttpListener _listener; private Thread _thread; private volatile bool _stop;
        public int Port { get; private set; }
        public string StartError { get; private set; }
        public long Requests;
        public bool Running { get { return _listener != null && _listener.IsListening; } }

        public HttpFacade(Func<string> bridgeStateJson) { _bridgeStateJson = bridgeStateJson; }

        public static bool Disabled { get { return Environment.GetEnvironmentVariable("SOLIDPILOT_HTTP") == "0"; } }

        /// <summary>Port: env SOLIDPILOT_PORT, else registry HKCU\Software\SolidPilot\Bridge\HttpPort (DWORD), else 5001.</summary>
        public static int ResolvePort()
        {
            int p;
            if (int.TryParse(Environment.GetEnvironmentVariable("SOLIDPILOT_PORT"), out p) && p > 0 && p < 65536) return p;
            try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\SolidPilot\Bridge", "HttpPort", null); if (v is int && (int)v > 0 && (int)v < 65536) return (int)v; } catch { }
            return DefaultPort;
        }

        public void Start()
        {
            Port = ResolvePort();
            try
            {
                _listener = new HttpListener(); _listener.Prefixes.Add("http://localhost:" + Port + "/"); _listener.Start();
                _stop = false; _thread = new Thread(Loop) { IsBackground = true, Name = "SolidPilotBridgeHttp" }; _thread.Start();
                BridgeLog.Info("http façade listening on http://localhost:" + Port + "/");
            }
            catch (Exception e) { StartError = e.GetType().Name + ": " + e.Message; BridgeLog.Error("http façade start on port " + Port, e); }
        }

        public void Stop()
        {
            _stop = true;
            try { if (_listener != null) { _listener.Stop(); _listener.Close(); } } catch { }
        }

        public string InfoJson()
        {
            return "{\"running\":" + (Running ? "true" : "false") + ",\"port\":" + Port + ",\"requests\":" + Interlocked.Read(ref Requests) + ",\"startError\":" + Json.Str(StartError) + "}";
        }

        private void Loop()
        {
            while (!_stop)
            {
                HttpListenerContext c;
                try { c = _listener.GetContext(); }
                catch (Exception) { if (_stop) return; Thread.Sleep(100); continue; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }

        private void Handle(HttpListenerContext c)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew(); int status = 500; string route = c.Request.HttpMethod + " " + c.Request.Url.AbsolutePath;
            try
            {
                Interlocked.Increment(ref Requests);
                var reply = Route(c.Request); status = reply.HttpStatus;
                var bytes = Encoding.UTF8.GetBytes(reply.Json ?? "");
                c.Response.StatusCode = reply.HttpStatus; c.Response.ContentType = "application/json; charset=utf-8"; c.Response.ContentLength64 = bytes.Length;
                c.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                BridgeLog.Error("http " + route, ex);
                try { var b = Encoding.UTF8.GetBytes("{\"error\":" + Json.Str(ex.Message) + "}"); c.Response.StatusCode = 500; c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(b, 0, b.Length); } catch { }
            }
            finally
            {
                try { c.Response.Close(); } catch { }
                if (!route.EndsWith("/health", StringComparison.OrdinalIgnoreCase)) BridgeLog.Info("http " + route + " -> " + status + " (" + clock.ElapsedMilliseconds + " ms)");
            }
        }

        private ToolReply Route(HttpListenerRequest r)
        {
            if (!string.IsNullOrEmpty(r.Headers["Origin"])) return ToolGate.Error(403, "browser-originated requests are refused");
            var host = r.Url.Host;
            if (host != "localhost" && host != "127.0.0.1" && host != "[::1]" && host != "::1") return ToolGate.Error(403, "unexpected Host header");
            var path = r.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant(); if (path.Length == 0) path = "/";
            var tools = ToolsLoader.Current;

            switch (path)
            {
                case "/":
                    return new ToolReply(200, "{\"service\":\"SolidPilot Bridge\",\"version\":" + Json.Str(PipeServer.Version) + ",\"inProcess\":true,\"toolsLoaded\":" + (tools != null ? "true" : "false") + "}");
                case "/api/tool/state":
                    if (r.HttpMethod != "GET") return ToolGate.Error(405, "GET only");
                    return tools != null ? tools.State() : NoTools();
                case "/health":
                    if (r.HttpMethod != "GET") return ToolGate.Error(405, "GET only");
                    return tools != null ? ToolGate.Lifecycle(tools, false, Extra()) : new ToolReply(200, "{\"status\":\"UP\",\"inProcess\":true,\"toolsLoaded\":false,\"toolsError\":" + Json.Str(ToolsLoader.LastError) + ",\"bridgeLoaded\":true}");
                case "/ensure_ready":
                    if (r.HttpMethod != "POST") return ToolGate.Error(405, "POST only");
                    return tools != null ? ToolGate.Lifecycle(tools, true, Extra()) : NoTools();
                case "/api/tool/execute":
                    if (r.HttpMethod != "POST") return ToolGate.Error(405, "POST only");
                    if (r.ContentType == null || !r.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return ToolGate.Error(415, "Content-Type must be application/json");
                    if (tools == null) return NoTools();
                    return ToolGate.Execute(tools, ReadBody(r));
                default:
                    return ToolGate.Error(404, "not found: " + path);
            }
        }

        private static ToolReply NoTools() { return ToolGate.Error(503, "tools are not loaded" + (ToolsLoader.LastError != null ? ": " + ToolsLoader.LastError : "") + " - build SolidPilotBridge.Tools and send 'reload_tools' on the bridge pipe"); }

        private string Extra()
        {
            string state; try { state = _bridgeStateJson(); } catch { state = "null"; }
            return "{\"bridgeLoaded\":true,\"bridgeState\":" + state + ",\"staTimeoutSeconds\":" + ToolGate.TimeoutSeconds + ",\"inProcess\":true,\"toolsGeneration\":" + ToolsLoader.Generation
                 + ",\"bridgeVersion\":" + Json.Str(PipeServer.Version) + ",\"http\":" + InfoJson() + ",\"snapshots\":" + Snapshots.ListJson()
                 + (Inflight.PreviousSessionDiedDuring != null ? ",\"previousSessionDiedDuring\":" + Json.Str(Inflight.PreviousSessionDiedDuring) : "") + "}";
        }

        private static string ReadBody(HttpListenerRequest r)
        {
            if (r.ContentLength64 > MaxBodyBytes) throw new InvalidOperationException("request body too large");
            using (var rd = new StreamReader(r.InputStream, r.ContentEncoding ?? Encoding.UTF8)) return rd.ReadToEnd();
        }
    }
}
