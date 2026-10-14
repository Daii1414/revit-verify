using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Compares a bounded snapshot with the active model without modifying it.</summary>
    public sealed class CompareModelSnapshotHandler : IRevitCommand
    {
        public string Name => "compare_model_snapshot";
        public string Description => "Compare a portable model snapshot with the active model. Read-only.";
        public string ParametersSchema => "{\"type\":\"object\",\"properties\":{\"snapshot_json\":{\"type\":\"string\",\"description\":\"Complete JSON returned by capture_model_snapshot.\"}},\"required\":[\"snapshot_json\"]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            JObject request;
            try { request = JObject.Parse(paramsJson ?? "{}"); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            var snapshotText = request.Value<string>("snapshot_json");
            if (string.IsNullOrWhiteSpace(snapshotText) || snapshotText.Length > 4 * 1024 * 1024)
                return CommandResult.Fail("snapshot_json is required and must be at most 4 MiB.");
            JObject baseline;
            try { baseline = JObject.Parse(snapshotText); }
            catch (Exception ex) { return CommandResult.Fail("snapshot_json is not valid JSON: " + ex.Message); }
            if (!ModelSnapshotContract.Validate(baseline, out var validationError)) return CommandResult.Fail(validationError);
            if (!string.Equals(baseline.Value<string>("revit_version"), doc.Application.VersionNumber, StringComparison.Ordinal))
                return CommandResult.Fail("Snapshot Revit version differs from the active model; recapture with the same Revit version.");
            var identity = McpChangeTracker.Scope.Identity(doc);
            if (identity == null || !string.Equals(identity.Value<string>("key"), baseline.Value<string>("model_key"), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(identity.Value<string>("kind"), baseline.Value<string>("identity_kind"), StringComparison.Ordinal))
                return CommandResult.Fail("Snapshot belongs to a different or unidentifiable model.");

            var baselineElements = (JArray)baseline["elements"];
            var oldByUid = baselineElements.Cast<JObject>().ToDictionary(x => x.Value<string>("unique_id"), StringComparer.Ordinal);
            var currentElements = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var mode = baseline["scope"].Value<string>("mode");
            if (mode == "category_ids")
            {
                var categoryIds = baseline["scope"]["category_ids"] as JArray;
                if (!CaptureModelSnapshotHandler.ReadIds(categoryIds, 20, out var ids, out var idError)) return CommandResult.Fail("Snapshot scope is invalid: " + idError);
                var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(ids.Select(RevitCompat.ToElementId).ToList())).WhereElementIsNotElementType();
                var count = collector.GetElementCount();
                if (count > ModelSnapshotContract.MaxElements) return CommandResult.Fail("Current category scope exceeds the 500 element comparison limit; narrow and recapture.");
                foreach (var element in collector)
                    currentElements[element.UniqueId] = CaptureModelSnapshot(element);
            }
            else if (mode == "element_ids")
            {
                foreach (var old in oldByUid.Values)
                {
                    var element = doc.GetElement(old.Value<string>("unique_id"));
                    if (element != null) currentElements[element.UniqueId] = CaptureModelSnapshot(element);
                }
            }
            else return CommandResult.Fail("Snapshot scope mode is not supported.");

            var findings = new JArray();
            var counts = new Dictionary<string, int> { ["added"] = 0, ["removed"] = 0, ["modified"] = 0, ["unchanged"] = 0, ["unverified"] = 0 };
            foreach (var uid in oldByUid.Keys.Union(currentElements.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                oldByUid.TryGetValue(uid, out var old);
                currentElements.TryGetValue(uid, out var current);
                string status;
                if (old == null) status = "added";
                else if (current == null) status = "removed";
                else if (old.Value<string>("geometry_status") != "verified" || current.Value<string>("geometry_status") != "verified") status = "unverified";
                else if (Equivalent(old, current)) status = "unchanged";
                else status = "modified";
                counts[status]++;
                if (status != "unchanged") findings.Add(new JObject
                {
                    ["status"] = status,
                    ["unique_id"] = uid,
                    ["baseline_element_id"] = old?["element_id"]?.DeepClone(),
                    ["current_element_id"] = current?["element_id"]?.DeepClone(),
                    ["before"] = old == null ? null : Summary(old),
                    ["after"] = current == null ? null : Summary(current)
                });
            }
            // Explicit-ID scope cannot prove that no elements were added outside the captured set.
            var complete = mode == "category_ids" && baseline.Value<bool>("complete") && counts["unverified"] == 0;
            var addedDetectable = mode == "category_ids";
            return CommandResult.Ok(new JObject
            {
                ["baseline_sha256"] = baseline.Value<string>("snapshot_sha256"),
                ["scope_mode"] = mode,
                ["added_detectable"] = addedDetectable,
                ["comparison_complete"] = complete,
                ["pass"] = complete ? new JValue(findings.Count == 0) : JValue.CreateNull(),
                ["counts"] = JObject.FromObject(counts),
                ["findings"] = findings,
                ["limitations"] = new JArray("Tessellated geometry hashes are approximate, not exact B-rep equivalence.", "Parameter values, appearance, joins, and design intent are not compared.")
            });
        }

        private static JObject CaptureModelSnapshot(Element element)
        {
            // The geometry reader does not need document access for element fingerprints.
            // Resolve the type through the owning document.
            return ModelSnapshotGeometry.Capture(element.Document, element);
        }

        private static bool Equivalent(JObject a, JObject b)
        {
            foreach (var key in new[] { "geometry_sha256", "category_id", "type_unique_id", "name", "bounding_box_mm" })
                if (!JToken.DeepEquals(a[key], b[key])) return false;
            return true;
        }

        private static JObject Summary(JObject element) => new JObject
        {
            ["category_id"] = element["category_id"]?.DeepClone(),
            ["type_unique_id"] = element["type_unique_id"]?.DeepClone(),
            ["name"] = element["name"]?.DeepClone(),
            ["bounding_box_mm"] = element["bounding_box_mm"]?.DeepClone(),
            ["geometry_status"] = element["geometry_status"]?.DeepClone(),
            ["geometry_sha256"] = element["geometry_sha256"]?.DeepClone()
        };
    }
}
