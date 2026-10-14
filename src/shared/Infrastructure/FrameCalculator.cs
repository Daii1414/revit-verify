using System;

namespace RvtMcp.Plugin
{
    /// <summary>Pure 3D-view framing math (no Revit API): unit-testable core
    /// behind set_view_orientation. All units are millimetres as doubles;
    /// callers convert to feet at the Revit boundary.</summary>
    public static class FrameCalculator
    {
        public static double[] ComputeForward(double[] direction)
        {
            var d = Normalize(direction);
            return new[] { -d[0], -d[1], -d[2] };
        }

        public static double[] ComputeUp(double[] forward)
        {
            // world-up reference; rejects (near-)vertical view directions
            var right = Cross(forward, new[] { 0.0, 0.0, 1.0 });
            if (Length(right) < 1e-9)
                throw new ArgumentException("direction must not be parallel to vertical.");
            right = Normalize(right);
            return Normalize(Cross(right, forward));
        }

        public static double[] ComputeEye(double[] targetMm, double[] direction, double distanceMm)
        {
            if (distanceMm <= 0)
                throw new ArgumentException("distance must be positive.");
            var fwd = ComputeForward(direction);
            return new[]
            {
                targetMm[0] - fwd[0] * distanceMm,
                targetMm[1] - fwd[1] * distanceMm,
                targetMm[2] - fwd[2] * distanceMm,
            };
        }

        public static double ResolveDistance(double sizeMm, double factor, double? explicitMm)
        {
            if (explicitMm.HasValue)
            {
                if (explicitMm.Value <= 0)
                    throw new ArgumentException("distance_mm must be positive.");
                return explicitMm.Value;
            }
            if (factor <= 0)
                throw new ArgumentException("distance_factor must be positive.");
            if (sizeMm <= 0)
                throw new ArgumentException("model size must be positive.");
            return sizeMm * factor;
        }

        public static double[] Normalize(double[] v)
        {
            var len = Length(v);
            if (len < 1e-12)
                throw new ArgumentException("direction must be non-zero.");
            return new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        public static double Length(double[] v) =>
            Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

        public static double Dot(double[] a, double[] b) =>
            a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        public static double[] Cross(double[] a, double[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0],
        };
    }
}
