using System;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class FrameCalculatorTests
    {
        [Fact]
        public void Iso_direction_gives_orthonormal_basis()
        {
            var dir = new[] { 1.0, -1.0, 0.7 };
            var fwd = FrameCalculator.ComputeForward(dir);
            var up = FrameCalculator.ComputeUp(fwd);
            Assert.Equal(1.0, FrameCalculator.Length(fwd), 9);
            Assert.Equal(1.0, FrameCalculator.Length(up), 9);
            Assert.Equal(0.0, FrameCalculator.Dot(fwd, up), 9);
            Assert.True(up[2] > 0.5); // roughly world-up
        }

        [Fact]
        public void Eye_lands_on_ray_at_given_distance()
        {
            var target = new[] { 15700.0, 7000.0, 3000.0 };
            var eye = FrameCalculator.ComputeEye(target, new[] { 1.0, -1.0, 0.7 }, 40000.0);
            var diff = new[] { eye[0] - target[0], eye[1] - target[1], eye[2] - target[2] };
            Assert.Equal(40000.0, FrameCalculator.Length(diff), 6);
            // eye is opposite the look direction
            Assert.True(eye[0] > target[0] && eye[1] < target[1] && eye[2] > target[2]);
        }

        [Fact]
        public void Vertical_direction_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => FrameCalculator.ComputeUp(new[] { 0.0, 0.0, -1.0 }));
        }

        [Fact]
        public void Zero_direction_and_bad_distance_are_rejected()
        {
            Assert.Throws<ArgumentException>(() => FrameCalculator.ComputeForward(new[] { 0.0, 0.0, 0.0 }));
            Assert.Throws<ArgumentException>(() => FrameCalculator.ComputeEye(
                new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, 0.0));
            Assert.Throws<ArgumentException>(() => FrameCalculator.ComputeEye(
                new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, -5.0));
        }
    }
}
