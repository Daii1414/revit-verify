using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server.Handlers
{
    /// <summary>Builds deterministic door/window opening candidates from bounded CAD
    /// extraction data. Doors come from swing arcs (center + radius + sweep);
    /// windows come from paired parallel boundary lines. Read-only, applies nothing.</summary>
    internal static class CadOpeningCandidateBuilder
    {
        private const int MaxEntities = 10000;
        private const int MaxUnsupported = 200;
        private const double PairAngleToleranceDegrees = 2.0;
        private const double PairOverlapFraction = 0.5;

        internal static JObject Build(JArray results, JObject rules)
        {
            var doorLayers = ReadNames(rules["door_layers"]);
            var windowLayers = ReadNames(rules["window_layers"]);
            if (doorLayers.Count == 0 && windowLayers.Count == 0)
                return Disabled();

            var sweepMin = ReadOptional(rules, "door_sweep_degrees_min", 45, 0, 360);
            var sweepMax = ReadOptional(rules, "door_sweep_degrees_max", 135, 0, 360);
            var doorWidthMin = ReadOptional(rules, "door_width_mm_min", 500, 1, 1000000);
            var doorWidthMax = ReadOptional(rules, "door_width_mm_max", 1500, 1, 1000000);
            var windowWidthMin = ReadOptional(rules, "window_width_mm_min", 400, 1, 1000000);
            var windowWidthMax = ReadOptional(rules, "window_width_mm_max", 3000, 1, 1000000);
            var windowGapMin = ReadOptional(rules, "window_gap_mm_min", 20, 0, 100000);
            var windowGapMax = ReadOptional(rules, "window_gap_mm_max", 500, 0, 100000);
            if (sweepMin > sweepMax)
                throw new ArgumentException("door_sweep_degrees_min cannot exceed door_sweep_degrees_max.");
            if (doorWidthMin > doorWidthMax)
                throw new ArgumentException("door_width_mm_min cannot exceed door_width_mm_max.");
            if (windowWidthMin > windowWidthMax)
                throw new ArgumentException("window_width_mm_min cannot exceed window_width_mm_max.");
            if (windowGapMin > windowGapMax)
                throw new ArgumentException("window_gap_mm_min cannot exceed window_gap_mm_max.");

            var doors = new JArray();
            var windows = new JArray();
            var unsupported = new JArray();
            var unsupportedTruncated = false;
            var doorArcs = 0;
            var windowLines = 0;
            var entityCount = 0;

            foreach (var instance in results.OfType<JObject>())
            {
                var entities = instance["entities"] as JArray ?? new JArray();
                var instanceId = instance.Value<long?>("instance_id");
                var sourcePath = instance.Value<string>("source_path");
                var lines = new List<LineSeg>();
                foreach (var token in entities)
                {
                    var entity = token as JObject;
                    if (entity == null) continue;
                    entityCount++;
                    if (entityCount > MaxEntities)
                        throw new ArgumentException("The CAD extraction contains more than 10000 returned entities; narrow the selected instances or layer scope.");
                    var index = entities.IndexOf(token);
                    var layer = entity.Value<string>("layer") ?? string.Empty;
                    var type = entity.Value<string>("type") ?? string.Empty;
                    var onDoors = doorLayers.Contains(layer);
                    var onWindows = windowLayers.Contains(layer);
                    if (!onDoors && !onWindows) continue;

                    if (onDoors && string.Equals(type, "curve:Arc", StringComparison.OrdinalIgnoreCase))
                    {
                        doorArcs++;
                        var candidate = ReadDoorArc(entity, instanceId, sourcePath, layer, index,
                            sweepMin, sweepMax, doorWidthMin, doorWidthMax, out var reason);
                        if (candidate != null) doors.Add(candidate);
                        else AddUnsupported(unsupported, ref unsupportedTruncated, entity, instanceId, sourcePath, layer, index, reason);
                        continue;
                    }
                    if (onWindows && string.Equals(type, "curve:Line", StringComparison.OrdinalIgnoreCase))
                    {
                        var seg = ReadLine(entity, instanceId, sourcePath, layer, index, out var why);
                        if (seg != null) lines.Add(seg);
                        else AddUnsupported(unsupported, ref unsupportedTruncated, entity, instanceId, sourcePath, layer, index, why);
                        windowLines++;
                        continue;
                    }
                    AddUnsupported(unsupported, ref unsupportedTruncated, entity, instanceId, sourcePath, layer, index,
                        "Only swing arcs feed door candidates and straight lines feed window candidates on these layers.");
                }
                PairWindows(lines, windows, unsupported, ref unsupportedTruncated,
                    windowWidthMin, windowWidthMax, windowGapMin, windowGapMax);
                lines.Clear();
            }

            return new JObject
            {
                ["enabled"] = true,
                ["analysis_complete"] = true,
                ["eligible_door_arc_count"] = doorArcs,
                ["eligible_window_line_count"] = windowLines,
                ["door_count"] = doors.Count,
                ["window_count"] = windows.Count,
                ["unsupported_count"] = unsupported.Count,
                ["unsupported_truncated"] = unsupportedTruncated,
                ["door_candidates"] = doors,
                ["window_candidates"] = windows,
                ["unsupported_entities"] = unsupported,
            };
        }

        private static JObject Disabled()
        {
            return new JObject
            {
                ["enabled"] = false,
                ["analysis_complete"] = true,
                ["eligible_door_arc_count"] = 0,
                ["eligible_window_line_count"] = 0,
                ["door_count"] = 0,
                ["window_count"] = 0,
                ["unsupported_count"] = 0,
                ["unsupported_truncated"] = false,
                ["door_candidates"] = new JArray(),
                ["window_candidates"] = new JArray(),
                ["unsupported_entities"] = new JArray(),
            };
        }

        private static JObject ReadDoorArc(JObject entity, long? instanceId, string sourcePath,
            string layer, int index, double sweepMin, double sweepMax,
            double widthMin, double widthMax, out string reason)
        {
            reason = null;
            var center = entity["center_mm"] as JObject;
            var radius = entity.Value<double?>("radius_mm");
            if (center == null || !radius.HasValue || !Finite(radius.Value) || radius.Value <= 0)
            {
                reason = "Arc is missing center_mm/radius_mm; extraction without arc parameters cannot feed doors.";
                return null;
            }
            var sweep = entity.Value<double?>("sweep_degrees");
            if (!sweep.HasValue || !Finite(sweep.Value))
            {
                reason = "Arc sweep is unknown; full circles and unmeasured arcs are not door swings.";
                return null;
            }
            if (sweep.Value >= 359)
            {
                reason = "Full-circle arc looks like a column or symbol, not a door swing.";
                return null;
            }
            if (sweep.Value < sweepMin || sweep.Value > sweepMax)
            {
                reason = "Arc sweep " + sweep.Value + "deg is outside the door swing range.";
                return null;
            }
            if (radius.Value < widthMin || radius.Value > widthMax)
            {
                reason = "Arc radius " + radius.Value + "mm is outside the door width range.";
                return null;
            }
            double? leafAngle = null;
            var start = entity["start_mm"] as JObject;
            if (start != null)
            {
                var dx = start.Value<double?>("x_mm") ?? 0;
                var dy = start.Value<double?>("y_mm") ?? 0;
                var cx = center.Value<double?>("x_mm") ?? 0;
                var cy = center.Value<double?>("y_mm") ?? 0;
                leafAngle = Math.Round(NormalizeAngle(Math.Atan2(dy - cy, dx - cx) * 180.0 / Math.PI), 3);
            }
            return new JObject
            {
                ["kind"] = "door",
                ["center_mm"] = new JObject
                {
                    ["x_mm"] = center.Value<double?>("x_mm"),
                    ["y_mm"] = center.Value<double?>("y_mm"),
                },
                ["width_mm"] = Math.Round(radius.Value, 3),
                ["sweep_degrees"] = Math.Round(sweep.Value, 3),
                ["leaf_angle_degrees"] = leafAngle.HasValue ? new JValue(leafAngle.Value) : JValue.CreateNull(),
                ["layer"] = layer,
                ["instance_id"] = instanceId,
                ["source_path"] = sourcePath,
                ["entity_index"] = index,
            };
        }

        private sealed class LineSeg
        {
            public double X1, Y1, X2, Y2, Length;
            public string Layer;
            public long? InstanceId;
            public string SourcePath;
            public int Index;
        }

        private static LineSeg ReadLine(JObject entity, long? instanceId, string sourcePath,
            string layer, int index, out string reason)
        {
            reason = null;
            var a = entity["start_mm"] as JObject;
            var b = entity["end_mm"] as JObject;
            if (a == null || b == null)
            {
                reason = "Line is missing start_mm/end_mm.";
                return null;
            }
            var x1 = a.Value<double?>("x_mm") ?? double.NaN;
            var y1 = a.Value<double?>("y_mm") ?? double.NaN;
            var x2 = b.Value<double?>("x_mm") ?? double.NaN;
            var y2 = b.Value<double?>("y_mm") ?? double.NaN;
            if (!Finite(x1) || !Finite(y1) || !Finite(x2) || !Finite(y2))
            {
                reason = "Line endpoints are not finite numbers.";
                return null;
            }
            var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (length <= 0)
            {
                reason = "Line has zero length.";
                return null;
            }
            return new LineSeg { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Length = length, Layer = layer, InstanceId = instanceId, SourcePath = sourcePath, Index = index };
        }

        private static void PairWindows(List<LineSeg> lines, JArray windows, JArray unsupported,
            ref bool truncated, double widthMin, double widthMax, double gapMin, double gapMax)
        {
            var used = new bool[lines.Count];
            var cosTol = Math.Cos(PairAngleToleranceDegrees * Math.PI / 180.0);
            for (var i = 0; i < lines.Count; i++)
            {
                if (used[i]) continue;
                var a = lines[i];
                if (a.Length < widthMin || a.Length > widthMax) continue;
                var ax = (a.X2 - a.X1) / a.Length;
                var ay = (a.Y2 - a.Y1) / a.Length;
                var amx = (a.X1 + a.X2) / 2;
                var amy = (a.Y1 + a.Y2) / 2;
                var best = -1;
                var bestGap = 0.0;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (used[j]) continue;
                    var c = lines[j];
                    if (c.Length < widthMin || c.Length > widthMax) continue;
                    var bx = (c.X2 - c.X1) / c.Length;
                    var by = (c.Y2 - c.Y1) / c.Length;
                    if (Math.Abs(ax * bx + ay * by) < cosTol) continue;
                    var bmx = (c.X1 + c.X2) / 2;
                    var bmy = (c.Y1 + c.Y2) / 2;
                    var gap = Math.Abs((bmx - amx) * ay + -(bmy - amy) * ax);
                    if (gap < gapMin || gap > gapMax) continue;
                    var overlap = Overlap(a.X1, a.Y1, a.X2, a.Y2, c.X1, c.Y1, c.X2, c.Y2, ax, ay);
                    if (overlap < PairOverlapFraction * Math.Min(a.Length, c.Length)) continue;
                    best = j;
                    bestGap = gap;
                    break;
                }
                if (best < 0) continue;
                used[i] = true;
                used[best] = true;
                var c2 = lines[best];
                var width = Math.Round((a.Length + c2.Length) / 2, 3);
                var angle = Math.Round(NormalizeAngle(Math.Atan2(ay, ax) * 180.0 / Math.PI), 3);
                windows.Add(new JObject
                {
                    ["kind"] = "window",
                    ["center_mm"] = new JObject
                    {
                        ["x_mm"] = Math.Round((amx + (c2.X1 + c2.X2) / 2) / 2, 3),
                        ["y_mm"] = Math.Round((amy + (c2.Y1 + c2.Y2) / 2) / 2, 3),
                    },
                    ["width_mm"] = width,
                    ["gap_mm"] = Math.Round(bestGap, 3),
                    ["angle_degrees"] = angle,
                    ["layer"] = a.Layer,
                    ["instance_id"] = a.InstanceId,
                    ["source_path"] = a.SourcePath,
                    ["entity_index_a"] = a.Index,
                    ["entity_index_b"] = c2.Index,
                });
            }
            for (var i = 0; i < lines.Count; i++)
            {
                if (!used[i] && lines[i].Length >= widthMin && lines[i].Length <= widthMax)
                    AddUnsupportedRaw(unsupported, ref truncated, lines[i], "Line found no parallel mate in the window gap range.");
            }
        }

        private static double Overlap(double ax1, double ay1, double ax2, double ay2,
            double bx1, double by1, double bx2, double by2, double dx, double dy)
        {
            var aMin = Math.Min(ax1 * dx + ay1 * dy, ax2 * dx + ay2 * dy);
            var aMax = Math.Max(ax1 * dx + ay1 * dy, ax2 * dx + ay2 * dy);
            var bMin = Math.Min(bx1 * dx + by1 * dy, bx2 * dx + by2 * dy);
            var bMax = Math.Max(bx1 * dx + by1 * dy, bx2 * dx + by2 * dy);
            return Math.Max(0, Math.Min(aMax, bMax) - Math.Max(aMin, bMin));
        }

        private static double NormalizeAngle(double degrees)
        {
            while (degrees < 0) degrees += 180;
            while (degrees >= 180) degrees -= 180;
            return degrees;
        }

        private static void AddUnsupported(JArray unsupported, ref bool truncated, JObject entity,
            long? instanceId, string sourcePath, string layer, int index, string reason)
        {
            if (unsupported.Count >= MaxUnsupported) { truncated = true; return; }
            unsupported.Add(new JObject
            {
                ["layer"] = layer,
                ["type"] = entity.Value<string>("type"),
                ["instance_id"] = instanceId,
                ["source_path"] = sourcePath,
                ["entity_index"] = index,
                ["reason"] = reason,
            });
        }

        private static void AddUnsupportedRaw(JArray unsupported, ref bool truncated, LineSeg seg, string reason)
        {
            if (unsupported.Count >= MaxUnsupported) { truncated = true; return; }
            unsupported.Add(new JObject
            {
                ["layer"] = seg.Layer,
                ["type"] = "curve:Line",
                ["instance_id"] = seg.InstanceId,
                ["source_path"] = seg.SourcePath,
                ["entity_index"] = seg.Index,
                ["reason"] = reason,
            });
        }

        private static HashSet<string> ReadNames(JToken token)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var array = token as JArray;
            if (array == null) return result;
            foreach (var item in array)
            {
                if (item.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>()))
                    throw new ArgumentException("Layer lists must contain non-empty strings.");
                result.Add(item.Value<string>().Trim());
            }
            return result;
        }

        private static double ReadOptional(JObject rules, string name, double fallback, double min, double max)
        {
            var token = rules?[name];
            if (token == null) return fallback;
            double value;
            try { value = token.Value<double>(); }
            catch { throw new ArgumentException(name + " must be a number."); }
            if (!Finite(value) || value < min || value > max)
                throw new ArgumentException(name + " must be between " + min + " and " + max + ".");
            return value;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
