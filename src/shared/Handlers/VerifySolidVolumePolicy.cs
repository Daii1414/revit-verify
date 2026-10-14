using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Pure validation/evaluation for per-element solid volume assertions.</summary>
    internal static class VerifySolidVolumePolicy
    {
        internal static bool TryEvaluate(double volumeM3, int solidCount, double? minVolumeM3, double? maxVolumeM3,
            out bool pass, out string error)
        {
            pass = false;
            error = null;
            if (!minVolumeM3.HasValue && !maxVolumeM3.HasValue)
            {
                error = "min_volume_m3 or max_volume_m3 is required.";
                return false;
            }
            if (solidCount < 0)
            {
                error = "solid_count must be non-negative.";
                return false;
            }
            if (!IsFiniteNonNegative(volumeM3)
                || (minVolumeM3.HasValue && !IsFiniteNonNegative(minVolumeM3.Value))
                || (maxVolumeM3.HasValue && !IsFiniteNonNegative(maxVolumeM3.Value)))
            {
                error = "Solid volumes must be finite and non-negative.";
                return false;
            }
            if (minVolumeM3.HasValue && maxVolumeM3.HasValue && minVolumeM3.Value > maxVolumeM3.Value)
            {
                error = "min_volume_m3 must be less than or equal to max_volume_m3.";
                return false;
            }

            pass = solidCount > 0
                && (!minVolumeM3.HasValue || volumeM3 >= minVolumeM3.Value)
                && (!maxVolumeM3.HasValue || volumeM3 <= maxVolumeM3.Value);
            return true;
        }

        private static bool IsFiniteNonNegative(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
    }
}
