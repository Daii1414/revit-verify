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
        public void Exact_camera_up_is_projected_onto_the_view_plane()
        {
            var up = FrameCalculator.ComputeUp(new[] { 0.0, 1.0, 0.0 }, new[] { 0.0, 1.0, 1.0 });
            Assert.Equal(1.0, FrameCalculator.Length(up), 9);
            Assert.Equal(0.0, FrameCalculator.Dot(up, new[] { 0.0, 1.0, 0.0 }), 9);
            Assert.Equal(1.0, up[2], 9);
        }

        [Fact]
        public void Exact_camera_up_parallel_to_forward_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => FrameCalculator.ComputeUp(
                new[] { 0.0, 1.0, 0.0 }, new[] { 0.0, -2.0, 0.0 }));
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

        [Fact]
        public void Explicit_distance_beats_factor()
        {
            Assert.Equal(25000.0, FrameCalculator.ResolveDistance(40000.0, 1.5, 25000.0));
            Assert.Equal(60000.0, FrameCalculator.ResolveDistance(40000.0, 1.5, null));
        }

        [Fact]
        public void Bad_distances_are_rejected()
        {
            Assert.Throws<ArgumentException>(() => FrameCalculator.ResolveDistance(40000.0, 1.5, 0.0));
            Assert.Throws<ArgumentException>(() => FrameCalculator.ResolveDistance(40000.0, 1.5, -1.0));
            Assert.Throws<ArgumentException>(() => FrameCalculator.ResolveDistance(40000.0, 0.0, null));
            Assert.Throws<ArgumentException>(() => FrameCalculator.ResolveDistance(0.0, 1.5, null));
        }
    }
}
