using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Pure geometric comparisons used to audit CAD wall candidates against Revit walls.</summary>
    internal static class CadWallAuditPolicy
    {
        internal static bool TryMatch(double ex1, double ey1, double ez1, double ex2, double ey2, double ez2,
            double ax1, double ay1, double az1, double ax2, double ay2, double az2,
            double tolerance, out double endpointDelta, out bool reversed)
        {
            endpointDelta = double.PositiveInfinity;
            reversed = false;
            if (!Finite(tolerance) || tolerance < 0) return false;
            if (!Finite(ex1) || !Finite(ey1) || !Finite(ez1) || !Finite(ex2) || !Finite(ey2) || !Finite(ez2)
                || !Finite(ax1) || !Finite(ay1) || !Finite(az1) || !Finite(ax2) || !Finite(ay2) || !Finite(az2)) return false;

            var direct = Math.Max(Distance(ex1, ey1, ez1, ax1, ay1, az1), Distance(ex2, ey2, ez2, ax2, ay2, az2));
            var reverse = Math.Max(Distance(ex1, ey1, ez1, ax2, ay2, az2), Distance(ex2, ey2, ez2, ax1, ay1, az1));
            reversed = reverse < direct;
            endpointDelta = Math.Min(direct, reverse);
            return endpointDelta <= tolerance;
        }

        internal static bool WithinThickness(double expectedMm, double actualMm, double toleranceMm, out double deltaMm)
        {
            deltaMm = Math.Abs(expectedMm - actualMm);
            return Finite(expectedMm) && expectedMm > 0 && Finite(actualMm) && actualMm > 0
                && Finite(toleranceMm) && toleranceMm >= 0 && deltaMm <= toleranceMm;
        }

        private static double Distance(double x1, double y1, double z1, double x2, double y2, double z2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            var dz = z1 - z2;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
