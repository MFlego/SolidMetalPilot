// Recent tool calls (tool, operation id, outcome, milliseconds) in a small ring buffer: the data behind the "activity" pipe command, and later the task-pane log. Pure bookkeeping, no SolidWorks access.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SolidPilot.Bridge
{
    public static class Activity
    {
        private sealed class Entry { public DateTime Utc; public string Tool, Op, Result; public long Ms; }
        private const int Capacity = 200;
        private static readonly LinkedList<Entry> Entries = new LinkedList<Entry>();
        private static readonly object Gate = new object();
        private static readonly Regex OpRx = new Regex("\"operationId\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StatusRx = new Regex("\"status\"\\s*:\\s*\"([A-Za-z_]+)\"", RegexOptions.Compiled);
        private static readonly Regex CodeRx = new Regex("\"code\"\\s*:\\s*\"([A-Za-z_]+)\"", RegexOptions.Compiled);
        public static long Total;

        public static void Add(string requestBody, ToolReply reply, long ms)
        {
            try
            {
                var tool = Snapshots.ToolOf(requestBody); var op = OpRx.Match(requestBody ?? ""); var json = reply == null ? "" : (reply.Json ?? "");
                var st = StatusRx.Match(json); var code = CodeRx.Match(json);
                var result = reply == null ? "no reply" : st.Success ? st.Groups[1].Value + (code.Success ? ":" + code.Groups[1].Value : "") : "HTTP " + reply.HttpStatus;
                lock (Gate)
                {
                    Total++;
                    Entries.AddLast(new Entry { Utc = DateTime.UtcNow, Tool = tool ?? "?", Op = op.Success ? op.Groups[1].Value : null, Result = result, Ms = ms });
                    while (Entries.Count > Capacity) Entries.RemoveFirst();
                }
            }
            catch { }
        }

        public sealed class Row { public DateTime Utc; public string Tool, Result; public long Ms; }

        /// <summary>The newest <paramref name="max"/> calls as rows, newest LAST (the task pane shows them reversed).</summary>
        public static List<Row> Recent(int max)
        {
            lock (Gate)
            {
                var all = new List<Entry>(Entries); var rows = new List<Row>(); var skip = Math.Max(0, all.Count - max);
                for (int i = skip; i < all.Count; i++) rows.Add(new Row { Utc = all[i].Utc, Tool = all[i].Tool, Result = all[i].Result, Ms = all[i].Ms });
                return rows;
            }
        }

        /// <summary>The newest <paramref name="max"/> calls, newest last.</summary>
        public static string Json(int max)
        {
            lock (Gate)
            {
                var all = new List<Entry>(Entries); var skip = Math.Max(0, all.Count - max);
                var sb = new StringBuilder("{\"total\":" + Total + ",\"calls\":[");
                for (int i = skip; i < all.Count; i++)
                {
                    var e = all[i]; if (i > skip) sb.Append(',');
                    sb.Append("{\"utc\":").Append(Json_.Str(e.Utc.ToString("HH:mm:ss.fff"))).Append(",\"tool\":").Append(Json_.Str(e.Tool)).Append(",\"op\":").Append(Json_.Str(e.Op))
                      .Append(",\"result\":").Append(Json_.Str(e.Result)).Append(",\"ms\":").Append(e.Ms).Append('}');
                }
                return sb.Append("]}").ToString();
            }
        }

        // (the shell's JSON string helper is called Json; inside this class the name is shadowed by the method above)
        private static class Json_ { public static string Str(string s) { return SolidPilot.Bridge.Json.Str(s); } }
    }
}
