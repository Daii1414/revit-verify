using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DeleteElementHandler : IRevitCommand
    {
        public string Name => "delete_element";
        public string Description => "Delete elements by ID. Supports dry_run preview and max_count guard.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""dry_run"":{""type"":""boolean"",""default"":false,""description"":""Preview only: resolve IDs, delete nothing.""},""max_count"":{""type"":""integer"",""default"":100,""description"":""Refuse when more IDs are given. Set higher explicitly for bulk deletes.""}},""required"":[""elementIds""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var elementIds = request["elementIds"]?.ToObject<long[]>() ?? new long[0];

            if (elementIds.Length == 0)
                return CommandResult.Fail("elementIds array is required.");

            var dryRun = request.Value<bool?>("dry_run") ?? false;
            var maxCount = request.Value<int?>("max_count") ?? 100;
            if (maxCount <= 0)
                return CommandResult.Fail("max_count must be positive.");
            if (elementIds.Length > maxCount)
                return CommandResult.Fail($"Refusing to delete {elementIds.Length} elements (max_count={maxCount}). Narrow the set or raise max_count explicitly.");

            var resolved = new List<long>();
            var missing = new List<long>();
            foreach (var id in elementIds)
            {
                if (doc.GetElement(RevitCompat.ToElementId(id)) == null)
                    missing.Add(id);
                else
                    resolved.Add(id);
            }

            if (dryRun)
                return CommandResult.Ok(new
                {
                    dry_run = true,
                    would_delete = resolved.Count,
                    missing = missing.Count,
                    resolvedIds = resolved,
                    missingIds = missing
                });

            using (var tx = new Transaction(doc, "MCP: Delete elements"))
            {
                tx.Start();
                var deletedIds = new List<long>();
                var failedIds = new List<long>();
                var errors = new List<string>();

                foreach (var id in elementIds)
                {
                    try
                    {
                        var elId = RevitCompat.ToElementId(id);
                        if (doc.GetElement(elId) == null)
                        {
                            failedIds.Add(id);
                            errors.Add($"{id}: element not found");
                            continue;
                        }
                        doc.Delete(elId);
                        deletedIds.Add(id);
                    }
                    catch (Exception ex)
                    {
                        failedIds.Add(id);
                        errors.Add($"{id}: {ex.Message}");
                    }
                }

                tx.Commit();
                return CommandResult.Ok(new
                {
                    deleted = deletedIds.Count,
                    failed = failedIds.Count,
                    total = elementIds.Length,
                    deletedIds,
                    failedIds,
                    errors
                });
            }
        }
    }
}
