using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using RvtMcp.Plugin.Handlers;
using RvtMcp.Server.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CadWallCandidateBuilderTests
    {
        [Fact]
        public void Builds_candidate_from_paired_parallel_boundaries_with_provenance()
        {
            var extraction = Extraction(
                Line(44, "WALL", 0, 0, 5000, 0),
                Line(44, "WALL", 0, 200, 5000, 200));

            var plan = CadPlanBuilder.Build(extraction.ToString(Formatting.None), Rules().ToString(Formatting.None));
            var candidate = plan["wall_candidates"][0];

            Assert.Single(plan["wall_candidates"]);
            Assert.Equal("candidate_unverified", candidate.Value<string>("status"));
            Assert.Equal(200, candidate.Value<double>("thickness_mm"), 3);
            Assert.Equal(5000, candidate.Value<double>("length_mm"), 3);
            Assert.Equal(100, candidate["start_mm"].Value<double>("y_mm"), 3);
            Assert.Equal(100, candidate["end_mm"].Value<double>("y_mm"), 3);
            Assert.Equal(2, candidate["provenance"].Count());
            Assert.Equal(44, candidate["provenance"][0].Value<long>("instance_id"));
            Assert.True(plan["wall_analysis"].Value<bool>("analysis_complete"));
            Assert.Empty(plan["wall_analysis"]["unpaired_segments"]);
            Assert.True(CadPlanIntegrity.Validate(plan, out var error), error);
            Assert.False(plan.Value<bool>("applies_changes"));
        }

        [Fact]
        public void Clips_candidate_to_the_overlapping_part_of_boundary_segments()
        {
            var extraction = Extraction(
                Line(44, "WALL", 0, 0, 5000, 0),
                Line(44, "WALL", 1000, 200, 4000, 200));

            var plan = CadPlanBuilder.Build(extraction.ToString(Formatting.None), Rules().ToString(Formatting.None));
            var candidate = plan["wall_candidates"][0];

            Assert.Equal(3000, candidate.Value<double>("length_mm"), 3);
            Assert.Equal(1000, candidate["start_mm"].Value<double>("x_mm"), 3);
            Assert.Equal(4000, candidate["end_mm"].Value<double>("x_mm"), 3);
        }

        [Fact]
        public void Does_not_pair_lines_from_different_cad_instances()
        {
            var extraction = new JObject
            {
                ["results"] = new JArray(
                    Instance(44, Line(44, "WALL", 0, 0, 5000, 0)),
                    Instance(45, Line(45, "WALL", 0, 200, 5000, 200)))
            };

            var plan = CadPlanBuilder.Build(extraction.ToString(Formatting.None), Rules().ToString(Formatting.None));

            Assert.Empty(plan["wall_candidates"]);
        }

        [Fact]
        public void Rejects_truncated_or_missing_polyline_vertices_on_wall_layers()
        {
            var truncated = Extraction(new JObject
            {
                ["layer"] = "WALL", ["type"] = "polyline", ["vertices_truncated"] = true,
                ["vertices_mm"] = new JArray(Point(0, 0), Point(0, 5000))
            });
            var missingVertices = Extraction(new JObject { ["layer"] = "WALL", ["type"] = "polyline" });

            var truncatedError = Assert.Throws<System.ArgumentException>(() => CadPlanBuilder.Build(
                truncated.ToString(Formatting.None), Rules().ToString(Formatting.None)));
            var verticesError = Assert.Throws<System.ArgumentException>(() => CadPlanBuilder.Build(
                missingVertices.ToString(Formatting.None), Rules().ToString(Formatting.None)));

            Assert.Contains("truncated", truncatedError.Message);
            Assert.Contains("include_vertices=true", verticesError.Message);
        }

        [Fact]
        public void Wall_rules_require_a_bounded_nominal_thickness()
        {
            var rules = Rules();
            rules.Remove("wall_thickness_mm");

            var error = Assert.Throws<System.ArgumentException>(() => CadPlanBuilder.Build(
                Extraction(Line(44, "WALL", 0, 0, 5000, 0)).ToString(Formatting.None), rules.ToString(Formatting.None)));

            Assert.Contains("wall_thickness_mm", error.Message);
        }

        [Fact]
        public void Reports_unpaired_lines_and_unsupported_geometry_instead_of_claiming_complete_analysis()
        {
            var extraction = Extraction(
                Line(44, "WALL", 0, 0, 5000, 0),
                new JObject { ["layer"] = "WALL", ["type"] = "curve:Arc" });

            var plan = CadPlanBuilder.Build(extraction.ToString(Formatting.None), Rules().ToString(Formatting.None));

            Assert.False(plan["wall_analysis"].Value<bool>("analysis_complete"));
            Assert.Single(plan["wall_analysis"]["unpaired_segments"]);
            Assert.Single(plan["wall_analysis"]["unsupported_entities"]);
            Assert.True(CadPlanIntegrity.Validate(plan, out var error), error);
        }

        [Fact]
        public void Marks_overlapping_boundary_pair_interpretations_as_ambiguous()
        {
            var extraction = Extraction(
                Line(44, "WALL", 0, 0, 5000, 0),
                Line(44, "WALL", 0, 200, 5000, 200),
                Line(44, "WALL", 0, 400, 5000, 400));

            var plan = CadPlanBuilder.Build(extraction.ToString(Formatting.None), Rules().ToString(Formatting.None));

            Assert.Equal(2, plan["wall_candidates"].Count());
            Assert.False(plan["wall_analysis"].Value<bool>("analysis_complete"));
            Assert.Equal(new[] { "candidate_ambiguous", "candidate_ambiguous" },
                plan["wall_candidates"].Values<string>("status"));
        }

        [Fact]
        public void Does_not_pair_boundaries_at_different_elevations()
        {
            var extraction = Extraction(
                Line(44, "WALL", 0, 0, 5000, 0),
                Line(44, "WALL", 0, 200, 5000, 200, 20));

            var plan = CadPlanBuilder.Build(extraction.ToString(Formatting.None), Rules().ToString(Formatting.None));

            Assert.Empty(plan["wall_candidates"]);
            Assert.False(plan["wall_analysis"].Value<bool>("analysis_complete"));
            Assert.Equal(2, plan["wall_analysis"]["unpaired_segments"].Count());
        }

        private static JObject Rules() => new JObject
        {
            ["version"] = "test-wall-v1",
            ["wall_layers"] = new JArray("WALL"),
            ["wall_thickness_mm"] = 200,
            ["wall_thickness_tolerance_mm"] = 5,
            ["minimum_wall_length_mm"] = 300
        };

        private static JObject Extraction(params JObject[] entities) => new JObject
        {
            ["requested"] = 1,
            ["returned"] = 1,
            ["results"] = new JArray(Instance(44, entities))
        };

        private static JObject Instance(long id, params JObject[] entities) => new JObject
        {
            ["instance_id"] = id,
            ["truncated"] = false,
            ["entities"] = new JArray(entities)
        };

        private static JObject Line(long id, string layer, double x1, double y1, double x2, double y2, double z = 0) => new JObject
        {
            ["instance_id"] = id,
            ["layer"] = layer,
            ["type"] = "curve:Line",
            ["start_mm"] = Point(x1, y1, z),
            ["end_mm"] = Point(x2, y2, z)
        };

        private static JObject Point(double x, double y, double z = 0) => new JObject
        {
            ["x_mm"] = x, ["y_mm"] = y, ["z_mm"] = z
        };
    }
}
