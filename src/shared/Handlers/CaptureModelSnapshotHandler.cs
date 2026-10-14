using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Captures a bounded, portable before-state from a saved, stably identified model.</summary>
    public sealed class CaptureModelSnapshotHandler : IRevitCommand
    {
        private const int MaxExplicitIds = 100;
        private const int MaxCategories = 20;

        public string Name => "capture_model_snapshot";
        public string Description => "Capture a read-only, hashed element snapshot from a saved model. No file or model changes.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 100 },
    ""category_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 20 }
  },
  ""description"": ""Provide exactly one scope: explicit element_ids (cannot discover later additions) or category_ids (detects additions and removals; hard cap 500 model elements).""
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var explicitToken = request["element_ids"] as JArray;
            var categoryToken = request["category_ids"] as JArray;
            if ((explicitToken == null) == (categoryToken == null))
                return CommandResult.Fail("Provide exactly one non-empty scope: element_ids or category_ids.");

            var identity = McpChangeTracker.Scope.Identity(doc);
            var modelKey = identity?.Value<string>("key");
            var identityKind = identity?.Value<string>("kind");
            if (string.IsNullOrWhiteSpace(modelKey) || string.IsNullOrWhiteSpace(identityKind))
                return CommandResult.Fail("A stable saved-model identity is unavailable. Save the model; detached and unsaved models cannot be snapshotted.");

            List<Element> elements;
            JObject scope;
            try
            {
                if (explicitToken != null)
                {
                    if (!ReadIds(explicitToken, MaxExplicitIds, out var ids, out var error)) return CommandResult.Fail(error);
                    elements = new List<Element>();
                    foreach (var id in ids)
                    {
                        var element = doc.GetElement(RevitCompat.ToElementId(id));
                        if (element == null) return CommandResult.Fail("Element ID " + id + " does not exist in the active model.");
                        elements.Add(element);
                    }
                    scope = new JObject { ["mode"] = "element_ids", ["element_ids"] = new JArray(ids) };
                }
                else
                {
                    if (!ReadIds(categoryToken, MaxCategories, out var categoryIds, out var error)) return CommandResult.Fail(error);
                    var categoryElements = categoryIds.Select(RevitCompat.ToElementId).ToList();
                    var collector = new FilteredElementCollector(doc)
                        .WherePasses(new ElementMulticategoryFilter(categoryElements))
                        .WhereElementIsNotElementType();
                    var count = collector.GetElementCount();
                    if (count == 0) return CommandResult.Fail("The category scope contains no model elements.");
                    if (count > ModelSnapshotContract.MaxElements)
                        return CommandResult.Fail("The category scope contains " + count + " elements; the snapshot limit is " + ModelSnapshotContract.MaxElements + ". Narrow the category scope.");
                    elements = collector.ToElements().OrderBy(x => x.UniqueId, StringComparer.Ordinal).ToList();
                    scope = new JObject { ["mode"] = "category_ids", ["category_ids"] = new JArray(categoryIds) };
                }
            }
            catch (Exception ex) { return CommandResult.Fail("Could not resolve the requested snapshot scope: " + ex.Message); }

            var records = new JArray();
            foreach (var element in elements.OrderBy(x => x.UniqueId, StringComparer.Ordinal))
            {
                try { records.Add(ModelSnapshotGeometry.Capture(doc, element)); }
                catch
                {
                    records.Add(new JObject
                    {
                        ["unique_id"] = element.UniqueId,
                        ["element_id"] = RevitCompat.GetId(element.Id),
                        ["category_id"] = element.Category == null ? JValue.CreateNull() : new JValue(RevitCompat.GetId(element.Category.Id)),
                        ["category"] = element.Category?.Name,
                        ["type_unique_id"] = JValue.CreateNull(),
                        ["name"] = string.Empty,
                        ["bounding_box_mm"] = JValue.CreateNull(),
                        ["geometry_status"] = "unverified",
                        ["geometry_sha256"] = JValue.CreateNull(),
                        ["triangle_count"] = 0,
                        ["curve_segment_count"] = 0,
                        ["geometry_warning"] = "element_capture_failed"
                    });
                }
            }

            var complete = records.OfType<JObject>().All(x => x.Value<string>("geometry_status") == "verified");
            var snapshot = ModelSnapshotContract.Create(modelKey, identityKind, doc.Application.VersionNumber, scope, records, complete);
            return CommandResult.Ok(snapshot);
        }

        internal static bool ReadIds(JArray tokens, int maximum, out List<long> ids, out string error)
        {
            ids = new List<long>();
            error = null;
            if (tokens == null || tokens.Count == 0 || tokens.Count > maximum)
            { error = "The ID scope must contain between 1 and " + maximum + " entries."; return false; }
            var seen = new HashSet<long>();
            foreach (var token in tokens)
            {
                if (token.Type != JTokenType.Integer)
                { error = "Each scope ID must be an integer."; return false; }
                var id = token.Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                { error = RevitCompat.ElementIdRangeError(id); return false; }
                if (!seen.Add(id))
                { error = "Scope IDs must be unique."; return false; }
                ids.Add(id);
            }
            return true;
        }
    }
}
