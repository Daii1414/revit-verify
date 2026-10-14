using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Server.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CadPlanBuilderTests
    {
        [Fact]
        public void Builds_sorted_labeled_axes_with_source_provenance_and_stable_hashes()
        {
            var extraction = new JObject
            {
                ["results"] = new JArray(new JObject
                {
                    ["instance_id"] = 501,
                    ["source_path"] = @"D:\plans\church.dwg",
                    ["truncated"] = false,
                    ["entities"] = new JArray(
                        Line("GRID-X", 11620, 0, 11620, 4820),
                        Line("GRID-X", 0, 0, 0, 4820),
                        Line("GRID-Y", 0, 4820, 11620, 4820),
                        Line("GRID-Y", 0, 0, 11620, 0),
                        Line("WALL", 100, 100, 500, 100))
                })
            };
            var rules = new JObject
            {
                ["version"] = "church-axes-v1",
                ["x_axis_layers"] = new JArray("GRID-X"),
                ["y_axis_layers"] = new JArray("GRID-Y"),
                ["x_axis_labels"] = new JArray("A", "B"),
                ["y_axis_labels"] = new JArray("1", "2"),
                ["minimum_axis_length_mm"] = 1000
            };
            var extractionJson = extraction.ToString(Formatting.None);
            var rulesJson = rules.ToString(Formatting.None);

            var plan = CadPlanBuilder.Build(extractionJson, rulesJson);

            Assert.Equal(1, plan.Value<int>("plan_version"));
            Assert.Equal("church-axes-v1", plan.Value<string>("rules_version"));
            Assert.Equal(64, plan.Value<string>("rules_sha256").Length);
            Assert.Equal(64, plan.Value<string>("extraction_sha256").Length);
            Assert.False(plan.Value<bool>("applies_changes"));
            Assert.Equal(new[] { "A", "B" }, plan["x_axes"].Values<string>("label"));
            Assert.Equal(0, plan["x_axes"][0].Value<double>("coordinate_mm"));
            Assert.Equal(11620, plan["x_axes"][1].Value<double>("coordinate_mm"));
            Assert.Equal(new[] { "1", "2" }, plan["y_axes"].Values<string>("label"));
            Assert.Equal(501, plan["x_axes"][0]["provenance"][0].Value<long>("instance_id"));
            Assert.Equal(1, plan["x_axes"][0]["provenance"][0].Value<int>("entity_index"));
            Assert.Equal(1, plan["x_axes"][0].Value<int>("segment_count"));
        }

        [Fact]
        public void Rejects_truncated_extraction_instead_of_building_an_incomplete_plan()
        {
            var extraction = "{\"results\":[{\"truncated\":true,\"entities\":[]}] }";
            var rules = "{\"version\":\"v1\",\"x_axis_layers\":[\"GRID\"]}";

            var error = Assert.Throws<System.ArgumentException>(() => CadPlanBuilder.Build(extraction, rules));

            Assert.Contains("truncated", error.Message);
        }

        [Fact]
        public void Rejects_label_count_mismatch_and_non_line_geometry()
        {
            var extraction = new JObject
            {
                ["results"] = new JArray(new JObject
                {
                    ["truncated"] = false,
                    ["entities"] = new JArray(
                        new JObject { ["layer"] = "GRID", ["type"] = "curve:Arc", ["start_mm"] = Point(0, 0), ["end_mm"] = Point(0, 5000) },
                        Line("GRID", 1000, 0, 1000, 5000))
                })
            };
            var rules = new JObject { ["version"] = "v1", ["x_axis_layers"] = new JArray("GRID"), ["x_axis_labels"] = new JArray("A", "B") };

            var error = Assert.Throws<System.ArgumentException>(() => CadPlanBuilder.Build(
                extraction.ToString(Formatting.None), rules.ToString(Formatting.None)));

            Assert.Contains("labels", error.Message);
        }

        [Theory]
        [InlineData("{\"results\":[{\"truncated\":false,\"entities\":[]}],\"requested\":2,\"returned\":1,\"failed\":[{}]}", "failed instances")]
        [InlineData("{\"results\":[{\"truncated\":false,\"entities\":[{\"layer\":\"GRID\",\"type\":\"polyline\",\"vertices_truncated\":true,\"vertices_mm\":[{\"x_mm\":0,\"y_mm\":0},{\"x_mm\":0,\"y_mm\":5000}]}]}]}", "vertices are truncated")]
        public void Rejects_partial_instance_or_vertex_data(string extraction, string expectedError)
        {
            var rules = "{\"version\":\"v1\",\"x_axis_layers\":[\"GRID\"]}";

            var error = Assert.Throws<System.ArgumentException>(() => CadPlanBuilder.Build(extraction, rules));

            Assert.Contains(expectedError, error.Message);
        }

        private static JObject Line(string layer, double x1, double y1, double x2, double y2) => new JObject
        {
            ["layer"] = layer,
            ["type"] = "curve:Line",
            ["start_mm"] = Point(x1, y1),
            ["end_mm"] = Point(x2, y2)
        };

        private static JObject Point(double x, double y) => new JObject { ["x_mm"] = x, ["y_mm"] = y, ["z_mm"] = 0 };
    }
}
