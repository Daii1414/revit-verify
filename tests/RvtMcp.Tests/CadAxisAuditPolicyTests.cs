using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CadAxisAuditPolicyTests
    {
        [Theory]
        [InlineData(100, 0, 100, 5000, "x", 100)]
        [InlineData(0, 200, 11620, 200, "y", 200)]
        [InlineData(100, 0, 200, 5000, null, 0)]
        public void Classifies_only_straight_axis_oriented_lines(double x1, double y1, double x2, double y2,
            string expectedAxis, double expectedCoordinate)
        {
            var actual = CadAxisAuditPolicy.ClassifyLine(x1, y1, x2, y2, 1, out var coordinate);

            Assert.Equal(expectedAxis, actual);
            if (expectedAxis != null) Assert.Equal(expectedCoordinate, coordinate, 3);
        }

        [Theory]
        [InlineData(100, 110, 10, true)]
        [InlineData(100, 110.001, 10, false)]
        [InlineData(double.NaN, 10, 100, false)]
        public void Coordinate_comparison_is_inclusive_and_rejects_non_finite_values(
            double expected, double actual, double tolerance, bool expectedWithin)
        {
            var within = CadAxisAuditPolicy.WithinTolerance(expected, actual, tolerance, out var delta);

            Assert.Equal(expectedWithin, within);
            Assert.Equal(System.Math.Abs(expected - actual), delta);
        }
    }
}
