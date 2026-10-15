using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class RenumberElementsHandler : IRevitCommand
    {
        private const int DefaultMax = 200;

        public string Name => "renumber_elements";
        public string Description => "Assign sequential marks to doors, windows or rooms (Mark for doors/windows, Number for rooms), ordered by level then plan position. Dry run by default. Restores with Revit Undo.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""category"":{""type"":""string"",""enum"":[""Doors"",""Windows"",""Rooms""]},""prefix"":{""type"":""string"",""default"":""""},""start"":{""type"":""integer"",""default"":1},""step"":{""type"":""integer"",""default"":1},""dry_run"":{""type"":""boolean"",""default"":true},""max_count"":{""type"":""integer"",""default"":200}},""required"":[""category""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject req;
            try { req = JObject.Parse(paramsJson ?? "{}"); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var category = (req.Value<string>("category") ?? "").Trim();
            BuiltInCategory bic;
            BuiltInParameter markParam;
            if (string.Equals(category, "Doors", StringComparison.OrdinalIgnoreCase))
            {
                bic = BuiltInCategory.OST_Doors;
                markParam = BuiltInParameter.ALL_MODEL_MARK;
            }
            else if (string.Equals(category, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                bic = BuiltInCategory.OST_Windows;
                markParam = BuiltInParameter.ALL_MODEL_MARK;
            }
            else if (string.Equals(category, "Rooms", StringComparison.OrdinalIgnoreCase))
            {
                bic = BuiltInCategory.OST_Rooms;
                markParam = BuiltInParameter.ROOM_NUMBER;
            }
            else
            {
                return CommandResult.Fail("category must be Doors, Windows or Rooms.");
            }

            var prefix = req.Value<string>("prefix") ?? "";
            var start = req.Value<int?>("start") ?? 1;
            var step = req.Value<int?>("step") ?? 1;
            if (start < 0) return CommandResult.Fail("start must be >= 0.");
            if (step < 1) return CommandResult.Fail("step must be >= 1.");
            var dryRun = req.Value<bool?>("dry_run") ?? true;
            var maxCount = req.Value<int?>("max_count") ?? DefaultMax;
            if (maxCount < 1) return CommandResult.Fail("max_count must be >= 1.");

            Category target;
            try { target = Category.GetCategory(doc, bic); }
            catch { return CommandResult.Fail("Category is not available in this model."); }
            var elements = new FilteredElementCollector(doc)
                .OfCategoryId(target.Id)
                .WhereElementIsNotElementType()
                .ToList();
            if (elements.Count > maxCount)
                return CommandResult.Fail("Scope contains " + elements.Count + " elements (max_count=" + maxCount + "). Narrow it or raise max_count explicitly.");

            var items = new List<RenumberPlanner.Item>();
            var unreadable = new List<long>();
            foreach (var element in elements)
            {
                if (!TryPosition(doc, element, out var levelMm, out var xMm, out var yMm))
                {
                    unreadable.Add(RevitCompat.GetId(element.Id));
                    continue;
                }
                items.Add(new RenumberPlanner.Item
                {
                    Id = RevitCompat.GetId(element.Id),
                    LevelMm = levelMm,
                    XMm = xMm,
                    YMm = yMm
                });
            }

            List<Tuple<long, string>> plan;
            try { plan = RenumberPlanner.Plan(items, prefix, start, step); }
            catch (Exception ex) { return CommandResult.Fail("Planning failed: " + ex.Message); }

            var byId = elements.ToDictionary(e => RevitCompat.GetId(e.Id));
            var writable = new List<Tuple<long, string, string>>();
            var skipped = new List<object>();
            foreach (var entry in plan)
            {
                var element = byId[entry.Item1];
                var parameter = element.get_Parameter(markParam);
                string old = null;
                var ok = false;
                try
                {
                    if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.String)
                    {
                        ok = true;
                        try { old = parameter.AsString(); } catch { }
                    }
                }
                catch { ok = false; }
                if (!ok)
                    skipped.Add(new { id = entry.Item1, reason = parameter == null ? "mark parameter not found" : "mark parameter is read-only or not text" });
                else
                    writable.Add(Tuple.Create(entry.Item1, old, entry.Item2));
            }
            var preview = writable.Select(w => new { id = w.Item1, old = w.Item2, @new = w.Item3 }).ToList<object>();

            if (dryRun)
                return CommandResult.Ok(new
                {
                    dry_run = true,
                    category,
                    planned = preview.Count,
                    skipped = skipped.Count,
                    assignments = preview,
                    skipped_ids = skipped,
                    unreadable_ids = unreadable
                });

            var applied = 0;
            var failed = new List<object>();
            using (var tx = new Transaction(doc, "RvtVerify: Renumber " + category))
            {
                tx.Start();
                try
                {
                    foreach (var assignment in writable)
                    {
                        try
                        {
                            byId[assignment.Item1].get_Parameter(markParam).Set(assignment.Item3);
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { id = assignment.Item1, error = ex.Message });
                        }
                    }
                    tx.Commit();
                }
                catch
                {
                    if (tx.HasStarted()) tx.RollBack();
                    throw;
                }
            }
            return CommandResult.Ok(new
            {
                dry_run = false,
                category,
                applied,
                failed,
                skipped = skipped.Count,
                skipped_ids = skipped,
                unreadable_ids = unreadable
            });
        }

        private static bool TryPosition(Document doc, Element element, out double levelMm, out double xMm, out double yMm)
        {
            levelMm = 0;
            xMm = 0;
            yMm = 0;
            try
            {
                try
                {
                    var levelId = element.LevelId;
                    if (levelId != null && levelId != ElementId.InvalidElementId)
                    {
                        var level = doc.GetElement(levelId) as Level;
                        if (level != null) levelMm = level.Elevation * 304.8;
                    }
                }
                catch { }
                var location = element.Location;
                if (location is LocationPoint point)
                {
                    xMm = point.Point.X * 304.8;
                    yMm = point.Point.Y * 304.8;
                    return true;
                }
                if (location is LocationCurve curve)
                {
                    var mid = (curve.Curve.GetEndPoint(0) + curve.Curve.GetEndPoint(1)) * 0.5;
                    xMm = mid.X * 304.8;
                    yMm = mid.Y * 304.8;
                    return true;
                }
                var box = element.get_BoundingBox(null);
                if (box == null) return false;
                xMm = (box.Min.X + box.Max.X) * 0.5 * 304.8;
                yMm = (box.Min.Y + box.Max.Y) * 0.5 * 304.8;
                return true;
            }
            catch { return false; }
        }
    }
}
