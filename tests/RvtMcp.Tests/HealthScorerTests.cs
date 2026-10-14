using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class HealthScorerTests
    {
        [Fact]
        public void Clean_model_scores_100_grade_A()
        {
            var score = HealthScorer.Score(0, 0, 0, 0, 0, false);

            Assert.Equal(100, score.Score);
            Assert.Equal("A", score.Grade);
            Assert.Empty(score.Deductions);
            Assert.NotEmpty(score.Recommendations);
        }

        [Fact]
        public void Each_error_costs_five_capped_at_thirty()
        {
            Assert.Equal(95, HealthScorer.Score(1, 0, 0, 0, 0, false).Score);
            Assert.Equal(70, HealthScorer.Score(6, 0, 0, 0, 0, false).Score);
            Assert.Equal(70, HealthScorer.Score(60, 0, 0, 0, 0, false).Score);
        }

        [Fact]
        public void Warnings_cost_one_per_ten_capped_at_twenty()
        {
            Assert.Equal(100, HealthScorer.Score(0, 9, 0, 0, 0, false).Score);
            Assert.Equal(99, HealthScorer.Score(0, 10, 0, 0, 0, false).Score);
            Assert.Equal(80, HealthScorer.Score(0, 500, 0, 0, 0, false).Score);
        }

        [Fact]
        public void Cad_links_and_in_place_families_are_capped()
        {
            var score = HealthScorer.Score(0, 0, 50, 50, 50, false);

            Assert.Equal(75, score.Score); // 10 + 5 + 10
            Assert.Equal("B", score.Grade);
        }

        [Fact]
        public void Score_floors_at_zero_with_grade_F()
        {
            var score = HealthScorer.Score(100, 1000, 100, 100, 100, false);

            Assert.Equal(25, score.Score); // 30 + 20 + 10 + 5 + 10 = 75
            Assert.Equal("F", score.Grade);
            Assert.Equal(5, score.Deductions.Count);
            Assert.NotEmpty(score.Recommendations);
        }

        [Fact]
        public void Grade_boundaries_match_documented_bands()
        {
            Assert.Equal("A", HealthScorer.Score(2, 0, 0, 0, 0, false).Grade); // 90
            Assert.Equal("B", HealthScorer.Score(5, 0, 0, 0, 0, false).Grade); // 75
            Assert.Equal("C", HealthScorer.Score(6, 100, 0, 0, 0, false).Grade); // 100-30-10 = 60
            Assert.Equal("D", HealthScorer.Score(6, 100, 0, 5, 0, false).Grade); // 100-30-10-5 = 55
        }

        [Fact]
        public void Truncation_adds_recommendation_without_changing_score()
        {
            var plain = HealthScorer.Score(0, 0, 0, 0, 0, false);
            var capped = HealthScorer.Score(0, 0, 0, 0, 0, true);

            Assert.Equal(plain.Score, capped.Score);
            Assert.Contains(capped.Recommendations, r => r.Contains("maxElements"));
        }
    }
}
