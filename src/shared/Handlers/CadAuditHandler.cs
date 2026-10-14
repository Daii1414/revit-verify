using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Compares versioned CAD axis and straight-wall candidates with the active model without changing it.</summary>
    public sealed class CadAuditHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;
        private const int MaxPlanCharacters = 1024 * 1024;

        public string Name => "cad_audit";
        public string Description => "Read-only comparison of a hashed revit_cad_plan with Revit grids and straight walls. Never creates or changes model elements.";
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
            string planError;
            double angleTolerance;
            var planned = new List<ExpectedAxis>();
            var plannedWalls = new List<ExpectedWall>();
            try
            {
                if (!CadPlanIntegrity.Validate(plan, out planError)) return CommandResult.Fail(planError);
                angleTolerance = plan.Value<double>("angle_tolerance_degrees");
                if (!ReadAxes(plan["x_axes"] as JArray, "x", planned, out planError)
                    || !ReadAxes(plan["y_axes"] as JArray, "y", planned, out planError)
                    || !ReadWalls(plan["wall_candidates"] as JArray, plannedWalls, out planError))
                    return CommandResult.Fail(planError);
            }
            catch (Exception ex) { return CommandResult.Fail("plan_json contains invalid CAD plan data: " + ex.Message); }
            var wallAnalysis = plan["wall_analysis"] as JObject;
            var wallAnalysisEnabled = wallAnalysis?.Value<bool?>("enabled")
                ?? (plannedWalls.Count > 0
                    || (wallAnalysis?["unpaired_segments"] as JArray)?.Count > 0
                    || (wallAnalysis?["unsupported_entities"] as JArray)?.Count > 0
                    || (wallAnalysis?["ambiguous_candidate_indices"] as JArray)?.Count > 0);
            if (planned.Count == 0 && !wallAnalysisEnabled)
                return CommandResult.Fail("The CAD plan contains no axis or wall candidates to audit.");
            var wallAnalysisComplete = wallAnalysis?.Value<bool?>("analysis_complete") ?? true;

            var grids = planned.Count == 0
                ? new List<Grid>()
                : new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
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

            var wallFindings = new List<object>();
            var wallMatches = new List<object>();
            var actualWalls = new List<ActualWall>();
            var usedWallIds = new HashSet<ElementId>();
            var unsupportedModelWallIds = new List<long>();
            if (wallAnalysisEnabled && !wallAnalysisComplete)
            {
                wallFindings.Add(new
                {
                    code = "WALL_CAD_PLAN_INCOMPLETE",
                    candidate_index = (int?)null,
                    offending_ids = Array.Empty<long>(),
                    unpaired_segment_count = (wallAnalysis["unpaired_segments"] as JArray)?.Count ?? 0,
                    unsupported_entity_count = (wallAnalysis["unsupported_entities"] as JArray)?.Count ?? 0,
                    ambiguous_candidate_count = (wallAnalysis["ambiguous_candidate_indices"] as JArray)?.Count ?? 0,
                    message = "The CAD wall analysis contains unpaired, unsupported, or ambiguous source geometry."
                });
            }
            if (plannedWalls.Count > 0)
            {
                foreach (var wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    try
                    {
                        var location = wall.Location as LocationCurve;
                        if (!(location?.Curve is Line line))
                        {
                            unsupportedModelWallIds.Add(RevitCompat.GetId(wall.Id));
                            continue;
                        }
                        var start = line.GetEndPoint(0);
                        var end = line.GetEndPoint(1);
                        var widthMm = wall.Width * FeetToMm;
                        if (!Finite(widthMm) || widthMm <= 0) { unsupportedModelWallIds.Add(RevitCompat.GetId(wall.Id)); continue; }
                        actualWalls.Add(new ActualWall(wall, start.X * FeetToMm, start.Y * FeetToMm, start.Z * FeetToMm,
                            end.X * FeetToMm, end.Y * FeetToMm, end.Z * FeetToMm, widthMm));
                    }
                    catch { unsupportedModelWallIds.Add(RevitCompat.GetId(wall.Id)); }
                }

                foreach (var expected in plannedWalls)
                {
                    if (string.Equals(expected.Status, "candidate_ambiguous", StringComparison.Ordinal))
                    {
                        wallFindings.Add(WallFinding("WALL_CAD_CANDIDATE_AMBIGUOUS", expected,
                            Array.Empty<long>(), "The CAD candidate shares a boundary with another plausible wall pair."));
                        continue;
                    }
                    MatchWall(expected, actualWalls, usedWallIds, tolerance, wallMatches, wallFindings);
                }
            }
            var unplannedWalls = actualWalls.Where(x => !usedWallIds.Contains(x.Wall.Id))
                .Take(500)
                .Select(x => new { id = RevitCompat.GetId(x.Wall.Id), name = x.Wall.Name, width_mm = Math.Round(x.WidthMm, 3) })
                .ToArray();
            var unplannedWallCount = actualWalls.Count(x => !usedWallIds.Contains(x.Wall.Id));
            if (plannedWalls.Count > 0 && unplannedWallCount > 0)
            {
                wallFindings.Add(new
                {
                    code = "WALL_NOT_IN_CAD_PLAN",
                    candidate_index = (int?)null,
                    offending_ids = actualWalls.Where(x => !usedWallIds.Contains(x.Wall.Id)).Take(500)
                        .Select(x => RevitCompat.GetId(x.Wall.Id)).ToArray(),
                    count = unplannedWallCount,
                    truncated = unplannedWallCount > 500,
                    message = "Straight Revit walls remain unmatched by the configured CAD wall candidates."
                });
            }
            if (plannedWalls.Count > 0 && unsupportedModelWallIds.Count > 0)
            {
                wallFindings.Add(new
                {
                    code = "WALL_MODEL_GEOMETRY_UNSUPPORTED",
                    candidate_index = (int?)null,
                    offending_ids = unsupportedModelWallIds.Take(500).ToArray(),
                    count = unsupportedModelWallIds.Count,
                    truncated = unsupportedModelWallIds.Count > 500,
                    message = "Some Revit walls do not expose a supported straight location line and could not be audited."
                });
            }
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
                expected_wall_count = plannedWalls.Count,
                matched_wall_count = wallMatches.Count,
                wall_analysis_enabled = wallAnalysisEnabled,
                wall_analysis_complete = wallAnalysisComplete,
                pass = findings.Count == 0 && wallFindings.Count == 0,
                matches,
                findings,
                unplanned_model_grids = unplanned,
                wall_matches = wallMatches,
                wall_findings = wallFindings,
                unplanned_model_walls = unplannedWalls,
                unplanned_model_walls_truncated = unplannedWallCount > unplannedWalls.Length,
                unsupported_model_wall_count = unsupportedModelWallIds.Count,
                unsupported_model_wall_ids = unsupportedModelWallIds.Take(500).ToArray(),
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

        private static bool ReadWalls(JArray walls, List<ExpectedWall> output, out string error)
        {
            error = null;
            if (walls == null) return true;
            var indices = new HashSet<int>();
            foreach (var token in walls)
            {
                if (!(token is JObject wall)) { error = "wall_candidates must contain objects."; return false; }
                var index = wall.Value<int?>("candidate_index");
                var status = wall.Value<string>("status");
                var thickness = wall.Value<double?>("thickness_mm");
                if (!index.HasValue || index.Value < 1 || !indices.Add(index.Value)
                    || (status != "candidate_unverified" && status != "candidate_ambiguous")
                    || !thickness.HasValue || !Finite(thickness.Value) || thickness.Value <= 0
                    || !TryPoint(wall["start_mm"] as JObject, out var start)
                    || !TryPoint(wall["end_mm"] as JObject, out var end))
                { error = "wall_candidates contains an invalid index, status, thickness, or endpoint."; return false; }
                output.Add(new ExpectedWall(index.Value, status, thickness.Value, start, end));
            }
            return true;
        }

        private static bool TryPoint(JObject point, out PointMm value)
        {
            value = default;
            var x = point?.Value<double?>("x_mm");
            var y = point?.Value<double?>("y_mm");
            var z = point?.Value<double?>("z_mm");
            if (!x.HasValue || !y.HasValue || !z.HasValue
                || !Finite(x.Value) || !Finite(y.Value) || !Finite(z.Value)) return false;
            value = new PointMm(x.Value, y.Value, z.Value);
            return true;
        }

        private static void MatchWall(ExpectedWall expected, List<ActualWall> actual, HashSet<ElementId> used,
            double tolerance, List<object> matches, List<object> findings)
        {
            var compatible = new List<Tuple<ActualWall, double, double, bool>>();
            foreach (var wall in actual)
            {
                if (!CadWallAuditPolicy.TryMatch(expected.Start.X, expected.Start.Y, expected.Start.Z,
                    expected.End.X, expected.End.Y, expected.End.Z,
                    wall.StartX, wall.StartY, wall.StartZ, wall.EndX, wall.EndY, wall.EndZ,
                    tolerance, out var endpointDelta, out var reversed)) continue;
                if (!CadWallAuditPolicy.WithinThickness(expected.ThicknessMm, wall.WidthMm, tolerance, out var thicknessDelta)) continue;
                compatible.Add(Tuple.Create(wall, endpointDelta, thicknessDelta, reversed));
            }

            var ordered = compatible.OrderBy(x => x.Item2 + x.Item3).ToList();
            if (ordered.Count == 0)
            {
                findings.Add(WallFinding("WALL_NOT_FOUND", expected, Array.Empty<long>(),
                    "No straight Revit wall matches the candidate endpoints and thickness within tolerance."));
                return;
            }
            if (ordered.Count > 1 && Math.Abs((ordered[1].Item2 + ordered[1].Item3) - (ordered[0].Item2 + ordered[0].Item3)) <= 0.001)
            {
                findings.Add(WallFinding("WALL_MATCH_AMBIGUOUS", expected,
                    ordered.Where(x => Math.Abs((x.Item2 + x.Item3) - (ordered[0].Item2 + ordered[0].Item3)) <= 0.001)
                        .Select(x => RevitCompat.GetId(x.Item1.Wall.Id)).ToArray(),
                    "Multiple Revit walls are equally close to the planned wall candidate."));
                return;
            }

            var candidate = ordered[0];
            var id = RevitCompat.GetId(candidate.Item1.Wall.Id);
            if (used.Contains(candidate.Item1.Wall.Id))
            {
                findings.Add(WallFinding("WALL_MATCH_REUSED", expected, new[] { id },
                    "The same Revit wall matched more than one CAD candidate."));
                return;
            }
            used.Add(candidate.Item1.Wall.Id);
            matches.Add(new
            {
                candidate_index = expected.Index,
                id,
                model_wall_name = candidate.Item1.Wall.Name,
                expected_thickness_mm = Math.Round(expected.ThicknessMm, 3),
                actual_thickness_mm = Math.Round(candidate.Item1.WidthMm, 3),
                thickness_delta_mm = Math.Round(candidate.Item3, 3),
                endpoint_delta_mm = Math.Round(candidate.Item2, 3),
                endpoints_reversed = candidate.Item4
            });
        }

        private static object WallFinding(string code, ExpectedWall expected, long[] ids, string message) => new
        {
            code,
            candidate_index = expected.Index,
            expected_thickness_mm = Math.Round(expected.ThicknessMm, 3),
            offending_ids = ids,
            message
        };

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

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private readonly struct PointMm
        {
            internal PointMm(double x, double y, double z) { X = x; Y = y; Z = z; }
            internal double X { get; }
            internal double Y { get; }
            internal double Z { get; }
        }

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

        private sealed class ExpectedWall
        {
            internal ExpectedWall(int index, string status, double thicknessMm, PointMm start, PointMm end)
            { Index = index; Status = status; ThicknessMm = thicknessMm; Start = start; End = end; }
            internal int Index { get; }
            internal string Status { get; }
            internal double ThicknessMm { get; }
            internal PointMm Start { get; }
            internal PointMm End { get; }
        }

        private sealed class ActualWall
        {
            internal ActualWall(Wall wall, double startX, double startY, double startZ,
                double endX, double endY, double endZ, double widthMm)
            { Wall = wall; StartX = startX; StartY = startY; StartZ = startZ; EndX = endX; EndY = endY; EndZ = endZ; WidthMm = widthMm; }
            internal Wall Wall { get; }
            internal double StartX { get; }
            internal double StartY { get; }
            internal double StartZ { get; }
            internal double EndX { get; }
            internal double EndY { get; }
            internal double EndZ { get; }
            internal double WidthMm { get; }
        }
    }
}
