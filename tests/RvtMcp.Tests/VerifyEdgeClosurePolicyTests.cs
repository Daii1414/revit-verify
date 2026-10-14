using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class VerifyEdgeClosurePolicyTests
    {
        [Fact]
        public void Paired_edges_and_positive_volume_are_reported_as_edge_closed()
        {
            var valid = VerifyEdgeClosurePolicy.TryEvaluate(1, 6, 12, 0, 0, 1, out var pass, out var status, out var error);

            Assert.True(valid, error);
            Assert.True(pass);
            Assert.Equal("edge_closed", status);
        }

        [Fact]
        public void An_unpaired_edge_is_reported_open()
        {
            var valid = VerifyEdgeClosurePolicy.TryEvaluate(1, 5, 9, 1, 0, 1, out var pass, out var status, out var error);

            Assert.True(valid, error);
            Assert.False(pass);
            Assert.Equal("open", status);
        }

        [Theory]
        [InlineData(0, 0, 0, 0, 0, 0)]
        [InlineData(1, 6, 12, 0, 1, 1)]
        [InlineData(1, 6, 12, 0, 0, 0)]
        public void Missing_or_unreadable_geometry_stays_unverified(int solids, int faces, int edges,
            int unpaired, int indeterminate, double volume)
        {
            var valid = VerifyEdgeClosurePolicy.TryEvaluate(solids, faces, edges, unpaired, indeterminate,
                volume, out var pass, out var status, out var error);

            Assert.True(valid, error);
            Assert.False(pass);
            Assert.Equal("unverified", status);
        }

        [Fact]
        public void Impossible_edge_counts_are_rejected()
        {
            var valid = VerifyEdgeClosurePolicy.TryEvaluate(1, 4, 2, 3, 0, 1, out _, out _, out var error);

            Assert.False(valid);
            Assert.Contains("invalid counts", error);
        }
    }
}
