using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>Distills mcp-calls session logs into an eval-style report: per-tool
    /// counts, success rates, duration percentiles and top errors. Read-only;
    /// malformed lines are counted and skipped.</summary>
    internal static class SessionDistiller
    {
        public const int DefaultMaxEntries = 5000;
        public const int MaxEntriesLimit = 20000;
        public const int MaxLinesScanned = 100000;
        public const int MaxErrorGroups = 20;

        public sealed class Row
        {
            public string SessionId { get; set; } = "";
            public string Tool { get; set; } = "";
            public bool Success { get; set; }
            public long DurationMs { get; set; }
            public string Error { get; set; } = "";
            public DateTime TimestampUtc { get; set; } = DateTime.MinValue;
        }

        public static Row ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            JObject obj;
            try { obj = JObject.Parse(line); }
            catch { return null; }
            var tool = obj.Value<string>("tool");
            if (string.IsNullOrEmpty(tool)) return null;
            DateTime timestamp = DateTime.MinValue;
            var raw = obj.Value<string>("timestamp");
            if (!string.IsNullOrEmpty(raw)
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                timestamp = parsed.ToUniversalTime();
            long duration = 0;
            try { duration = obj.Value<long?>("duration_ms") ?? 0; } catch { }
            if (duration < 0) duration = 0;
            return new Row
            {
                SessionId = obj.Value<string>("session_id") ?? "",
                Tool = tool,
                Success = obj.Value<bool?>("success") ?? false,
                DurationMs = duration,
                Error = obj.Value<string>("error") ?? "",
                TimestampUtc = timestamp,
            };
        }

        public static List<string> ListLogFiles(string logDir)
        {
            var files = new List<string>(Directory.GetFiles(logDir, "mcp-calls-*.jsonl"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            var current = Path.Combine(logDir, "mcp-calls.jsonl");
            if (File.Exists(current)) files.Add(current);
            return files;
        }

        public static JObject Distill(IEnumerable<Row> rows, int maxEntries)
        {
            var kept = rows.Reverse().Take(maxEntries).Reverse().ToList();
            var tools = kept
                .GroupBy(r => r.Tool, StringComparer.Ordinal)
                .Select(g =>
                {
                    var durations = g.Select(r => r.DurationMs).OrderBy(d => d).ToArray();
                    var failed = g.Count(r => !r.Success);
                    return new JObject
                    {
                        ["tool"] = g.Key,
                        ["calls"] = g.Count(),
                        ["succeeded"] = g.Count() - failed,
                        ["failed"] = failed,
                        ["success_rate"] = Math.Round((double)(g.Count() - failed) / g.Count(), 3),
                        ["total_duration_ms"] = durations.Sum(),
                        ["avg_duration_ms"] = Math.Round(durations.Average(), 1),
                        ["p50_ms"] = Percentile(durations, 0.5),
                        ["p95_ms"] = Percentile(durations, 0.95),
                    };
                })
                .OrderByDescending(t => t.Value<int>("calls"))
                .ToArray();
            var errors = kept
                .Where(r => !r.Success && !string.IsNullOrWhiteSpace(r.Error))
                .GroupBy(r => NormalizeError(r.Error), StringComparer.Ordinal)
                .Select(g => new { Error = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .Take(MaxErrorGroups)
                .Select(g => new JObject { ["error"] = g.Error, ["count"] = g.Count })
                .ToArray();
            var stamps = kept.Where(r => r.TimestampUtc != DateTime.MinValue)
                .Select(r => r.TimestampUtc).ToArray();
            return new JObject
            {
                ["entries"] = kept.Count,
                ["sessions"] = new JArray(kept.Select(r => r.SessionId).Distinct(StringComparer.Ordinal)
                    .Select(s => new JValue(s)).ToArray()),
                ["span_utc"] = stamps.Length == 0 ? null : new JObject
                {
                    ["first"] = stamps.Min().ToString("o"),
                    ["last"] = stamps.Max().ToString("o"),
                },
                ["tools"] = new JArray(tools),
                ["errors_top"] = new JArray(errors),
            };
        }

        private static long Percentile(long[] sorted, double rank)
        {
            if (sorted.Length == 0) return 0;
            var index = (int)Math.Ceiling(rank * sorted.Length) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Length) index = sorted.Length - 1;
            return sorted[index];
        }

        private static string NormalizeError(string error)
        {
            var collapsed = string.Join(" ", error.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length > 200 ? collapsed.Substring(0, 200) : collapsed;
        }
    }
}
