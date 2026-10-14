namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Pure validation/evaluation for exact or inclusive count assertions.</summary>
    internal static class VerifyCountPolicy
    {
        internal static bool TryEvaluate(int actual, int? expect, int? minExpect, int? maxExpect,
            out bool pass, out string error)
        {
            pass = false;
            error = null;
            var hasRange = minExpect.HasValue || maxExpect.HasValue;
            if (expect.HasValue && hasRange)
            {
                error = "Use either expect or min_expect/max_expect, not both.";
                return false;
            }
            if (!expect.HasValue && !hasRange)
            {
                error = "expect or at least one of min_expect/max_expect is required.";
                return false;
            }
            if ((expect.HasValue && expect.Value < 0)
                || (minExpect.HasValue && minExpect.Value < 0)
                || (maxExpect.HasValue && maxExpect.Value < 0))
            {
                error = "Count expectations must be non-negative.";
                return false;
            }
            if (minExpect.HasValue && maxExpect.HasValue && minExpect.Value > maxExpect.Value)
            {
                error = "min_expect must be less than or equal to max_expect.";
                return false;
            }

            pass = expect.HasValue
                ? actual == expect.Value
                : (!minExpect.HasValue || actual >= minExpect.Value)
                    && (!maxExpect.HasValue || actual <= maxExpect.Value);
            return true;
        }
    }
}
