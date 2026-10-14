using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Compares versioned CAD axis candidates with existing Revit grids without changing the document.</summary>
    public sealed class CadAuditHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;
        private const int MaxPlanCharacters = 1024 * 1024;

        public string Name => "cad_audit";
        public string Description => "Read-only comparison of a hashed revit_cad_plan with existing straight Revit grids. Never creates or changes model elements.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""plan_json""],
  ""properties"": {
    ""plan_json"": { ""type"": ""string"", ""description"": ""Complete output from revit_cad_plan, including plan_sha256."" },
    ""tolerance_mm"": { ""type"": ""number"", ""default"": 10, ""minimum"": 0, ""maximum"": 1000 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var planText = request.Value<string>("plan_json");
            if (string.IsNullOrWhiteSpace(planText) || planText.Length > MaxPlanCharacters)
                return CommandResult.Fail("plan_json is required and must be at most 1 MiB.");
            var tolerance = request.Value<double?>("tolerance_mm") ?? 10;
            if (double.IsNaN(tolerance) || double.IsInfinity(tolerance) || tolerance < 0 || tolerance > 1000)
                return CommandResult.Fail("tolerance_mm must be between 0 and 1000.");

            JObject plan;
            try { plan = JObject.Parse(planText); }
            catch (Exception ex) { return CommandResult.Fail("plan_json must be a JSON object: " + ex.Message); }
            if (!CadPlanIntegrity.Validate(plan, out var planError)) return CommandResult.Fail(planError);

            var angleTolerance = plan.Value<double>("angle_tolerance_degrees");
            var planned = new List<ExpectedAxis>();
            if (!ReadAxes(plan["x_axes"] as JArray, "x", planned, out planError)
                || !ReadAxes(plan["y_axes"] as JArray, "y", planned, out planError))
                return CommandResult.Fail(planError);
            if (planned.Count == 0) return CommandResult.Fail("The CAD plan contains no axis candidates to audit.");

            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            var actual = new List<ActualAxis>();
            foreach (var grid in grids)
            {
                try
                {
                    if (!(grid.Curve is Line line)) continue;
                    var a = line.GetEndPoint(0);
                    var b = line.GetEndPoint(1);
                    var orientation = CadAxisAuditPolicy.ClassifyLine(a.X, a.Y, b.X, b.Y,
                        angleTolerance, out var coordinateFeet);
                    if (orientation == null) continue;
                    actual.Add(new ActualAxis(grid, orientation, coordinateFeet * FeetToMm));
                }
                catch { /* A grid with unreadable geometry cannot be matched; no model state is changed. */ }
            }

            var used = new HashSet<ElementId>();
            var matches = new List<object>();
            var findings = new List<object>();
            foreach (var expected in planned)
                MatchAxis(expected, actual, used, tolerance, matches, findings);

            var unplanned = actual.Where(x => !used.Contains(x.Grid.Id))
                .OrderBy(x => x.Orientation, StringComparer.Ordinal)
                .ThenBy(x => x.CoordinateMm)
                .Select(x => new { id = RevitCompat.GetId(x.Grid.Id), name = x.Grid.Name, axis = x.Orientation, coordinate_mm = Math.Round(x.CoordinateMm, 3) })
                .ToArray();
            string projectInfoId = null;
            try { projectInfoId = doc.ProjectInformation?.UniqueId; } catch { }

            return CommandResult.Ok(new
            {
                plan_version = plan.Value<int>("plan_version"),
                plan_sha256 = plan.Value<string>("plan_sha256"),
                rules_version = plan.Value<string>("rules_version"),
                rules_sha256 = plan.Value<string>("rules_sha256"),
                extraction_sha256 = plan.Value<string>("extraction_sha256"),
                model_title = doc.Title,
                project_information_unique_id = projectInfoId,
                tolerance_mm = tolerance,
                expected_count = planned.Count,
                matched_count = matches.Count,
                pass = findings.Count == 0,
                matches,
                findings,
                unplanned_model_grids = unplanned,
                read_only = true,
                applies_changes = false
            });
        }

        private static bool ReadAxes(JArray axes, string orientation, List<ExpectedAxis> output, out string error)
        {
            error = null;
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in axes)
            {
                if (!(token is JObject axis)) { error = orientation + "_axes must contain objects."; return false; }
                var coordinate = axis.Value<double?>("coordinate_mm");
                var label = axis.Value<string>("label");
                if (!coordinate.HasValue || double.IsNaN(coordinate.Value) || double.IsInfinity(coordinate.Value)
                    || (label != null && (string.IsNullOrWhiteSpace(label) || !labels.Add(label))))
                { error = orientation + "_axes contains an invalid coordinate or duplicate label."; return false; }
                output.Add(new ExpectedAxis(orientation, label, coordinate.Value));
            }
            return true;
        }

        private static void MatchAxis(ExpectedAxis expected, List<ActualAxis> actual, HashSet<ElementId> used,
            double tolerance, List<object> matches, List<object> findings)
        {
            var sameName = expected.Label == null
                ? new List<ActualAxis>()
                : actual.Where(x => string.Equals(x.Grid.Name, expected.Label, StringComparison.OrdinalIgnoreCase)).ToList();
            ActualAxis candidate = null;
            if (sameName.Count > 1)
            {
                findings.Add(Finding("GRID_MATCH_AMBIGUOUS", expected, sameName.Select(x => RevitCompat.GetId(x.Grid.Id)).ToArray(), "More than one Revit grid has the requested label."));
                return;
            }
            if (sameName.Count == 1) candidate = sameName[0];
            else if (expected.Label == null)
            {
                var compatible = actual.Where(x => x.Orientation == expected.Orientation && !used.Contains(x.Grid.Id))
                    .Select(x => new { Axis = x, Delta = Math.Abs(x.CoordinateMm - expected.CoordinateMm) })
                    .OrderBy(x => x.Delta).ToList();
                if (compatible.Count > 0)
                {
                    if (compatible.Count > 1 && Math.Abs(compatible[1].Delta - compatible[0].Delta) <= 0.001)
                    {
                        findings.Add(Finding("GRID_MATCH_AMBIGUOUS", expected,
                            compatible.Where(x => Math.Abs(x.Delta - compatible[0].Delta) <= 0.001).Select(x => RevitCompat.GetId(x.Axis.Grid.Id)).ToArray(),
                            "Multiple unlabeled Revit grids are equally close to the planned coordinate."));
                        return;
                    }
                    candidate = compatible[0].Axis;
                }
            }

            if (candidate == null)
            {
                findings.Add(Finding("GRID_NOT_FOUND", expected, Array.Empty<long>(), "No Revit grid matches the planned axis label."));
                return;
            }
            var id = RevitCompat.GetId(candidate.Grid.Id);
            if (used.Contains(candidate.Grid.Id))
            {
                findings.Add(Finding("GRID_MATCH_REUSED", expected, new[] { id }, "The same Revit grid matched more than one planned axis."));
                return;
            }
            if (candidate.Orientation != expected.Orientation)
            {
                findings.Add(Finding("GRID_ORIENTATION_MISMATCH", expected, new[] { id }, "The labeled Revit grid has the wrong axis orientation."));
                return;
            }
            var within = CadAxisAuditPolicy.WithinTolerance(expected.CoordinateMm, candidate.CoordinateMm, tolerance, out var delta);
            if (!within)
            {
                findings.Add(Finding("GRID_COORDINATE_MISMATCH", expected, new[] { id }, "The Revit grid is outside the allowed coordinate tolerance.", candidate.CoordinateMm, delta));
                return;
            }
            used.Add(candidate.Grid.Id);
            matches.Add(new { axis = expected.Orientation, label = expected.Label, expected_coordinate_mm = expected.CoordinateMm,
                actual_coordinate_mm = Math.Round(candidate.CoordinateMm, 3), delta_mm = Math.Round(delta, 3), id, model_grid_name = candidate.Grid.Name });
        }

        private static object Finding(string code, ExpectedAxis expected, long[] ids, string message,
            double? actualCoordinate = null, double? delta = null) => new
        {
            code, axis = expected.Orientation, label = expected.Label, expected_coordinate_mm = expected.CoordinateMm,
            actual_coordinate_mm = actualCoordinate.HasValue ? (double?)Math.Round(actualCoordinate.Value, 3) : null,
            delta_mm = delta.HasValue ? (double?)Math.Round(delta.Value, 3) : null,
            offending_ids = ids, message
        };

        private sealed class ExpectedAxis
        {
            internal ExpectedAxis(string orientation, string label, double coordinateMm)
            { Orientation = orientation; Label = label; CoordinateMm = coordinateMm; }
            internal string Orientation { get; }
            internal string Label { get; }
            internal double CoordinateMm { get; }
        }

        private sealed class ActualAxis
        {
            internal ActualAxis(Grid grid, string orientation, double coordinateMm)
            { Grid = grid; Orientation = orientation; CoordinateMm = coordinateMm; }
            internal Grid Grid { get; }
            internal string Orientation { get; }
            internal double CoordinateMm { get; }
        }
    }
}
