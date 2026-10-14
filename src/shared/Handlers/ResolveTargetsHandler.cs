using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ResolveTargetsHandler : IRevitCommand
    {
        public string Name => "resolve_targets";
        public string Description => "Resolve fuzzy user terms to exact Revit names: levels, categories, families and types. Read-only. Returns matches with ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""term"":{""type"":""string"",""description"":""Substring to match (case-insensitive).""},""kinds"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""level"",""category"",""family"",""type""]},""description"":""Defaults to all kinds.""},""limit"":{""type"":""integer"",""default"":20,""minimum"":1,""maximum"":100}},""required"":[""term""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var term = (req.Value<string>("term") ?? "").Trim();
            if (term.Length == 0) return CommandResult.Fail("term is required.");
            var kindsToken = req["kinds"] as JArray;
            var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (kindsToken != null)
                foreach (var k in kindsToken) kinds.Add(k.Value<string>());
            var limit = req.Value<int?>("limit") ?? 20;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            var matches = new List<object>();
            bool Want(string kind) => kinds.Count == 0 || kinds.Contains(kind);

            if (Want("level"))
            {
                foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                {
                    if (matches.Count >= limit) break;
                    if (level.Name != null && level.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(new { kind = "level", name = level.Name, id = RevitCompat.GetId(level.Id), elevation_mm = level.Elevation * 304.8 });
                }
            }

            if (Want("category") && matches.Count < limit)
            {
                foreach (var cat in doc.Settings.Categories)
                {
                    if (matches.Count >= limit) break;
                    var category = cat as Category;
                    if (category == null || category.Name == null) continue;
                    if (category.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(new { kind = "category", name = category.Name, id = RevitCompat.GetId(category.Id) });
                }
            }

            if ((Want("family") || Want("type")) && matches.Count < limit)
            {
                foreach (FamilySymbol symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
                {
                    if (matches.Count >= limit) break;
                    string familyName = null;
                    string categoryName = null;
                    try { familyName = symbol.Family?.Name; } catch { }
                    try { categoryName = symbol.Category?.Name; } catch { }
                    var typeName = symbol.Name;
                    if (Want("family") && familyName != null && familyName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(new { kind = "family", name = familyName, id = RevitCompat.GetId(symbol.Family.Id), category = categoryName });
                    else if (Want("type") && typeName != null && typeName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(new { kind = "type", name = typeName, family = familyName, id = RevitCompat.GetId(symbol.Id), category = categoryName });
                }
            }

            return CommandResult.Ok(new { term, count = matches.Count, matches });
        }
    }
}
