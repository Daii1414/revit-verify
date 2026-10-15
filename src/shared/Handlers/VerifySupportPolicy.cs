using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Conservative support verdict per element. Only definite breakage fails:
    /// a hostable family instance without a live host, or a dangling level reference.
    /// This is a hosting/reference heuristic, not a structural analysis.</summary>
    internal static class VerifySupportPolicy
    {
        internal static bool TryEvaluate(bool levelDangling, bool hostRequired, bool hostMissing,
            out bool pass, out string status, out string error)
        {
            pass = false;
            status = "unsupported";
            error = null;
            if (levelDangling || (hostRequired && hostMissing))
                return true;
            pass = true;
            status = "supported";
            return true;
        }
    }
}
