using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class UndoBatchHandler : IRevitCommand
    {
        public string Name => "undo_batch";
        public string Description => "List recorded write batches or undo the newest one (created elements are deleted). Refuses when the model was saved since the batch. Dry-run by default; nothing is touched until dry_run=false.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""operation"":{""type"":""string"",""enum"":[""list"",""undo_last""],""default"":""list""},""dry_run"":{""type"":""boolean"",""default"":true}},""required"":[]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var op = (req.Value<string>("operation") ?? "list").ToLowerInvariant();
            if (op != "list" && op != "undo_last")
                return CommandResult.Fail("operation must be list or undo_last.");

            var path = UndoJournal.PathFor(doc.Title);
            var batches = UndoJournal.Load(path);
            var stamp = SaveStamp(doc);

            if (op == "list")
            {
                var rows = batches.AsEnumerable().Reverse().Select(b => new
                {
                    batch_id = b.Id,
                    created_utc = b.CreatedUtc,
                    tool = b.Tool,
                    tracked = b.Entries.SelectMany(e => e.ElementIds).Count(),
                    undone = b.Undone,
                    saved_since = b.SaveStamp != stamp
                }).ToList();
                var last = batches.LastOrDefault(b => !b.Undone);
                return CommandResult.Ok(new
                {
                    document = doc.Title,
                    batches = rows,
                    undo_last_available = last != null && last.SaveStamp == stamp,
                    note = "Only the newest non-undone batch is undoable; a save since the batch blocks it."
                });
            }

            var dryRun = req.Value<bool?>("dry_run") ?? true;
            var target = batches.LastOrDefault(b => !b.Undone);
            if (target == null)
                return CommandResult.Fail("Nothing to undo: no recorded batches.");
            if (target.SaveStamp != stamp)
                return CommandResult.Fail("Nothing was undone: the model was saved or synced since batch " + target.Id + ". Undo refuses to run on a moved baseline.");

            var ids = target.Entries.SelectMany(e => e.ElementIds).Distinct().ToList();
            var missing = ids.Where(id => doc.GetElement(ToId(id)) == null).ToList();
            if (dryRun)
                return CommandResult.Ok(new
                {
                    dry_run = true,
                    batch_id = target.Id,
                    verdict = "undoable",
                    would_delete = ids.Count,
                    already_gone = missing.Count,
                    note = "Nothing was changed. Re-run with dry_run=false to apply."
                });

            List<long> deleted;
            using (var tx = new Transaction(doc, "RvtVerify: Undo batch"))
            {
                tx.Start();
                try
                {
                    deleted = doc.Delete(ids.Select(ToId).ToList())
                        .Select(e => RevitCompat.GetId(e)).ToList();
                    var beyond = deleted.Where(id => !ids.Contains(id)).ToList();
                    if (beyond.Count > 0)
                        throw new InvalidOperationException("deleting would also remove " + beyond.Count +
                            " element(s) the batch did not create (" + string.Join(", ", beyond.Take(10)) + ").");
                    foreach (var id in ids)
                        if (doc.GetElement(ToId(id)) != null)
                            throw new InvalidOperationException("element " + id + " still present after delete.");
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Undo failed, nothing was undone: " + ex.Message);
                }
            }

            target.Undone = true;
            try
            {
                batches[batches.FindIndex(b => b.Id == target.Id)] = target;
                UndoJournal.Save(path, batches);
            }
            catch { }
            return CommandResult.Ok(new
            {
                dry_run = false,
                batch_id = target.Id,
                deleted = deleted.Count,
                skipped_missing = missing.Count
            });
        }

        private static ElementId ToId(long id) => RevitCompat.ToElementId(id);

        private static string SaveStamp(Document doc)
        {
            try
            {
                var v = Document.GetDocumentVersion(doc);
                if (v == null) return "unsaved";
                return v.VersionGUID + "#" + v.NumberOfSaves.ToString(CultureInfo.InvariantCulture);
            }
            catch { return "unsaved"; }
        }
    }
}
