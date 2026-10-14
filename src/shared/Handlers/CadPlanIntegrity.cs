using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Validates the versioned, content-hashed CAD plan contract before model comparison.</summary>
    internal static class CadPlanIntegrity
    {
        internal static bool Validate(JObject plan, out string error)
        {
            error = null;
            if (plan == null || plan.Value<int?>("plan_version") != 1 || plan.Value<bool?>("read_only") != true
                || plan.Value<bool?>("applies_changes") != false)
            { error = "plan_json must be a read-only, non-applying plan_version 1 result from revit_cad_plan."; return false; }
            if (string.IsNullOrWhiteSpace(plan.Value<string>("rules_version"))
                || !IsSha256(plan.Value<string>("rules_sha256"))
                || !IsSha256(plan.Value<string>("extraction_sha256"))
                || !IsSha256(plan.Value<string>("plan_sha256")))
            { error = "plan_json is missing its rules version or SHA-256 provenance fields."; return false; }
            var suppliedHash = plan.Value<string>("plan_sha256");
            var copy = (JObject)plan.DeepClone();
            copy.Remove("plan_sha256");
            if (!string.Equals(Hash(copy.ToString(Formatting.None)), suppliedHash, StringComparison.OrdinalIgnoreCase))
            { error = "plan_json content does not match plan_sha256; regenerate or recopy the complete plan."; return false; }
            var angle = plan.Value<double?>("angle_tolerance_degrees");
            if (!angle.HasValue || double.IsNaN(angle.Value) || double.IsInfinity(angle.Value) || angle <= 0 || angle > 10)
            { error = "plan_json.angle_tolerance_degrees must be greater than 0 and at most 10."; return false; }
            if (!(plan["x_axes"] is JArray) || !(plan["y_axes"] is JArray))
            { error = "plan_json must contain x_axes and y_axes arrays."; return false; }
            if (plan["wall_candidates"] != null && !(plan["wall_candidates"] is JArray))
            { error = "plan_json.wall_candidates must be an array."; return false; }
            if (plan["wall_analysis"] != null)
            {
                var wallAnalysis = plan["wall_analysis"] as JObject;
                var enabled = wallAnalysis?.Value<bool?>("enabled");
                var complete = wallAnalysis?.Value<bool?>("analysis_complete");
                var candidates = wallAnalysis?["wall_candidates"] as JArray;
                var unpaired = wallAnalysis?["unpaired_segments"] as JArray;
                var unsupported = wallAnalysis?["unsupported_entities"] as JArray;
                var ambiguous = wallAnalysis?["ambiguous_candidate_indices"] as JArray;
                if (wallAnalysis == null || (wallAnalysis["enabled"] != null && wallAnalysis["enabled"].Type != JTokenType.Boolean)
                    || wallAnalysis["analysis_complete"]?.Type != JTokenType.Boolean || !complete.HasValue || candidates == null
                    || unpaired == null || unsupported == null
                    || (plan["wall_candidates"] != null && !JToken.DeepEquals(plan["wall_candidates"], candidates)))
                { error = "plan_json.wall_analysis is missing required analysis arrays or does not match wall_candidates."; return false; }
                var analysisEnabled = enabled ?? (candidates.Count > 0 || unpaired.Count > 0 || unsupported.Count > 0 || (ambiguous?.Count ?? 0) > 0);
                if (ambiguous == null && analysisEnabled)
                { error = "plan_json.wall_analysis is missing ambiguous_candidate_indices."; return false; }
                if (!analysisEnabled && candidates.Count > 0)
                { error = "plan_json has wall candidates while wall analysis is disabled."; return false; }
                if (complete.Value && analysisEnabled
                    && (candidates.Count == 0 || unpaired.Count > 0 || unsupported.Count > 0 || ambiguous.Count > 0))
                { error = "plan_json marks a wall analysis complete despite empty, unpaired, unsupported, or ambiguous geometry."; return false; }
            }
            return true;
        }

        private static bool IsSha256(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
        }
    }
}
