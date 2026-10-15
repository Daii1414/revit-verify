using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// A6 batch execution (aspect #3 §A6). Wraps N MCP commands in a single
    /// <see cref="TransactionGroup"/> so the whole batch resolves to one Revit undo
    /// step. Inner handlers each open their own <see cref="Transaction"/>; TransactionGroup
    /// permits that (nested Transactions are forbidden, but inner-tx-inside-tx-group is fine).
    ///
    /// Iteration + rollback-decision logic lives in <see cref="BatchExecutor"/> so it can
    /// be unit-tested without a live document. This handler owns only the TransactionGroup
    /// lifecycle + the final Assimilate/RollBack call based on the outcome.
    /// </summary>
    public class BatchExecuteHandler : IRevitCommand
    {
        private readonly CommandDispatcher _dispatcher;

        public BatchExecuteHandler(CommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public string Name => "batch_execute";
        public string Description => "Run multiple MCP commands in one Revit TransactionGroup (one undo step). Opt-in support_check evaluates hosting/level support on created elements and reports offender IDs without a second call.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""commands"":{""type"":""array"",""items"":{""type"":""object""}},""continueOnError"":{""type"":""boolean""},""support_check"":{""type"":""boolean"",""default"":false,""description"":""Evaluate support on created element IDs after commit (max 200, else skipped with a reason).""},""output"":{""type"":""string"",""enum"": [""inline"", ""file""],""default"":""inline""}},""required"":[""commands""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var commandsArr = request["commands"] as JArray;
            if (commandsArr == null || commandsArr.Count == 0)
                return CommandResult.Fail("'commands' must be a non-empty array.");

            if (commandsArr.Count > BatchExecutor.MaxCommands)
                return CommandResult.Fail(BatchExecutor.TooManyCommandsMessage(commandsArr.Count));

            var continueOnError = request.Value<bool?>("continueOnError") ?? false;
            var supportCheck = request.Value<bool?>("support_check") ?? false;

            using (var tg = new TransactionGroup(doc, "MCP: batch_execute"))
            {
                tg.Start();

                var outcome = BatchExecutor.Run(commandsArr, continueOnError, (cmdName, subParams) =>
                {
                    var handler = _dispatcher.GetCommand(cmdName);
                    if (handler == null) return BatchExecutor.InvokeResult.Unknown();
                    var r = handler.Execute(app, subParams);
                    return r.Success
                        ? BatchExecutor.InvokeResult.Ok(r.Data)
                        : BatchExecutor.InvokeResult.Fail(r.Error);
                }, _dispatcher.IsBakedCommand);

                bool rolledBack;
                if (outcome.AnyFailed && !continueOnError)
                {
                    tg.RollBack();
                    rolledBack = true;
                }
                else
                {
                    tg.Assimilate();
                    rolledBack = false;
                }

                object undo = null;
                if (!rolledBack)
                    undo = TryRecordUndo(doc, outcome);

                object support = null;
                if (!rolledBack && supportCheck)
                    support = TrySupportCheck(doc, outcome);

                return CommandResult.Ok(new { results = outcome.Results, rolledBack, undo, support });
            }
        }

        private static object TrySupportCheck(Document doc, BatchExecutor.Outcome outcome)
        {
            try
            {
                var created = CollectCreatedIds(outcome);
                if (created.Count == 0)
                    return new { checked_ids = 0, skipped = "no created elements to check" };
                if (created.Count > 200)
                    return new { checked_ids = 0, skipped = created.Count + " created elements exceed the 200-element bound; narrow the batch" };
                return VerifyAssertHandler.EvaluateSupportIds(doc, created);
            }
            catch (Exception ex)
            {
                return new { checked_ids = 0, error = ex.Message };
            }
        }

        private static List<long> CollectCreatedIds(BatchExecutor.Outcome outcome)
        {
            var created = new List<long>();
            foreach (var r in outcome.Results)
            {
                var dataProp = r?.GetType().GetProperty("data");
                var okProp = r?.GetType().GetProperty("ok");
                if (okProp == null || !(okProp.GetValue(r) is bool ok) || !ok) continue;
                created.AddRange(UndoJournal.ExtractCreatedIds(dataProp?.GetValue(r)));
            }
            return created.Distinct().ToList();
        }

        private static object TryRecordUndo(Document doc, BatchExecutor.Outcome outcome)
        {
            try
            {
                var created = CollectCreatedIds(outcome);
                if (created.Count == 0)
                    return new { recorded = false, reason = "no created elements to track" };
                var batch = UndoJournal.Record(doc.Title, SaveStamp(doc), "batch_execute", created);
                return new { recorded = true, batch_id = batch.Id, tracked = created.Count };
            }
            catch (Exception ex)
            {
                return new { recorded = false, reason = ex.Message };
            }
        }

        private static string SaveStamp(Document doc)
        {
            try
            {
                var v = Document.GetDocumentVersion(doc);
                if (v == null) return "unsaved";
                return v.VersionGUID + "#" + v.NumberOfSaves;
            }
            catch { return "unsaved"; }
        }
    }
}
