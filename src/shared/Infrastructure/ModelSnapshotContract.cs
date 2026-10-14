using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Versioned integrity contract for portable, privacy-minimized model snapshots.</summary>
    public static class ModelSnapshotContract
    {
        public const int SnapshotVersion = 1;
        public const string GeometryMethod = "revit-triangulated-geometry-0.5mm-v1";
        public const int MaxElements = 500;

        public static JObject Create(string modelKey, string identityKind, string revitVersion,
            JObject scope, JArray elements, bool complete)
        {
            if (!IsSha256(modelKey)) throw new ArgumentException("modelKey must be a SHA-256 identity key.");
            if (string.IsNullOrWhiteSpace(identityKind)) throw new ArgumentException("identityKind is required.");
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (elements == null || elements.Count > MaxElements) throw new ArgumentException("elements must contain at most 500 entries.");

            var snapshot = new JObject
            {
                ["snapshot_version"] = SnapshotVersion,
                ["geometry_method"] = GeometryMethod,
                ["model_key"] = modelKey.ToLowerInvariant(),
                ["identity_kind"] = identityKind,
                ["revit_version"] = revitVersion ?? string.Empty,
                ["captured_utc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["scope"] = scope.DeepClone(),
                ["element_count"] = elements.Count,
                ["complete"] = complete && elements.OfType<JObject>().All(x => x.Value<string>("geometry_status") == "verified"),
                ["elements"] = elements.DeepClone(),
                ["limitations"] = new JArray(
                    "Geometry fingerprints use Revit's tessellated display geometry, quantized to 0.5 mm; they are evidence for comparison, not exact B-rep equivalence.",
                    "Only the stated element or category scope is compared. Element-ID scope cannot detect newly added elements outside that set.",
                    "Parameter values, view appearance, joins, and design intent are not included.")
            };
            snapshot["snapshot_sha256"] = Hash(snapshot.ToString(Formatting.None));
            return snapshot;
        }

        public static bool Validate(JObject snapshot, out string error)
        {
            error = null;
            if (snapshot == null || snapshot.Value<int?>("snapshot_version") != SnapshotVersion
                || snapshot.Value<string>("geometry_method") != GeometryMethod)
            { error = "snapshot_json must use the supported snapshot_version and geometry_method."; return false; }
            if (!IsSha256(snapshot.Value<string>("model_key")) || string.IsNullOrWhiteSpace(snapshot.Value<string>("identity_kind")))
            { error = "snapshot_json is missing a stable model identity."; return false; }
            if (!(snapshot["scope"] is JObject) || !(snapshot["elements"] is JArray elements)
                || elements.Count > MaxElements || snapshot.Value<int?>("element_count") != elements.Count
                || snapshot["complete"]?.Type != JTokenType.Boolean || !IsSha256(snapshot.Value<string>("snapshot_sha256")))
            { error = "snapshot_json has an invalid scope, element count, completeness flag, or hash."; return false; }

            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var token in elements)
            {
                var element = token as JObject;
                var uniqueId = element?.Value<string>("unique_id");
                var status = element?.Value<string>("geometry_status");
                if (element == null || string.IsNullOrWhiteSpace(uniqueId) || !ids.Add(uniqueId)
                    || (status != "verified" && status != "unverified"))
                { error = "snapshot_json contains an invalid or duplicate element record."; return false; }
                if (status == "verified" && !IsSha256(element.Value<string>("geometry_sha256")))
                { error = "snapshot_json marks geometry verified without a valid geometry hash."; return false; }
            }

            var suppliedHash = snapshot.Value<string>("snapshot_sha256");
            var copy = (JObject)snapshot.DeepClone();
            copy.Remove("snapshot_sha256");
            if (!string.Equals(Hash(copy.ToString(Formatting.None)), suppliedHash, StringComparison.OrdinalIgnoreCase))
            { error = "snapshot_json content does not match snapshot_sha256."; return false; }
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
