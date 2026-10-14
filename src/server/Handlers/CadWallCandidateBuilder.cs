using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server.Handlers
{
    /// <summary>Infers read-only straight wall candidates from paired CAD boundary segments.</summary>
    internal static class CadWallCandidateBuilder
    {
        private const int MaxWallSegments = 2000;
        private const int MaxCandidates = 2000;

        internal static JObject Build(JArray results, JObject rules)
        {
            var layers = ReadNames(rules["wall_layers"]);
            if (layers.Count == 0) return new JObject
            {
                ["analysis_complete"] = true,
                ["wall_candidates"] = new JArray(),
                ["unpaired_segments"] = new JArray(),
                ["unsupported_entities"] = new JArray()
            };

            var thickness = ReadRequired(rules, "wall_thickness_mm", 50, 2000);
            var thicknessTolerance = ReadOptional(rules, "wall_thickness_tolerance_mm", 5, 0, 500);
            var angleTolerance = ReadOptional(rules, "wall_angle_tolerance_degrees", 1, 0.01, 10);
            var elevationTolerance = ReadOptional(rules, "wall_elevation_tolerance_mm", 5, 0, 1000);
            var minimumLength = ReadOptional(rules, "minimum_wall_length_mm", 300, 1, 1000000);
            var unsupported = new JArray();
            var segments = ReadSegments(results, layers, unsupported);
            var candidates = new JArray();
            var pairedSegments = new HashSet<WallSegment>();
            var candidatePairs = new List<Tuple<WallSegment, WallSegment>>();

            var cosineTolerance = Math.Cos(angleTolerance * Math.PI / 180.0);
            for (var i = 0; i < segments.Count; i++)
            {
                var a = segments[i];
                var ax = a.End.X - a.Start.X;
                var ay = a.End.Y - a.Start.Y;
                var aLength = Math.Sqrt(ax * ax + ay * ay);
                if (!Finite(aLength)) throw new ArgumentException("Wall segment coordinates exceed the supported numeric range.");
                if (aLength <= 1e-6) continue;
                var ux = ax / aLength;
                var uy = ay / aLength;

                for (var j = i + 1; j < segments.Count; j++)
                {
                    var b = segments[j];
                    if (a.InstanceId != b.InstanceId) continue;
                    if (!Finite(b.Length)) throw new ArgumentException("Wall segment coordinates exceed the supported numeric range.");
                    if (Math.Abs(a.Start.Z - a.End.Z) > elevationTolerance
                        || Math.Abs(b.Start.Z - b.End.Z) > elevationTolerance) continue;
                    if (Math.Abs(ux * ((b.End.X - b.Start.X) / b.Length) + uy * ((b.End.Y - b.Start.Y) / b.Length)) < cosineTolerance)
                        continue;
                    if (Math.Abs(a.Elevation - b.Elevation) > elevationTolerance) continue;

                    var signedStart = ux * (b.Start.Y - a.Start.Y) - uy * (b.Start.X - a.Start.X);
                    var signedEnd = ux * (b.End.Y - a.Start.Y) - uy * (b.End.X - a.Start.X);
                    if (signedStart * signedEnd <= 0) continue;
                    if (Math.Abs(Math.Abs(signedStart) - Math.Abs(signedEnd)) > thicknessTolerance) continue;
                    var measuredThickness = (Math.Abs(signedStart) + Math.Abs(signedEnd)) / 2;
                    if (Math.Abs(measuredThickness - thickness) > thicknessTolerance) continue;

                    var projectionStart = ux * (b.Start.X - a.Start.X) + uy * (b.Start.Y - a.Start.Y);
                    var projectionEnd = ux * (b.End.X - a.Start.X) + uy * (b.End.Y - a.Start.Y);
                    var overlapStart = Math.Max(0, Math.Min(projectionStart, projectionEnd));
                    var overlapEnd = Math.Min(aLength, Math.Max(projectionStart, projectionEnd));
                    var overlapLength = overlapEnd - overlapStart;
                    if (overlapLength < minimumLength) continue;

                    var midOffset = (signedStart + signedEnd) / 4;
                    var start = new Point3(a.Start.X + ux * overlapStart - uy * midOffset,
                        a.Start.Y + uy * overlapStart + ux * midOffset, (a.Elevation + b.Elevation) / 2);
                    var end = new Point3(a.Start.X + ux * overlapEnd - uy * midOffset,
                        a.Start.Y + uy * overlapEnd + ux * midOffset, (a.Elevation + b.Elevation) / 2);
                    candidates.Add(new JObject
                    {
                        ["candidate_index"] = candidates.Count + 1,
                        ["status"] = "candidate_unverified",
                        ["start_mm"] = start.ToJson(),
                        ["end_mm"] = end.ToJson(),
                        ["length_mm"] = Math.Round(overlapLength, 3),
                        ["thickness_mm"] = Math.Round(measuredThickness, 3),
                        ["provenance"] = new JArray(a.ToJson(), b.ToJson()),
                        ["limitations"] = new JArray(
                            "Inferred from two straight CAD boundary segments; joins, openings, wall height, and material are not inferred.",
                            "Candidate has not been matched to a Revit wall and must not be applied automatically.")
                    });
                    pairedSegments.Add(a);
                    pairedSegments.Add(b);
                    candidatePairs.Add(Tuple.Create(a, b));
                    if (candidates.Count > MaxCandidates)
                        throw new ArgumentException("Wall rules produced more than 2000 candidates; narrow the CAD layer scope or increase the minimum wall length.");
                }
            }
            var unpaired = new JArray(segments.Where(x => !pairedSegments.Contains(x)).Select(x => x.ToJson()));
            var pairCounts = new Dictionary<WallSegment, int>();
            foreach (var pair in candidatePairs)
            {
                pairCounts.TryGetValue(pair.Item1, out var firstCount);
                pairCounts[pair.Item1] = firstCount + 1;
                pairCounts.TryGetValue(pair.Item2, out var secondCount);
                pairCounts[pair.Item2] = secondCount + 1;
            }
            var ambiguous = new JArray();
            for (var i = 0; i < candidatePairs.Count; i++)
            {
                var pair = candidatePairs[i];
                if (pairCounts[pair.Item1] <= 1 && pairCounts[pair.Item2] <= 1) continue;
                candidates[i]["status"] = "candidate_ambiguous";
                candidates[i]["ambiguity"] = "At least one boundary segment participates in multiple plausible wall pairs.";
                ambiguous.Add(candidates[i].Value<int>("candidate_index"));
            }
            return new JObject
            {
                ["analysis_complete"] = unsupported.Count == 0 && unpaired.Count == 0 && ambiguous.Count == 0,
                ["wall_candidates"] = candidates,
                ["unpaired_segments"] = unpaired,
                ["unsupported_entities"] = unsupported,
                ["ambiguous_candidate_indices"] = ambiguous,
                ["limitations"] = new JArray(
                    "Completeness covers only straight lines and polylines on the exact configured layers.",
                    "A complete pairing analysis is not proof of correct architectural interpretation.",
                    "Openings, joins, wall height, and materials are not inferred.")
            };
        }

        private static List<WallSegment> ReadSegments(JArray results, HashSet<string> layers, JArray unsupported)
        {
            var segments = new List<WallSegment>();
            foreach (var instance in results.OfType<JObject>())
            {
                var instanceId = instance.Value<long?>("instance_id");
                var sourcePath = instance.Value<string>("source_path");
                var entities = instance["entities"] as JArray ?? new JArray();
                for (var entityIndex = 0; entityIndex < entities.Count; entityIndex++)
                {
                    var entity = entities[entityIndex] as JObject;
                    if (entity == null || !layers.Contains(entity.Value<string>("layer") ?? string.Empty)) continue;
                    var type = entity.Value<string>("type");
                    if (!string.Equals(type, "curve:Line", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(type, "polyline", StringComparison.OrdinalIgnoreCase))
                    {
                        unsupported.Add(new JObject
                        {
                            ["instance_id"] = instanceId.HasValue ? new JValue(instanceId.Value) : JValue.CreateNull(),
                            ["source_path"] = sourcePath == null ? JValue.CreateNull() : new JValue(sourcePath),
                            ["layer"] = entity.Value<string>("layer"),
                            ["entity_index"] = entityIndex,
                            ["type"] = type
                        });
                        continue;
                    }
                    if (entity.Value<bool?>("vertices_truncated") == true)
                        throw new ArgumentException("A polyline is truncated on a configured wall layer. Re-run extraction with complete vertices.");
                    if (string.Equals(type, "polyline", StringComparison.OrdinalIgnoreCase)
                        && !(entity["vertices_mm"] is JArray))
                        throw new ArgumentException("Polylines on configured wall layers require revit_cad_extract with include_vertices=true.");

                    var points = ReadPoints(entity);
                    if (points.Count < 2 || (string.Equals(type, "curve:Line", StringComparison.OrdinalIgnoreCase) && points.Count != 2))
                        throw new ArgumentException("A straight CAD entity on a configured wall layer has missing or invalid endpoint geometry.");
                    for (var pointIndex = 1; pointIndex < points.Count; pointIndex++)
                    {
                        var start = points[pointIndex - 1];
                        var end = points[pointIndex];
                        var length = Distance2D(start, end);
                        if (length <= 1e-6) continue;
                        if (segments.Count >= MaxWallSegments)
                            throw new ArgumentException("Configured wall layers contain more than 2000 straight segments; narrow the CAD scope.");
                        segments.Add(new WallSegment(instanceId, sourcePath, entity.Value<string>("layer"),
                            entityIndex, pointIndex - 1, start, end, length));
                    }
                }
            }
            return segments;
        }

        private static List<Point3> ReadPoints(JObject entity)
        {
            var result = new List<Point3>();
            var vertices = entity["vertices_mm"] as JArray;
            if (vertices != null)
            {
                foreach (var token in vertices)
                {
                    if (!(token is JObject vertex) || !TryPoint(vertex, out var point))
                        throw new ArgumentException("A CAD polyline contains a malformed vertex on a configured wall layer.");
                    result.Add(point);
                }
                return result;
            }
            if (TryPoint(entity["start_mm"] as JObject, out var start)
                && TryPoint(entity["end_mm"] as JObject, out var end))
            { result.Add(start); result.Add(end); }
            return result;
        }

        private static bool TryPoint(JObject point, out Point3 value)
        {
            value = default;
            if (point == null || !TryNumber(point["x_mm"], out var x) || !TryNumber(point["y_mm"], out var y)
                || !TryNumber(point["z_mm"], out var z)
                || Math.Abs(x) > 1000000000 || Math.Abs(y) > 1000000000 || Math.Abs(z) > 1000000000) return false;
            value = new Point3(x, y, z);
            return true;
        }

        private static HashSet<string> ReadNames(JToken token)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token == null) return result;
            if (!(token is JArray array)) throw new ArgumentException("wall_layers must be an array of exact CAD layer names.");
            foreach (var item in array)
            {
                if (item.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>()))
                    throw new ArgumentException("wall_layers must contain non-empty strings.");
                result.Add(item.Value<string>().Trim());
            }
            return result;
        }

        private static double ReadRequired(JObject rules, string name, double minimum, double maximum)
        {
            var value = rules.Value<double?>(name);
            if (!value.HasValue || !Finite(value.Value) || value.Value < minimum || value.Value > maximum)
                throw new ArgumentException(name + " is required and must be between " + minimum + " and " + maximum + " mm.");
            return value.Value;
        }

        private static double ReadOptional(JObject rules, string name, double defaultValue, double minimum, double maximum)
        {
            var value = rules.Value<double?>(name) ?? defaultValue;
            if (!Finite(value) || value < minimum || value > maximum)
                throw new ArgumentException(name + " must be between " + minimum + " and " + maximum + ".");
            return value;
        }

        private static bool TryNumber(JToken token, out double value) =>
            double.TryParse(token?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Finite(value);

        private static double Distance2D(Point3 a, Point3 b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private readonly struct Point3
        {
            internal Point3(double x, double y, double z) { X = x; Y = y; Z = z; }
            internal double X { get; }
            internal double Y { get; }
            internal double Z { get; }
            internal JObject ToJson() => new JObject { ["x_mm"] = Math.Round(X, 3), ["y_mm"] = Math.Round(Y, 3), ["z_mm"] = Math.Round(Z, 3) };
        }

        private sealed class WallSegment
        {
            internal WallSegment(long? instanceId, string sourcePath, string layer, int entityIndex, int segmentIndex,
                Point3 start, Point3 end, double length)
            { InstanceId = instanceId; SourcePath = sourcePath; Layer = layer; EntityIndex = entityIndex; SegmentIndex = segmentIndex; Start = start; End = end; Length = length; Elevation = (start.Z + end.Z) / 2; }
            internal long? InstanceId { get; }
            internal string SourcePath { get; }
            internal string Layer { get; }
            internal int EntityIndex { get; }
            internal int SegmentIndex { get; }
            internal Point3 Start { get; }
            internal Point3 End { get; }
            internal double Length { get; }
            internal double Elevation { get; }
            internal JObject ToJson() => new JObject
            {
                ["instance_id"] = InstanceId.HasValue ? new JValue(InstanceId.Value) : JValue.CreateNull(),
                ["source_path"] = SourcePath == null ? JValue.CreateNull() : new JValue(SourcePath),
                ["layer"] = Layer,
                ["entity_index"] = EntityIndex,
                ["segment_index"] = SegmentIndex,
                ["start_mm"] = Start.ToJson(),
                ["end_mm"] = End.ToJson()
            };
        }
    }
}
