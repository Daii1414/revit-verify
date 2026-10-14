using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CadWallAuditPolicyTests
    {
        [Theory]
        [InlineData(0, 0, 0, 5000, 0, 0, false)]
        [InlineData(5000, 0, 0, 0, 0, 0, true)]
        public void Matches_line_endpoints_in_either_direction(double ax1, double ay1, double az1,
            double ax2, double ay2, double az2, bool expectedReversed)
        {
            var matched = CadWallAuditPolicy.TryMatch(0, 0, 0, 5000, 0, 0,
                ax1, ay1, az1, ax2, ay2, az2, 1, out var delta, out var reversed);

            Assert.True(matched);
            Assert.Equal(0, delta, 3);
            Assert.Equal(expectedReversed, reversed);
        }

        [Fact]
        public void Rejects_line_when_one_endpoint_is_outside_tolerance()
        {
            var matched = CadWallAuditPolicy.TryMatch(0, 0, 0, 5000, 0, 0,
                0, 0, 0, 5000, 0, 2, 1, out var delta, out _);

            Assert.False(matched);
            Assert.Equal(2, delta, 3);
        }

        [Theory]
        [InlineData(200, 202, 2, true)]
        [InlineData(200, 202.001, 2, false)]
        [InlineData(0, 0, 0, false)]
        public void Compares_positive_wall_thickness_with_inclusive_tolerance(
            double expected, double actual, double tolerance, bool expectedWithin)
        {
            var within = CadWallAuditPolicy.WithinThickness(expected, actual, tolerance, out var delta);

            Assert.Equal(expectedWithin, within);
            Assert.Equal(System.Math.Abs(expected - actual), delta);
        }
    }
}
