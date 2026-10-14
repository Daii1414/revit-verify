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
