using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CadArcMetricsTests
    {
        [Theory]
        [InlineData(1.5707963267948966, 1, 90)]
        [InlineData(3.141592653589793, 2, 90)]
        [InlineData(4.71238898038469, 1, 270)]
        public void Computes_sweep_from_length_and_radius(double lengthFeet, double radiusFeet, double expectedDegrees)
        {
            var success = CadArcMetrics.TryGetSweepDegrees(lengthFeet, radiusFeet, false, out var actualDegrees);

            Assert.True(success);
            Assert.Equal(expectedDegrees, actualDegrees, 6);
        }

        [Fact]
        public void Cyclic_arc_is_a_full_circle()
        {
            var success = CadArcMetrics.TryGetSweepDegrees(0, 0, true, out var actualDegrees);

            Assert.True(success);
            Assert.Equal(360, actualDegrees);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        [InlineData(double.NaN, 1)]
        [InlineData(double.PositiveInfinity, 1)]
        public void Rejects_invalid_open_arc_measurements(double lengthFeet, double radiusFeet)
        {
            var success = CadArcMetrics.TryGetSweepDegrees(lengthFeet, radiusFeet, false, out var actualDegrees);

            Assert.False(success);
            Assert.Equal(0, actualDegrees);
        }
    }
}
