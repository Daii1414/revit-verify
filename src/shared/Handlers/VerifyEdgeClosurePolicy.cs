using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Conservative verdict for Revit solid boundary-edge incidence.</summary>
    internal static class VerifyEdgeClosurePolicy
    {
        private const double MinimumVolumeFt3 = 1e-9;

        internal static bool TryEvaluate(int solidCount, int faceCount, int edgeCount,
            int unpairedEdgeCount, int indeterminateCount, double absoluteVolumeFt3,
            out bool pass, out string status, out string error)
        {
            pass = false;
            status = "unverified";
            error = null;
            if (solidCount < 0 || faceCount < 0 || edgeCount < 0 || unpairedEdgeCount < 0 || indeterminateCount < 0
                || !Finite(absoluteVolumeFt3) || absoluteVolumeFt3 < 0
                || unpairedEdgeCount > edgeCount || indeterminateCount > edgeCount)
            {
                error = "Geometry inspection returned invalid counts or volume.";
                return false;
            }
            if (solidCount == 0 || faceCount == 0 || edgeCount == 0 || absoluteVolumeFt3 <= MinimumVolumeFt3)
                return true;
            if (indeterminateCount > 0)
                return true;
            if (unpairedEdgeCount > 0)
            {
                status = "open";
                return true;
            }
            pass = true;
            status = "edge_closed";
            return true;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
