using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class VerifyRunRulesTests
    {
        private const string BasicYaml = @"version: 1
rules:
  - id: walls_present
    description: Room walls exist
    weight: 2
    assertions:
      - kind: count
        category: Walls
        min_expect: 4
  - id: volume_ok
    assertions:
      - kind: solid_volume
        ids: [1, 2]
        min_volume_m3: 0.5
";

        // ---------- parser ----------

        [Fact]
        public void Parses_full_document_with_defaults()
        {
            var rules = VerifyRulesYamlParser.Parse(BasicYaml);

            Assert.Null(rules.ModelKey);
            Assert.Equal(2, rules.Rules.Count);
            Assert.Equal("walls_present", rules.Rules[0].Id);
            Assert.Equal("Room walls exist", rules.Rules[0].Description);
            Assert.Equal(2, rules.Rules[0].Weight);
            Assert.Equal("volume_ok", rules.Rules[1].Id);
            Assert.Equal("", rules.Rules[1].Description);
            Assert.Equal(1, rules.Rules[1].Weight);
            var count = rules.Rules[0].Assertions[0];
            Assert.Equal("count", count.Value<string>("kind"));
            Assert.Equal("Walls", count.Value<string>("category"));
            Assert.Equal(4L, count.Value<long>("min_expect"));
            var ids = rules.Rules[1].Assertions[0]["ids"] as JArray;
            Assert.Equal(new[] { 1L, 2L }, ids.Select(t => t.Value<long>()).ToArray());
            Assert.Equal(0.5, rules.Rules[1].Assertions[0].Value<double>("min_volume_m3"));
        }

        [Fact]
        public void Parses_support_kind()
        {
            var rules = VerifyRulesYamlParser.Parse(
                "version: 1\nrules:\n  - id: hosted\n    assertions:\n      - kind: support\n        category: Windows\n");

            Assert.Equal("support", rules.Rules[0].Assertions[0].Value<string>("kind"));
            Assert.Equal("Windows", rules.Rules[0].Assertions[0].Value<string>("category"));
        }

        [Fact]
        public void Parses_comments_quotes_markers_and_model_key()
        {
            var rules = VerifyRulesYamlParser.Parse(@"---
# leading comment
version: 1 # trailing comment
model_key: 'abc#123'
rules:
  - id: quoted # rule comment
    description: ""walls: all four""
    assertions:
      - kind: bbox
        tol_mm: 2.5
...
");

            Assert.Equal("abc#123", rules.ModelKey);
            Assert.Equal("walls: all four", rules.Rules[0].Description);
            Assert.Equal(2.5, rules.Rules[0].Assertions[0].Value<double>("tol_mm"));
        }

        [Theory]
        [InlineData("", "empty")]
        [InlineData("probe", "key: value")]
        [InlineData("version: 2\nrules:\n  - id: a\n    assertions:\n      - kind: count", "version must be 1")]
        [InlineData("version: 1", "'rules' must be")]
        [InlineData("version: 1\nrules: []", "non-empty list")]
        [InlineData("version: 1\nrules:\n  - id: a\n    assertions:\n      - kind: bogus", "unknown kind")]
        [InlineData("version: 1\nrules:\n  - id: a\n    assertions:\n      - category: Walls", "missing required 'kind'")]
        [InlineData("version: 1\nrules:\n  - id: Bad-Id\n    assertions:\n      - kind: count", "must match")]
        [InlineData("version: 1\nrules:\n  - id: a\n    assertions:\n      - kind: count\n  - id: a\n    assertions:\n      - kind: count", "duplicate rule id")]
        [InlineData("version: 1\nrules:\n  - id: a\n    weight: 0\n    assertions:\n      - kind: count", "weight must be")]
        [InlineData("version: 1\nrules:\n  - id: a\n    assertions:\n      - kind: count\n        ids: [1, x]", "only integers")]
        [InlineData("version: 1\nrules:\n  - id: a\n    assertions:\n      - kind: bbox\n        box_mm: [1]", "must be a mapping")]
        [InlineData("version: 1\nrules:\n  - id: a\n    frobnicate: 1\n    assertions:\n      - kind: count", "unknown rule key")]
        [InlineData("version: 1\nnope: 1\nrules:\n  - id: a\n    assertions:\n      - kind: count", "unknown top-level key")]
        [InlineData("version: 1\nrules:\n\t- id: a\n    assertions:\n      - kind: count", "tabs are not allowed")]
        [InlineData("version: 1\nrules:\n  - id: a\n    assertions:\n      - kind: count\n        extra: *alias", "not supported")]
        public void Rejects_malformed_documents(string yaml, string fragment)
        {
            var ex = Assert.Throws<VerifyRulesYamlParser.ParseException>(
                () => VerifyRulesYamlParser.Parse(yaml));
            Assert.Contains(fragment, ex.Message);
            Assert.True(ex.Line >= 1);
        }

        [Fact]
        public void Rejects_too_many_rules_and_oversize_input()
        {
            var many = "version: 1\nrules:\n" + string.Concat(
                Enumerable.Range(0, 11).Select(i => "  - id: r" + i + "\n    assertions:\n      - kind: count\n"));
            Assert.Contains("at most 10", Assert.Throws<VerifyRulesYamlParser.ParseException>(
                () => VerifyRulesYamlParser.Parse(many)).Message);
            Assert.Contains("exceeds 65536", Assert.Throws<VerifyRulesYamlParser.ParseException>(
                () => VerifyRulesYamlParser.Parse(new string('x', 70000))).Message);
        }

        // ---------- runner through the tool method ----------

        private static JObject AssertOk(bool pass, string modelKey, params JObject[] assertions)
        {
            return new JObject
            {
                ["success"] = true,
                ["data"] = new JObject
                {
                    ["pass"] = pass,
                    ["model_key"] = modelKey,
                    ["assertions"] = new JArray(assertions),
                },
                ["error"] = null,
            };
        }

        private static JObject PassAssertion(string kind)
        {
            return new JObject { ["kind"] = kind, ["pass"] = true };
        }

        private static async Task<JObject> RunWith(params JObject[] responses)
        {
            var queue = new Queue<JObject>(responses);
            var calls = new List<(string Command, object Args)>();
            JObject output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) =>
                {
                    calls.Add((command, parameters));
                    return Task.FromResult(queue.Dequeue());
                },
                async () => { output = JObject.Parse(await QueryTools.VerifyRunRules(BasicYaml)); });
            Assert.Equal(responses.Length, calls.Count);
            Assert.All(calls, c => Assert.Equal("verify_assert", c.Command));
            return output;
        }

        [Fact]
        public async Task All_pass_scores_100()
        {
            var output = await RunWith(
                AssertOk(true, "k", PassAssertion("count")),
                AssertOk(true, "k", PassAssertion("solid_volume")));

            Assert.True(output.Value<bool>("pass"));
            Assert.Equal(100, output.Value<int>("score"));
            Assert.Equal(2, output.Value<int>("rules_passed"));
            Assert.Equal("k", output.Value<string>("model_key"));
            Assert.False(output.Value<bool>("stale_refused"));
            Assert.Empty(output["skipped"]);
        }

        [Fact]
        public async Task Weighted_score_and_offender_union()
        {
            var output = await RunWith(
                AssertOk(true, null, PassAssertion("count")),
                AssertOk(false, null,
                    new JObject { ["kind"] = "solid_volume", ["pass"] = false, ["offending_ids"] = new JArray(9, 3, 9) },
                    new JObject { ["kind"] = "count", ["pass"] = false, ["missing"] = new JArray(7) }));

            Assert.False(output.Value<bool>("pass"));
            Assert.Equal(67, output.Value<int>("score"));
            var rule = output["rules"][1];
            Assert.Equal(new[] { 3L, 9L }, rule["offending_ids"].Select(t => t.Value<long>()).ToArray());
            Assert.Equal(new[] { 7L }, rule["missing_ids"].Select(t => t.Value<long>()).ToArray());
            Assert.False(rule.Value<bool>("offenders_truncated"));
        }

        [Fact]
        public async Task Stale_model_key_mismatch_refuses_and_skips_rest()
        {
            const string yaml = "version: 1\nmodel_key: pinned\nrules:\n  - id: first\n    assertions:\n      - kind: count\n  - id: volume_ok\n    assertions:\n      - kind: count\n";
            var calls = 0;
            JObject output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) =>
                {
                    calls++;
                    return Task.FromResult(AssertOk(true, "old", PassAssertion("count")));
                },
                async () => { output = JObject.Parse(await QueryTools.VerifyRunRules(yaml)); });

            Assert.Equal(1, calls);
            Assert.False(output.Value<bool>("pass"));
            Assert.True(output.Value<bool>("stale_refused"));
            Assert.Contains("stale model", output.Value<string>("error"));
            Assert.Equal(new[] { "volume_ok" }, output["skipped"].Select(t => t.Value<string>()).ToArray());
        }

        [Fact]
        public async Task Missing_key_with_pin_refuses()
        {
            const string yaml = "version: 1\nmodel_key: pinned\nrules:\n  - id: only\n    assertions:\n      - kind: count\n";
            JObject output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => Task.FromResult(AssertOk(true, null, PassAssertion("count"))),
                async () => { output = JObject.Parse(await QueryTools.VerifyRunRules(yaml)); });

            Assert.True(output.Value<bool>("stale_refused"));
            Assert.Contains("no model_key", output.Value<string>("error"));
        }

        [Fact]
        public async Task Stop_on_first_fail_skips_rest()
        {
            const string yaml = "version: 1\nrules:\n  - id: first\n    assertions:\n      - kind: count\n  - id: second\n    assertions:\n      - kind: count\n";
            var calls = 0;
            JObject output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) =>
                {
                    calls++;
                    return Task.FromResult(AssertOk(false, null,
                        new JObject { ["kind"] = "count", ["pass"] = false }));
                },
                async () => { output = JObject.Parse(await QueryTools.VerifyRunRules(yaml, true)); });

            Assert.Equal(1, calls);
            Assert.Equal(new[] { "second" }, output["skipped"].Select(t => t.Value<string>()).ToArray());
        }

        [Fact]
        public async Task Transport_failure_fails_rule_without_throwing()
        {
            JObject output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => throw new InvalidOperationException("pipe down"),
                async () => { output = JObject.Parse(await QueryTools.VerifyRunRules(BasicYaml)); });

            Assert.False(output.Value<bool>("pass"));
            Assert.Equal(0, output.Value<int>("score"));
            Assert.Contains("pipe down", output["rules"][0]["errors"][0].Value<string>("error"));
        }

        [Fact]
        public async Task Envelope_failure_becomes_rule_error()
        {
            JObject output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => Task.FromResult(new JObject { ["success"] = false, ["error"] = "no doc" }),
                async () => { output = JObject.Parse(await QueryTools.VerifyRunRules(BasicYaml)); });

            Assert.False(output.Value<bool>("pass"));
            Assert.Contains("no doc", output["rules"][0]["errors"][0].Value<string>("error"));
        }

        [Fact]
        public async Task Data_as_string_is_parsed()
        {
            var payload = new JObject { ["pass"] = true, ["model_key"] = "s", ["assertions"] = new JArray(PassAssertion("count")) };
            JObject output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => Task.FromResult(new JObject { ["success"] = true, ["data"] = payload.ToString() }),
                async () => { output = JObject.Parse(await QueryTools.VerifyRunRules(BasicYaml)); });

            Assert.True(output.Value<bool>("pass"));
            Assert.Equal("s", output.Value<string>("model_key"));
        }

        [Fact]
        public async Task Parse_error_returns_error_string_without_gateway_call()
        {
            var calls = 0;
            string output = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => { calls++; return Task.FromResult(new JObject()); },
                async () => { output = await QueryTools.VerifyRunRules("probe"); });

            Assert.Equal(0, calls);
            Assert.StartsWith("Error:", output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Works_in_read_only_mode()
        {
            var original = ServerState.Config;
            ServerState.Config = new RvtMcpConfig { ReadOnly = true };
            try
            {
                var output = await RunWith(
                    AssertOk(true, null, PassAssertion("count")),
                    AssertOk(true, null, PassAssertion("count")));
                Assert.True(output.Value<bool>("pass"));
            }
            finally
            {
                ServerState.Config = original;
            }
        }
    }
}
