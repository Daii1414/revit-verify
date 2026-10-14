using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server.Handlers
{
    /// <summary>Builds a deterministic, non-applying axis and optional straight-wall plan from bounded CAD extraction data.</summary>
    internal static class CadPlanBuilder
    {
        private const int MaxPayloadCharacters = 5 * 1024 * 1024;
        private const int MaxEntities = 10000;

        internal static JObject Build(string extractionJson, string rulesJson)
        {
            if (string.IsNullOrWhiteSpace(extractionJson) || extractionJson.Length > MaxPayloadCharacters)
                throw new ArgumentException("extraction_json is required and must be at most 5 MiB.");
            if (string.IsNullOrWhiteSpace(rulesJson) || rulesJson.Length > 64 * 1024)
                throw new ArgumentException("rules_json is required and must be at most 64 KiB.");

            JObject extraction;
            JObject rules;
            try { extraction = JObject.Parse(extractionJson); }
            catch (JsonException ex) { throw new ArgumentException("extraction_json must be a JSON object: " + ex.Message); }
            try { rules = JObject.Parse(rulesJson); }
            catch (JsonException ex) { throw new ArgumentException("rules_json must be a JSON object: " + ex.Message); }

            var version = (rules.Value<string>("version") ?? string.Empty).Trim();
            if (version.Length == 0 || version.Length > 64)
                throw new ArgumentException("rules_json.version is required and must be 1-64 characters.");
            var xLayers = ReadNames(rules["x_axis_layers"], "x_axis_layers");
            var yLayers = ReadNames(rules["y_axis_layers"], "y_axis_layers");
            var wallLayers = ReadNames(rules["wall_layers"], "wall_layers");
            if (xLayers.Count == 0 && yLayers.Count == 0 && wallLayers.Count == 0)
                throw new ArgumentException("At least one exact layer name is required in axis or wall layer rules.");

            var coordinateTolerance = rules.Value<double?>("coordinate_tolerance_mm") ?? 5;
            var angleTolerance = rules.Value<double?>("angle_tolerance_degrees") ?? 1;
            var minimumLength = rules.Value<double?>("minimum_axis_length_mm") ?? 1000;
            if (!Finite(coordinateTolerance) || coordinateTolerance < 0 || coordinateTolerance > 1000)
                throw new ArgumentException("coordinate_tolerance_mm must be between 0 and 1000.");
            if (!Finite(angleTolerance) || angleTolerance <= 0 || angleTolerance > 10)
                throw new ArgumentException("angle_tolerance_degrees must be greater than 0 and at most 10.");
            if (!Finite(minimumLength) || minimumLength <= 0 || minimumLength > 1000000)
                throw new ArgumentException("minimum_axis_length_mm must be greater than 0 and at most 1000000.");

            var results = extraction["results"] as JArray;
            if (results == null || results.Count == 0)
                throw new ArgumentException("extraction_json.results must contain at least one CAD instance.");
            var failed = extraction["failed"] as JArray;
            if ((failed != null && failed.Count > 0)
                || (extraction.Value<int?>("requested").HasValue && extraction.Value<int?>("returned").HasValue
                    && extraction.Value<int>("requested") != extraction.Value<int>("returned")))
                throw new ArgumentException("The CAD extraction contains failed instances. Re-run extraction for valid instances before planning.");
            if (results.Any(r => r.Value<bool?>("truncated") == true))
                throw new ArgumentException("The CAD extraction is truncated. Re-run revit_cad_extract with a larger entity_limit before planning.");

            var segments = new List<AxisSegment>();
            var entityCount = 0;
            foreach (var instance in results.OfType<JObject>())
            {
                var entities = instance["entities"] as JArray ?? new JArray();
                entityCount += entities.Count;
                if (entityCount > MaxEntities)
                    throw new ArgumentException("The CAD extraction contains more than 10000 returned entities; narrow the selected instances or layer scope.");
                var instanceId = instance.Value<long?>("instance_id");
                var sourcePath = instance.Value<string>("source_path");
                for (var index = 0; index < entities.Count; index++)
                {
                    var entity = entities[index] as JObject;
                    if (entity == null) continue;
                    var geometryType = entity.Value<string>("type");
                    if (!string.Equals(geometryType, "curve:Line", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(geometryType, "polyline", StringComparison.OrdinalIgnoreCase)) continue;
                    var layer = entity.Value<string>("layer") ?? string.Empty;
                    var axis = xLayers.Contains(layer) ? "x" : yLayers.Contains(layer) ? "y" : null;
                    if (axis == null) continue;
                    if (entity.Value<bool?>("vertices_truncated") == true)
                        throw new ArgumentException("CAD polyline vertices are truncated on a configured axis layer. Re-run extraction with complete vertices before planning.");

                    var points = ReadPoints(entity);
                    for (var i = 1; i < points.Count; i++)
                    {
                        var a = points[i - 1];
                        var b = points[i];
                        var dx = b.X - a.X;
                        var dy = b.Y - a.Y;
                        var length = Math.Sqrt(dx * dx + dy * dy);
                        if (!Finite(length) || length < minimumLength) continue;
                        var angularDrift = length * Math.Tan(angleTolerance * Math.PI / 180.0);
                        if (axis == "x" && Math.Abs(dx) <= angularDrift)
                            segments.Add(new AxisSegment(axis, (a.X + b.X) / 2, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y),
                                instanceId, sourcePath, layer, index, a, b));
                        else if (axis == "y" && Math.Abs(dy) <= angularDrift)
                            segments.Add(new AxisSegment(axis, (a.Y + b.Y) / 2, Math.Min(a.X, b.X), Math.Max(a.X, b.X),
                                instanceId, sourcePath, layer, index, a, b));
                    }
                }
            }

            var xAxes = Cluster(segments.Where(s => s.Axis == "x"), coordinateTolerance, ReadLabels(rules["x_axis_labels"], "x_axis_labels"));
            var yAxes = Cluster(segments.Where(s => s.Axis == "y"), coordinateTolerance, ReadLabels(rules["y_axis_labels"], "y_axis_labels"));
            var wallAnalysis = CadWallCandidateBuilder.Build(results, rules);
            var plan = new JObject
            {
                ["plan_version"] = 1,
                ["rules_version"] = version,
                ["rules_sha256"] = Sha256(rulesJson),
                ["extraction_sha256"] = Sha256(extractionJson),
                ["angle_tolerance_degrees"] = angleTolerance,
                ["read_only"] = true,
                ["applies_changes"] = false,
                ["coordinate_system"] = "Revit internal project coordinates, millimetres; CAD insertion transform already applied by Revit",
                ["x_axes"] = xAxes,
                ["y_axes"] = yAxes,
                ["wall_candidates"] = wallAnalysis["wall_candidates"],
                ["wall_analysis"] = wallAnalysis,
                ["limitations"] = new JArray(
                    "Candidates are inferred only from straight or tessellated CAD segments on the exact configured layers.",
                    "Labels are assigned by coordinate order from versioned rules; source text and bubble association are not inferred.",
                    "Wall candidates require two parallel boundary segments and a configured nominal thickness; openings, joins, height, and materials are not inferred.",
                    "This plan does not create or modify Revit elements.")
            };
            plan["plan_sha256"] = Sha256(plan.ToString(Formatting.None));
            return plan;
        }

        private static HashSet<string> ReadNames(JToken token, string name)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token == null) return result;
            var array = token as JArray;
            if (array == null) throw new ArgumentException(name + " must be an array of exact CAD layer names.");
            foreach (var item in array)
            {
                if (item.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>()))
                    throw new ArgumentException(name + " must contain non-empty strings.");
                result.Add(item.Value<string>().Trim());
            }
            return result;
        }

        private static List<string> ReadLabels(JToken token, string name)
        {
            if (token == null) return null;
            var array = token as JArray;
            if (array == null) throw new ArgumentException(name + " must be an array of labels.");
            var labels = array.Select(x => x.Type == JTokenType.String ? x.Value<string>()?.Trim() : null).ToList();
            if (labels.Any(string.IsNullOrWhiteSpace) || labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count)
                throw new ArgumentException(name + " must contain unique, non-empty string labels.");
            return labels;
        }

        private static List<Point2> ReadPoints(JObject entity)
        {
            var result = new List<Point2>();
            var vertices = entity["vertices_mm"] as JArray;
            if (vertices != null)
            {
                foreach (var vertex in vertices.OfType<JObject>())
                    if (TryPoint(vertex, out var point)) result.Add(point);
                return result;
            }
            if (TryPoint(entity["start_mm"] as JObject, out var start)
                && TryPoint(entity["end_mm"] as JObject, out var end))
            {
                result.Add(start);
                result.Add(end);
            }
            return result;
        }

        private static bool TryPoint(JObject point, out Point2 value)
        {
            value = default;
            if (point == null || !double.TryParse(point["x_mm"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(point["y_mm"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !Finite(x) || !Finite(y)) return false;
            value = new Point2(x, y);
            return true;
        }

        private static JArray Cluster(IEnumerable<AxisSegment> source, double tolerance, List<string> labels)
        {
            var sorted = source.OrderBy(x => x.Coordinate).ThenBy(x => x.Start).ToList();
            var clusters = new List<List<AxisSegment>>();
            foreach (var segment in sorted)
            {
                var last = clusters.LastOrDefault();
                if (last == null || Math.Abs(segment.Coordinate - last.Average(x => x.Coordinate)) > tolerance)
                    clusters.Add(new List<AxisSegment> { segment });
                else last.Add(segment);
            }
            if (labels != null && labels.Count != clusters.Count)
                throw new ArgumentException("Configured labels for " + (source.FirstOrDefault()?.Axis ?? "axis") + " axes have " + labels.Count + " entries, but geometry produced " + clusters.Count + " candidates.");

            var output = new JArray();
            for (var i = 0; i < clusters.Count; i++)
            {
                var group = clusters[i];
                output.Add(new JObject
                {
                    ["index"] = i + 1,
                    ["label"] = labels == null ? JValue.CreateNull() : new JValue(labels[i]),
                    ["coordinate_mm"] = Math.Round(group.Average(x => x.Coordinate), 3),
                    ["span_min_mm"] = Math.Round(group.Min(x => x.Start), 3),
                    ["span_max_mm"] = Math.Round(group.Max(x => x.End), 3),
                    ["segment_count"] = group.Count,
                    ["provenance"] = new JArray(group.Select(x => new JObject
                    {
                        ["instance_id"] = x.InstanceId.HasValue ? new JValue(x.InstanceId.Value) : JValue.CreateNull(),
                        ["source_path"] = x.SourcePath == null ? JValue.CreateNull() : new JValue(x.SourcePath),
                        ["layer"] = x.Layer,
                        ["entity_index"] = x.EntityIndex,
                        ["start_mm"] = new JObject { ["x_mm"] = Math.Round(x.StartPoint.X, 3), ["y_mm"] = Math.Round(x.StartPoint.Y, 3) },
                        ["end_mm"] = new JObject { ["x_mm"] = Math.Round(x.EndPoint.X, 3), ["y_mm"] = Math.Round(x.EndPoint.Y, 3) }
                    }))
                });
            }
            return output;
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private readonly struct Point2
        {
            internal Point2(double x, double y) { X = x; Y = y; }
            internal double X { get; }
            internal double Y { get; }
        }

        private sealed class AxisSegment
        {
            internal AxisSegment(string axis, double coordinate, double start, double end,
                long? instanceId, string sourcePath, string layer, int entityIndex, Point2 startPoint, Point2 endPoint)
            { Axis = axis; Coordinate = coordinate; Start = start; End = end; InstanceId = instanceId; SourcePath = sourcePath; Layer = layer; EntityIndex = entityIndex; StartPoint = startPoint; EndPoint = endPoint; }
            internal string Axis { get; }
            internal double Coordinate { get; }
            internal double Start { get; }
            internal double End { get; }
            internal long? InstanceId { get; }
            internal string SourcePath { get; }
            internal string Layer { get; }
            internal int EntityIndex { get; }
            internal Point2 StartPoint { get; }
            internal Point2 EndPoint { get; }
        }
    }
}
