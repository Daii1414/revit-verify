using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Pure orientation and coordinate checks shared by the Revit CAD audit.</summary>
    internal static class CadAxisAuditPolicy
    {
        internal static string ClassifyLine(double x1, double y1, double x2, double y2,
            double angleToleranceDegrees, out double coordinate)
        {
            coordinate = 0;
            if (!Finite(x1) || !Finite(y1) || !Finite(x2) || !Finite(y2)
                || !Finite(angleToleranceDegrees) || angleToleranceDegrees <= 0 || angleToleranceDegrees > 10)
                return null;
            var dx = x2 - x1;
            var dy = y2 - y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (!Finite(length) || length <= 1e-6) return null;
            var drift = length * Math.Tan(angleToleranceDegrees * Math.PI / 180.0);
            if (Math.Abs(dx) <= drift)
            {
                coordinate = (x1 + x2) / 2;
                return "x";
            }
            if (Math.Abs(dy) <= drift)
            {
                coordinate = (y1 + y2) / 2;
                return "y";
            }
            return null;
        }

        internal static bool WithinTolerance(double expected, double actual, double tolerance, out double delta)
        {
            delta = Math.Abs(expected - actual);
            return Finite(expected) && Finite(actual) && Finite(tolerance)
                && tolerance >= 0 && delta <= tolerance;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
