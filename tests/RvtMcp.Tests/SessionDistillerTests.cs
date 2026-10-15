using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class SessionDistillerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtverify-distill-" + Guid.NewGuid().ToString("N"));

        public SessionDistillerTests()
        {
            Directory.CreateDirectory(_dir);
        }

        private static string Line(string tool, bool ok, long ms, string error = null, string session = "s1", string ts = "2026-10-14T10:00:00Z")
        {
            return new JObject
            {
                ["session_id"] = session,
                ["tool"] = tool,
                ["success"] = ok,
                ["duration_ms"] = ms,
                ["error"] = error,
                ["timestamp"] = ts,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        [Fact]
        public void ParseLine_reads_shapes_and_rejects_junk()
        {
            var row = SessionDistiller.ParseLine(Line("verify_assert", true, 120));
            Assert.Equal("verify_assert", row.Tool);
            Assert.True(row.Success);
            Assert.Equal(120, row.DurationMs);
            Assert.Equal(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc), row.TimestampUtc);

            Assert.Null(SessionDistiller.ParseLine(""));
            Assert.Null(SessionDistiller.ParseLine("{oops"));
            Assert.Null(SessionDistiller.ParseLine("{\"tool\":\"\"}"));
            var neg = SessionDistiller.ParseLine(Line("x", false, -5));
            Assert.Equal(0, neg.DurationMs);
            var noTs = SessionDistiller.ParseLine("{\"tool\":\"x\",\"success\":true}");
            Assert.Equal(DateTime.MinValue, noTs.TimestampUtc);
        }

        [Fact]
        public void Distill_aggregates_rates_percentiles_and_errors()
        {
            var rows = new[]
            {
                SessionDistiller.ParseLine(Line("a", true, 100)),
                SessionDistiller.ParseLine(Line("a", false, 300, "boom x")),
                SessionDistiller.ParseLine(Line("a", false, 200, "boom  x")),
                SessionDistiller.ParseLine(Line("b", true, 50, null, "s2")),
            };

            var report = SessionDistiller.Distill(rows, 100);

            Assert.Equal(4, report.Value<int>("entries"));
            Assert.Equal(new[] { "s1", "s2" }, report["sessions"].Select(s => s.Value<string>()).ToArray());
            var a = report["tools"].Single(t => t.Value<string>("tool") == "a");
            Assert.Equal(3, a.Value<int>("calls"));
            Assert.Equal(1, a.Value<int>("succeeded"));
            Assert.Equal(2, a.Value<int>("failed"));
            Assert.Equal(0.333, a.Value<double>("success_rate"));
            Assert.Equal(200, a.Value<long>("p50_ms"));
            Assert.Equal(300, a.Value<long>("p95_ms"));
            Assert.Equal(600, a.Value<long>("total_duration_ms"));
            Assert.Equal("boom x", report["errors_top"][0].Value<string>("error"));
            Assert.Equal(2, report["errors_top"][0].Value<int>("count"));
        }

        [Fact]
        public void Distill_keeps_newest_and_handles_empty()
        {
            var rows = Enumerable.Range(0, 5)
                .Select(i => SessionDistiller.ParseLine(Line("a", true, i, null, "s1",
                    "2026-10-14T10:0" + i + ":00Z")))
                .ToList();

            var report = SessionDistiller.Distill(rows, 2);

            Assert.Equal(2, report.Value<int>("entries"));
            Assert.Equal("2026-10-14T10:03:00.0000000Z", report["span_utc"].Value<string>("first"));
            var empty = SessionDistiller.Distill(new SessionDistiller.Row[0], 10);
            Assert.True(empty["span_utc"] == null || empty["span_utc"].Type == Newtonsoft.Json.Linq.JTokenType.Null);
        }

        [Fact]
        public void ListLogFiles_orders_archives_before_live()
        {
            File.WriteAllText(Path.Combine(_dir, "mcp-calls-20261014-090000.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(_dir, "mcp-calls.jsonl"), "{}\n");

            var files = SessionDistiller.ListLogFiles(_dir);

            Assert.Equal(2, files.Count);
            Assert.EndsWith("mcp-calls-20261014-090000.jsonl", files[0]);
            Assert.EndsWith("mcp-calls.jsonl", files[1]);
        }

        [Fact]
        public async Task Tool_reads_directory_filters_session_and_rejects_bad_input()
        {
            File.WriteAllText(Path.Combine(_dir, "mcp-calls.jsonl"),
                Line("verify_assert", true, 100, null, "s1") + "\n"
                + "junk\n"
                + Line("verify_assert", false, 50, "nope", "s2") + "\n");

            var json = JObject.Parse(await MetaTools.DistillSession(_dir));
            Assert.Equal(2, json.Value<int>("entries"));
            Assert.Equal(1, json.Value<long>("malformed_skipped"));
            Assert.False(json.Value<bool>("scan_truncated"));

            json = JObject.Parse(await MetaTools.DistillSession(_dir, 5000, "s2"));
            Assert.Equal(1, json.Value<int>("entries"));

            Assert.StartsWith("Error:", await MetaTools.DistillSession(_dir, 0));
            Assert.StartsWith("Error:", await MetaTools.DistillSession(
                Path.Combine(_dir, "missing")));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }
    }
}
