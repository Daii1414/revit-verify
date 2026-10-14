using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class VerifySolidVolumePolicyTests
    {
        [Theory]
        [InlineData(1.0, 1.0, 2.0, true)]
        [InlineData(2.0, 1.0, 2.0, true)]
        [InlineData(2.01, 1.0, 2.0, false)]
        [InlineData(0.99, 1.0, 2.0, false)]
        public void Inclusive_volume_range_is_evaluated(double actual, double min, double max, bool expectedPass)
        {
            var valid = VerifySolidVolumePolicy.TryEvaluate(actual, 1, min, max, out var pass, out var error);

            Assert.True(valid, error);
            Assert.Equal(expectedPass, pass);
        }

        [Fact]
        public void One_sided_volume_range_is_supported()
        {
            Assert.True(VerifySolidVolumePolicy.TryEvaluate(2, 1, 1, null, out var minimumPass, out _));
            Assert.True(minimumPass);
            Assert.True(VerifySolidVolumePolicy.TryEvaluate(2, 1, null, 2, out var maximumPass, out _));
            Assert.True(maximumPass);
        }

        [Theory]
        [InlineData(1.0, null, null, "min_volume_m3 or max_volume_m3 is required.")]
        [InlineData(2.0, 3.0, 1.0, "min_volume_m3 must be less than or equal to max_volume_m3.")]
        [InlineData(2.0, -1.0, null, "Solid volumes must be finite and non-negative.")]
        public void Invalid_volume_contracts_are_rejected(double actual, double? min, double? max, string expectedError)
        {
            var valid = VerifySolidVolumePolicy.TryEvaluate(actual, 1, min, max, out _, out var error);

            Assert.False(valid);
            Assert.Equal(expectedError, error);
        }

        [Fact]
        public void Non_finite_values_are_rejected()
        {
            var valid = VerifySolidVolumePolicy.TryEvaluate(double.PositiveInfinity, 1, 0, null, out _, out var error);

            Assert.False(valid);
            Assert.Equal("Solid volumes must be finite and non-negative.", error);
        }

        [Fact]
        public void Element_without_positive_solid_fails_even_when_zero_volume_is_allowed()
        {
            var valid = VerifySolidVolumePolicy.TryEvaluate(0, 0, 0, null, out var pass, out var error);

            Assert.True(valid, error);
            Assert.False(pass);
        }
    }
}
