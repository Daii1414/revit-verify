using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Converts Revit arc length and radius to an explicit angular sweep.</summary>
    internal static class CadArcMetrics
    {
        internal static bool TryGetSweepDegrees(double lengthFeet, double radiusFeet, bool isCyclic, out double sweepDegrees)
        {
            sweepDegrees = 0;
            if (isCyclic)
            {
                sweepDegrees = 360;
                return true;
            }

            if (!Finite(lengthFeet) || !Finite(radiusFeet) || lengthFeet <= 0 || radiusFeet <= 0)
                return false;

            var sweep = lengthFeet / radiusFeet * 180.0 / Math.PI;
            if (!Finite(sweep) || sweep <= 0 || sweep > 360.000001)
                return false;

            sweepDegrees = Math.Min(sweep, 360.0);
            return true;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
