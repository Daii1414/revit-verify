using Newtonsoft.Json.Linq;
using RvtMcp.Server.Handlers;
using System.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class CadOpeningCandidateBuilderTests
    {
        private static JObject Rules()
        {
            return new JObject
            {
                ["door_layers"] = new JArray("A-DOOR"),
                ["window_layers"] = new JArray("A-GLAZ"),
            };
        }

        private static JObject Instance(params JObject[] entities)
        {
            return new JObject
            {
                ["instance_id"] = 11L,
                ["source_path"] = "plan.dwg",
                ["entities"] = new JArray(entities),
            };
        }

        private static JObject Arc(string layer, double cx, double cy, double radius, double sweep)
        {
            return new JObject
            {
                ["layer"] = layer,
                ["type"] = "curve:Arc",
                ["center_mm"] = new JObject { ["x_mm"] = cx, ["y_mm"] = cy },
                ["radius_mm"] = radius,
                ["sweep_degrees"] = sweep,
                ["start_mm"] = new JObject { ["x_mm"] = cx + radius, ["y_mm"] = cy },
                ["end_mm"] = new JObject { ["x_mm"] = cx, ["y_mm"] = cy + radius },
            };
        }

        private static JObject Line(string layer, double x1, double y1, double x2, double y2)
        {
            return new JObject
            {
                ["layer"] = layer,
                ["type"] = "curve:Line",
                ["start_mm"] = new JObject { ["x_mm"] = x1, ["y_mm"] = y1 },
                ["end_mm"] = new JObject { ["x_mm"] = x2, ["y_mm"] = y2 },
            };
        }

        [Fact]
        public void Door_swing_arc_becomes_candidate_with_provenance()
        {
            var results = new JArray(Instance(Arc("A-DOOR", 1000, 2000, 900, 90)));

            var analysis = CadOpeningCandidateBuilder.Build(results, Rules());

            Assert.True(analysis.Value<bool>("enabled"));
            Assert.Equal(1, analysis.Value<int>("door_count"));
            var door = analysis["door_candidates"][0];
            Assert.Equal("door", door.Value<string>("kind"));
            Assert.Equal(1000, door["center_mm"].Value<double>("x_mm"));
            Assert.Equal(2000, door["center_mm"].Value<double>("y_mm"));
            Assert.Equal(900, door.Value<double>("width_mm"));
            Assert.Equal(90, door.Value<double>("sweep_degrees"));
            Assert.Equal(0, door.Value<double>("leaf_angle_degrees"));
            Assert.Equal("A-DOOR", door.Value<string>("layer"));
            Assert.Equal(11, door.Value<long>("instance_id"));
            Assert.Equal("plan.dwg", door.Value<string>("source_path"));
            Assert.Equal(0, door.Value<int>("entity_index"));
        }

        [Fact]
        public void Full_circle_and_out_of_range_arcs_are_unsupported()
        {
            var results = new JArray(Instance(
                Arc("A-DOOR", 0, 0, 900, 360),
                Arc("A-DOOR", 0, 0, 300, 90),
                new JObject { ["layer"] = "A-DOOR", ["type"] = "curve:Arc" }));

            var analysis = CadOpeningCandidateBuilder.Build(results, Rules());

            Assert.Equal(0, analysis.Value<int>("door_count"));
            Assert.Equal(3, analysis.Value<int>("unsupported_count"));
            Assert.Contains(analysis["unsupported_entities"].Select(e => e.Value<string>("reason")),
                r => r.Contains("Full-circle"));
        }

        [Fact]
        public void Window_pair_becomes_candidate_lone_line_is_unsupported()
        {
            var results = new JArray(Instance(
                Line("A-GLAZ", 0, 0, 1200, 0),
                Line("A-GLAZ", 0, 150, 1200, 150),
                Line("A-GLAZ", 5000, 0, 5800, 0)));

            var analysis = CadOpeningCandidateBuilder.Build(results, Rules());

            Assert.Equal(1, analysis.Value<int>("window_count"));
            var window = analysis["window_candidates"][0];
            Assert.Equal("window", window.Value<string>("kind"));
            Assert.Equal(600, window["center_mm"].Value<double>("x_mm"));
            Assert.Equal(75, window["center_mm"].Value<double>("y_mm"));
            Assert.Equal(1200, window.Value<double>("width_mm"));
            Assert.Equal(150, window.Value<double>("gap_mm"));
            Assert.Equal(1, analysis.Value<int>("unsupported_count"));
        }

        [Fact]
        public void Unconfigured_layers_disable_and_other_layers_are_ignored()
        {
            var results = new JArray(Instance(
                Arc("A-WALL", 0, 0, 900, 90),
                Line("A-WALL", 0, 0, 1200, 0)));

            var disabled = CadOpeningCandidateBuilder.Build(results, new JObject());
            Assert.False(disabled.Value<bool>("enabled"));
            Assert.Equal(0, disabled.Value<int>("door_count"));

            var analysis = CadOpeningCandidateBuilder.Build(results, Rules());
            Assert.Equal(0, analysis.Value<int>("door_count"));
            Assert.Equal(0, analysis.Value<int>("window_count"));
            Assert.Equal(0, analysis.Value<int>("unsupported_count"));
        }

        [Fact]
        public void Rejects_inconsistent_rules()
        {
            var results = new JArray();
            var rules = Rules();
            rules["door_sweep_degrees_min"] = 200;
            rules["door_sweep_degrees_max"] = 100;

            var ex = Assert.Throws<System.ArgumentException>(
                () => CadOpeningCandidateBuilder.Build(results, rules));
            Assert.Contains("sweep", ex.Message);
        }
    }
}
