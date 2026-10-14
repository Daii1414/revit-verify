using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CheckModelHealthHandler : IRevitCommand
    {
        private const int DefaultCap = 100_000;

        public string Name => "check_model_health";
        public string Description => "Single-call model health: warnings by severity, element/CAD/link/in-place counts, 0-100 score with grade and recommendations. Read-only.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""maxElements"":{""type"":""integer"",""description"":""Max elements to iterate. Default 100000.""},""max_warning_types"":{""type"":""integer"",""default"":25,""minimum"":1,""maximum"":100,""description"":""Top warning types returned by count.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var maxElements = req.Value<int?>("maxElements") ?? DefaultCap;
            if (maxElements <= 0) return CommandResult.Fail("maxElements must be positive.");
            var maxTypes = req.Value<int?>("max_warning_types") ?? 25;
            if (maxTypes < 1 || maxTypes > 100)
                return CommandResult.Fail("max_warning_types must be between 1 and 100.");

            var warnings = doc.GetWarnings();
            var errors = 0;
            foreach (var w in warnings)
            {
                try { if (w.GetSeverity() == FailureSeverity.Error) errors++; }
                catch { }
            }
            var top = warnings
                .GroupBy(w => { try { return w.GetDescriptionText(); } catch { return "?"; } })
                .OrderByDescending(g => g.Count())
                .Take(maxTypes)
                .Select(g => new
                {
                    description = Truncate(g.Key, 140),
                    count = g.Count(),
                    severity = FirstSeverity(g)
                })
                .ToList();

            var elements = 0;
            var cadImports = 0;
            var rvtLinks = 0;
            var inPlace = 0;
            var truncated = false;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (elements >= maxElements) { truncated = true; break; }
                elements++;
                if (e is ImportInstance) cadImports++;
                else if (e is RevitLinkInstance) rvtLinks++;
                else if (e is FamilyInstance fi)
                {
                    try { if (fi.Symbol?.Family?.IsInPlace == true) inPlace++; }
                    catch { }
                }
            }

            var score = HealthScorer.Score(errors, warnings.Count - errors,
                cadImports, rvtLinks, inPlace, truncated);

            long? sizeBytes = null;
            try { if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName)) sizeBytes = new FileInfo(doc.PathName).Length; }
            catch { }

            return CommandResult.Ok(new
            {
                project = doc.Title,
                path = doc.PathName,
                size_bytes = sizeBytes,
                workshared = doc.IsWorkshared,
                elements_counted = elements,
                truncated,
                cap = maxElements,
                total_warnings = warnings.Count,
                error_count = errors,
                warning_count = warnings.Count - errors,
                cad_imports = cadImports,
                rvt_links = rvtLinks,
                in_place_families = inPlace,
                top_warnings = top,
                score = score.Score,
                grade = score.Grade,
                deductions = score.Deductions,
                recommendations = score.Recommendations
            });
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max) + "...";
        }

        private static string FirstSeverity(IGrouping<string, FailureMessage> group)
        {
            try { return group.First().GetSeverity().ToString(); }
            catch { return "?"; }
        }
    }
}
