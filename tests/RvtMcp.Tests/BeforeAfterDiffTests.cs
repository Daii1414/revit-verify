using System.Drawing;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class BeforeAfterDiffTests
    {
        private static Bitmap Solid(int w, int h, Color color)
        {
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
                g.Clear(color);
            return bmp;
        }

        private static JObject Camera(double dx)
        {
            return new JObject
            {
                ["eye"] = new JObject { ["x_mm"] = dx, ["y_mm"] = 0.0, ["z_mm"] = 0.0 },
                ["target"] = new JObject { ["x_mm"] = 0.0, ["y_mm"] = 0.0, ["z_mm"] = 0.0 },
                ["up"] = new JObject { ["x_mm"] = 0.0, ["y_mm"] = 0.0, ["z_mm"] = 1.0 },
            };
        }

        [Fact]
        public void Identical_images_report_zero()
        {
            using (var a = Solid(64, 48, Color.FromArgb(10, 20, 30)))
            using (var b = Solid(64, 48, Color.FromArgb(10, 20, 30)))
            {
                var diff = BeforeAfterDiff.DiffImages(a, b, false, false);

                Assert.Equal("identical", diff.Value<string>("status"));
                Assert.Equal(0L, diff.Value<long>("pixels_changed"));
                Assert.Equal(0.0, diff.Value<double>("percent_changed"));
            }
        }

        [Fact]
        public void Changed_block_is_counted_with_stats()
        {
            using (var a = Solid(64, 48, Color.White))
            using (var b = Solid(64, 48, Color.White))
            {
                for (var x = 0; x < 16; x++)
                    for (var y = 0; y < 12; y++)
                        b.SetPixel(x, y, Color.Black);

                var diff = BeforeAfterDiff.DiffImages(a, b, false, false);

                Assert.Equal("different", diff.Value<string>("status"));
                Assert.Equal(192L, diff.Value<long>("pixels_changed"));
                Assert.Equal(6.25, diff.Value<double>("percent_changed"));
                Assert.True(diff.Value<double>("mean_abs_diff") > 0);
                Assert.False(diff.Value<bool>("cross_format"));
            }
        }

        [Fact]
        public void Sub_threshold_noise_is_ignored()
        {
            using (var a = Solid(32, 32, Color.FromArgb(100, 100, 100)))
            using (var b = Solid(32, 32, Color.FromArgb(100, 100, 100)))
            {
                b.SetPixel(0, 0, Color.FromArgb(105, 100, 100)); // max channel diff 5 < 8

                var diff = BeforeAfterDiff.DiffImages(a, b, false, false);

                Assert.Equal("identical", diff.Value<string>("status"));
            }
        }

        [Fact]
        public void Dimension_mismatch_and_camera_move_are_unverified()
        {
            using (var a = Solid(64, 48, Color.White))
            using (var b = Solid(32, 32, Color.White))
            {
                var diff = BeforeAfterDiff.DiffImages(a, b, false, false);

                Assert.Equal("unverified", diff.Value<string>("status"));
                Assert.Contains("dimensions differ", diff.Value<string>("reason"));
            }

            using (var a = Solid(32, 32, Color.White))
            using (var b = Solid(32, 32, Color.White))
            {
                var diff = BeforeAfterDiff.DiffImages(a, b, true, true);

                Assert.Equal("unverified", diff.Value<string>("status"));
                Assert.Contains("camera moved", diff.Value<string>("reason"));
            }
        }

        [Fact]
        public void Baseline_age_and_staleness()
        {
            var now = new System.DateTime(2026, 10, 15, 12, 0, 0, System.DateTimeKind.Utc);
            Assert.Equal(2.5, BeforeAfterDiff.BaselineAgeHours("2026-10-15T09:30:00Z", now));
            Assert.Null(BeforeAfterDiff.BaselineAgeHours("not-a-date", now));
            Assert.Null(BeforeAfterDiff.BaselineAgeHours(null, now));
            Assert.False(BeforeAfterDiff.IsStale(5.0, 0));
            Assert.False(BeforeAfterDiff.IsStale(1.0, 2.0));
            Assert.True(BeforeAfterDiff.IsStale(3.0, 2.0));
            Assert.True(BeforeAfterDiff.IsStale(null, 2.0));
        }

        [Fact]
        public void Camera_distance_uses_millimetre_tolerance()
        {
            Assert.Equal(0.0, BeforeAfterDiff.CameraDistanceMm(Camera(0), Camera(0)));
            Assert.Equal(0.5, BeforeAfterDiff.CameraDistanceMm(Camera(0.5), Camera(0)));
            Assert.True(BeforeAfterDiff.CameraDistanceMm(Camera(2), Camera(0)) > BeforeAfterDiff.CameraToleranceMm);
            Assert.Equal(double.PositiveInfinity, BeforeAfterDiff.CameraDistanceMm(new JObject(), Camera(0)));
        }

        [Fact]
        public void Spec_compare_lists_every_drift()
        {
            var baseline = new JObject
            {
                ["view_id"] = 7L,
                ["pixel_size"] = 1200,
                ["image_format"] = "png",
                ["element_ids"] = new JArray(1, 2),
            };

            Assert.Empty(BeforeAfterDiff.CompareSpec(baseline, 7, 1200, "png", new[] { 2L, 1L }));
            Assert.Equal(
                new[] { "view_id", "pixel_size", "image_format", "element_ids" },
                BeforeAfterDiff.CompareSpec(baseline, 9, 800, "jpeg", new[] { 1L, 3L }).ToArray());
        }
    }
}
