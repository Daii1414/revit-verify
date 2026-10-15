using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class VerifySupportPolicyTests
    {
        [Fact]
        public void Healthy_element_passes_as_supported()
        {
            Assert.True(VerifySupportPolicy.TryEvaluate(false, false, false, out var pass, out var status, out var error));
            Assert.True(pass);
            Assert.Equal("supported", status);
            Assert.Null(error);
        }

        [Fact]
        public void Hosted_instance_with_live_host_passes()
        {
            Assert.True(VerifySupportPolicy.TryEvaluate(false, true, false, out var pass, out var status, out _));
            Assert.True(pass);
            Assert.Equal("supported", status);
        }

        [Fact]
        public void Missing_host_fails()
        {
            Assert.True(VerifySupportPolicy.TryEvaluate(false, true, true, out var pass, out var status, out _));
            Assert.False(pass);
            Assert.Equal("unsupported", status);
        }

        [Fact]
        public void Dangling_level_fails_even_with_host()
        {
            Assert.True(VerifySupportPolicy.TryEvaluate(true, true, false, out var pass, out _, out _));
            Assert.False(pass);
            Assert.True(VerifySupportPolicy.TryEvaluate(true, false, false, out var plainPass, out _, out _));
            Assert.False(plainPass);
        }

        [Fact]
        public void Non_hosted_element_without_level_info_passes()
        {
            Assert.True(VerifySupportPolicy.TryEvaluate(false, false, true, out var pass, out _, out _));
            Assert.True(pass);
        }
    }
}
