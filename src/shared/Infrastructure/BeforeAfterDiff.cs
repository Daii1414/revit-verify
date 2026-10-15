using System;
using System.Globalization;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Pure before/after frame math (no Revit API): pixel diffs, camera and
    /// spec comparison. Unit-testable core behind capture_before_after.</summary>
    public static class BeforeAfterDiff
    {
        public const double CameraToleranceMm = 1.0;
        public const int PixelThreshold = 8;

        public static JObject DiffImages(Bitmap baseline, Bitmap after, bool crossFormat, bool? cameraMoved)
        {
            if (baseline == null || after == null)
                return Unverified("a frame image is missing.");
            if (baseline.Width != after.Width || baseline.Height != after.Height)
                return Unverified("dimensions differ (" + baseline.Width + "x" + baseline.Height + " vs " +
                    after.Width + "x" + after.Height + "); use the same pixel_size and framing.");
            if (cameraMoved == true)
                return Unverified("the 3D camera moved between slots; set the same eye/target/up before capturing after.");
            var changed = CountChangedPixels(baseline, after, out var mean);
            var total = (long)baseline.Width * baseline.Height;
            if (changed == 0)
                return new JObject { ["status"] = "identical", ["pixels_changed"] = 0, ["percent_changed"] = 0.0, ["mean_abs_diff"] = 0.0 };
            return new JObject
            {
                ["status"] = "different",
                ["pixels_changed"] = changed,
                ["percent_changed"] = Math.Round(100.0 * changed / total, 2),
                ["mean_abs_diff"] = Math.Round(mean, 3),
                ["cross_format"] = crossFormat,
            };
        }

        public static double? BaselineAgeHours(string capturedUtc, DateTime utcNow)
        {
            DateTime captured;
            if (string.IsNullOrWhiteSpace(capturedUtc)
                || !DateTime.TryParse(capturedUtc, null, DateTimeStyles.RoundtripKind, out captured))
                return null;
            return (utcNow - captured.ToUniversalTime()).TotalHours;
        }

        public static bool IsStale(double? ageHours, double maxAgeHours)
        {
            if (!(maxAgeHours > 0)) return false;
            if (!ageHours.HasValue) return true;
            return ageHours.Value > maxAgeHours;
        }

        public static JObject Unverified(string reason)
        {
            return new JObject { ["status"] = "unverified", ["reason"] = reason };
        }

        public static long CountChangedPixels(Bitmap a, Bitmap b, out double meanAbsDiff)
        {
            var rect = new Rectangle(0, 0, a.Width, a.Height);
            var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var stride = Math.Abs(da.Stride);
                var bytes = new byte[stride * a.Height];
                var other = new byte[stride * a.Height];
                Marshal.Copy(da.Scan0, bytes, 0, bytes.Length);
                Marshal.Copy(db.Scan0, other, 0, other.Length);
                long changed = 0;
                long sum = 0;
                for (var y = 0; y < a.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < a.Width; x++)
                    {
                        var i = row + x * 3;
                        var d0 = Math.Abs(bytes[i] - other[i]);
                        var d1 = Math.Abs(bytes[i + 1] - other[i + 1]);
                        var d2 = Math.Abs(bytes[i + 2] - other[i + 2]);
                        sum += d0 + d1 + d2;
                        if (Math.Max(d0, Math.Max(d1, d2)) > PixelThreshold) changed++;
                    }
                }
                meanAbsDiff = bytes.Length == 0 ? 0 : (double)sum / bytes.Length;
                return changed;
            }
            finally
            {
                a.UnlockBits(da);
                b.UnlockBits(db);
            }
        }

        public static double CameraDistanceMm(JObject current, JObject baseline)
        {
            try
            {
                var worst = 0.0;
                foreach (var key in new[] { "eye", "target", "up" })
                {
                    var a = current[key] as JObject;
                    var b = baseline[key] as JObject;
                    if (a == null || b == null) return double.PositiveInfinity;
                    foreach (var axis in new[] { "x_mm", "y_mm", "z_mm" })
                        worst = Math.Max(worst, Math.Abs(a.Value<double>(axis) - b.Value<double>(axis)));
                }
                return worst;
            }
            catch { return double.PositiveInfinity; }
        }

        public static List<string> CompareSpec(JObject baseline, long viewId, int pixelSize, string format, long[] elementIds)
        {
            var changed = new List<string>();
            if (baseline.Value<long?>("view_id") != viewId) changed.Add("view_id");
            if (baseline.Value<int?>("pixel_size") != pixelSize) changed.Add("pixel_size");
            if (!string.Equals(baseline.Value<string>("image_format"), format, StringComparison.OrdinalIgnoreCase))
                changed.Add("image_format");
            var baseIds = ((baseline["element_ids"] as JArray) ?? new JArray())
                .Select(t => t.Value<long>()).OrderBy(x => x).ToArray();
            if (!baseIds.SequenceEqual(elementIds.OrderBy(x => x).ToArray())) changed.Add("element_ids");
            return changed;
        }
    }
}
