using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Server.Handlers;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class CadApplyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtverify-apply-" + Guid.NewGuid().ToString("N"));

        public CadApplyTests()
        {
            Directory.CreateDirectory(_dir);
            CadApplyStore.RootOverride = _dir;
        }

        private static string BuildPlan()
        {
            var extraction = new JObject
            {
                ["results"] = new JArray(new JObject
                {
                    ["instance_id"] = 9L,
                    ["source_path"] = "walls.dwg",
                    ["truncated"] = false,
                    ["entities"] = new JArray(
                        WallLine("WALL", 0, 0, 5000, 0),
                        WallLine("WALL", 0, 200, 5000, 200))
                })
            };
            var rules = new JObject
            {
                ["version"] = "apply-v1",
                ["wall_layers"] = new JArray("WALL"),
                ["wall_thickness_mm"] = 200,
            };
            var plan = CadPlanBuilder.Build(
                extraction.ToString(Formatting.None), rules.ToString(Formatting.None));
            Assert.True(plan["wall_candidates"].Count() >= 1);
            return plan.ToString(Formatting.None);
        }

        private static JObject WallLine(string layer, double x1, double y1, double x2, double y2)
        {
            return new JObject
            {
                ["layer"] = layer,
                ["type"] = "curve:Line",
                ["start_mm"] = new JObject { ["x_mm"] = x1, ["y_mm"] = y1, ["z_mm"] = 0 },
                ["end_mm"] = new JObject { ["x_mm"] = x2, ["y_mm"] = y2, ["z_mm"] = 0 },
            };
        }

        private static JObject BatchOk(params long[] ids)
        {
            return new JObject
            {
                ["rolledBack"] = false,
                ["results"] = new JArray(ids.Select(id => new JObject
                {
                    ["ok"] = true,
                    ["data"] = new JObject { ["elementId"] = id },
                }).ToArray()),
            };
        }

        [Fact]
        public async Task Dry_run_previews_without_sending()
        {
            var calls = 0;
            string output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => { calls++; return Task.FromResult(new JObject()); },
                async () => { output = await LinksTools.CadApply(BuildPlan(), null, "L1", 3000, true, 20, false); });

            Assert.Equal(0, calls);
            var json = JObject.Parse(output);
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(1, json.Value<int>("count"));
            Assert.Equal("L1", json.Value<string>("level"));
            Assert.Equal(3000, json.Value<double>("height_mm"));
            Assert.True(json["apply_id"] == null || json["apply_id"].Type == Newtonsoft.Json.Linq.JTokenType.Null);
        }

        [Fact]
        public async Task Requires_confirm_for_apply()
        {
            var calls = 0;
            string output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => { calls++; return Task.FromResult(new JObject()); },
                async () => { output = await LinksTools.CadApply(BuildPlan(), "[1]", "L1", 3000, false, 20, false); });

            Assert.Equal(0, calls);
            Assert.StartsWith("Error:", output, StringComparison.Ordinal);
            Assert.Contains("confirm", output);
        }

        [Fact]
        public async Task Apply_fans_out_to_batch_and_journals_provenance()
        {
            var commands = new System.Collections.Generic.List<string>();
            JObject batchSeen = null;
            string output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) =>
                {
                    commands.Add(command);
                    batchSeen = JObject.FromObject(parameters);
                    return Task.FromResult(BatchOk(101));
                },
                async () => { output = await LinksTools.CadApply(BuildPlan(), null, "L2", 3600, false, 20, true); });

            Assert.Equal(new[] { "batch_execute" }, commands.ToArray());
            Assert.Single(batchSeen["commands"]);
            Assert.Equal("wall", batchSeen["commands"][0]["params"].Value<string>("elementType"));
            Assert.Equal("L2", batchSeen["commands"][0]["params"].Value<string>("level"));
            var json = JObject.Parse(output);
            Assert.True(json.Value<bool>("applied"));
            Assert.False(string.IsNullOrEmpty(json.Value<string>("apply_id")));
            Assert.Equal(new[] { 101L },
                json["mappings"].Select(m => m["element_ids"][0].Value<long>()).ToArray());

            var journal = JArray.Parse(File.ReadAllText(Path.Combine(_dir, "applies.json")));
            Assert.Single(journal);
            Assert.Equal(json.Value<string>("apply_id"), journal[0].Value<string>("apply_id"));
            Assert.True(journal[0]["mappings"].Count() == 1);
        }

        [Fact]
        public async Task Rejects_tampered_plan_bad_selection_and_limits()
        {
            var plan = JObject.Parse(BuildPlan());
            plan["wall_candidates"][0]["length_mm"] = -1;
            var calls = 0;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => { calls++; return Task.FromResult(new JObject()); },
                async () =>
                {
                    Assert.Contains("plan_sha256", await LinksTools.CadApply(plan.ToString(Formatting.None)));
                    Assert.StartsWith("Error:", await LinksTools.CadApply(BuildPlan(), "[9]", "L1", 3000, true));
                    Assert.StartsWith("Error:", await LinksTools.CadApply(BuildPlan(), "nope", "L1", 3000, true));
                    Assert.StartsWith("Error:", await LinksTools.CadApply(BuildPlan(), null, "", 3000, true));
                    Assert.StartsWith("Error:", await LinksTools.CadApply(BuildPlan(), null, "L1", 0, true));
                    Assert.StartsWith("Error:", await LinksTools.CadApply(BuildPlan(), null, "L1", 3000, true, 0));
                });
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task Rolled_back_batch_applies_nothing()
        {
            string output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => Task.FromResult(new JObject
                {
                    ["rolledBack"] = true,
                    ["results"] = new JArray(),
                }),
                async () => { output = await LinksTools.CadApply(BuildPlan(), null, "L1", 3000, false, 20, true); });

            var json = JObject.Parse(output);
            Assert.False(json.Value<bool>("applied"));
            Assert.False(File.Exists(Path.Combine(_dir, "applies.json")));
        }

        public void Dispose()
        {
            CadApplyStore.RootOverride = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }
    }
}
