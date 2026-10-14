using Newtonsoft.Json.Linq;
using System.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ModelSnapshotContractTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Fact]
        public void Snapshot_is_bound_to_identity_and_scope_without_exposing_path_or_title()
        {
            var snapshot = Snapshot(new JArray(Element("uid-1")), true);

            Assert.True(ModelSnapshotContract.Validate(snapshot, out var error), error);
            Assert.Equal(Hash, snapshot.Value<string>("model_key"));
            Assert.Equal("category_ids", snapshot["scope"].Value<string>("mode"));
            Assert.Null(snapshot["path"]);
            Assert.Null(snapshot["title"]);
        }

        [Fact]
        public void Tampered_snapshot_content_is_rejected()
        {
            var snapshot = Snapshot(new JArray(Element("uid-1")), true);
            snapshot["elements"][0]["category_id"] = 42;

            Assert.False(ModelSnapshotContract.Validate(snapshot, out var error));
            Assert.Contains("snapshot_sha256", error);
        }

        [Fact]
        public void Duplicate_unique_ids_are_rejected_even_with_a_recomputed_snapshot_hash()
        {
            var snapshot = Snapshot(new JArray(Element("same"), Element("same")), true);
            snapshot["snapshot_sha256"] = Rehash(snapshot);

            Assert.False(ModelSnapshotContract.Validate(snapshot, out var error));
            Assert.Contains("duplicate", error);
        }

        [Fact]
        public void Unverified_geometry_makes_snapshot_incomplete_but_remains_representable()
        {
            var element = Element("uid-1");
            element["geometry_status"] = "unverified";
            element["geometry_sha256"] = JValue.CreateNull();
            var snapshot = Snapshot(new JArray(element), true);

            Assert.False(snapshot.Value<bool>("complete"));
            Assert.True(ModelSnapshotContract.Validate(snapshot, out var error), error);
        }

        private static JObject Snapshot(JArray elements, bool complete) => ModelSnapshotContract.Create(
            Hash, "file", "2026", new JObject { ["mode"] = "category_ids", ["category_ids"] = new JArray(-2000011) }, elements, complete);

        private static JObject Element(string uniqueId) => new JObject
        {
            ["unique_id"] = uniqueId,
            ["category_id"] = -2000011,
            ["geometry_status"] = "verified",
            ["geometry_sha256"] = Hash
        };

        private static string Rehash(JObject snapshot)
        {
            var copy = (JObject)snapshot.DeepClone();
            copy.Remove("snapshot_sha256");
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(copy.ToString(Newtonsoft.Json.Formatting.None)))
                    .Select(b => b.ToString("x2")));
        }
    }
}
