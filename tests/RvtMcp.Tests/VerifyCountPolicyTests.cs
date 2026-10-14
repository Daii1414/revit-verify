using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class VerifyCountPolicyTests
    {
        [Theory]
        [InlineData(3, 3, true)]
        [InlineData(2, 3, false)]
        public void Exact_expectation_is_compared(int actual, int expected, bool expectedPass)
        {
            var valid = VerifyCountPolicy.TryEvaluate(actual, expected, null, null, out var pass, out var error);

            Assert.True(valid, error);
            Assert.Equal(expectedPass, pass);
        }

        [Theory]
        [InlineData(2, 2, 4, true)]
        [InlineData(4, 2, 4, true)]
        [InlineData(5, 2, 4, false)]
        [InlineData(1, 2, 4, false)]
        public void Inclusive_range_checks_both_bounds(int actual, int min, int max, bool expectedPass)
        {
            var valid = VerifyCountPolicy.TryEvaluate(actual, null, min, max, out var pass, out var error);

            Assert.True(valid, error);
            Assert.Equal(expectedPass, pass);
        }

        [Fact]
        public void One_sided_ranges_are_supported()
        {
            Assert.True(VerifyCountPolicy.TryEvaluate(3, null, 2, null, out var minimumPass, out _));
            Assert.True(minimumPass);
            Assert.True(VerifyCountPolicy.TryEvaluate(3, null, null, 3, out var maximumPass, out _));
            Assert.True(maximumPass);
        }

        [Theory]
        [InlineData(3, 3, 2, 4, "Use either expect or min_expect/max_expect, not both.")]
        [InlineData(3, null, null, null, "expect or at least one of min_expect/max_expect is required.")]
        [InlineData(3, null, 5, 2, "min_expect must be less than or equal to max_expect.")]
        [InlineData(3, -1, null, null, "Count expectations must be non-negative.")]
        public void Invalid_expectations_are_rejected(int actual, int? expected, int? min, int? max, string expectedError)
        {
            var valid = VerifyCountPolicy.TryEvaluate(actual, expected, min, max, out _, out var error);

            Assert.False(valid);
            Assert.Equal(expectedError, error);
        }
    }
}
