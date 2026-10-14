using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class VerifyAssertHandler : IRevitCommand
    {
        private const int MaxAssertions = 20;
        private const int MaxSolidVolumeElements = 200;
        private const double CubicFeetToCubicMeters = 0.028316846592;

        public string Name => "verify_assert";
        public string Description => "Check measurable post-conditions against the model: bounding boxes, exact or ranged element counts, and per-element solid volumes. Read-only. Returns pass/fail and offending or missing element IDs.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""assertions"":{""type"":""array"",""maxItems"":20,""items"":{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""bbox"",""count"",""solid_volume""]},""ids"":{""type"":""array"",""items"":{""type"":""integer""}},""category"":{""type"":""string""},""level"":{""type"":""string""},""box_mm"":{""type"":""object""},""tol_mm"":{""type"":""number"",""default"":10},""expect"":{""type"":""integer""},""min_expect"":{""type"":""integer""},""max_expect"":{""type"":""integer""},""min_volume_m3"":{""type"":""number""},""max_volume_m3"":{""type"":""number""}}},""description"":""bbox: union box of ids (or category+level filter) must match box_mm within tol_mm per axis. count: exact expect or inclusive min_expect/max_expect. solid_volume: every scoped element's positive Revit solid volume must fall within min_volume_m3/max_volume_m3; use ids or category/level, max 200 elements. This does not prove structural support or watertightness of source geometry.""}},""required"":[""assertions""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var arr = req["assertions"] as JArray;
            if (arr == null || !arr.Any())
                return CommandResult.Fail("assertions non-empty array is required.");
            if (arr.Count > MaxAssertions)
                return CommandResult.Fail("At most " + MaxAssertions + " assertions are allowed per request.");

            var results = new List<object>();
            var allPass = true;
            foreach (var a in arr)
            {
                var kind = (a.Value<string>("kind") ?? "").ToLowerInvariant();
                try
                {
                    if (kind == "bbox") results.Add(CheckBox(doc, (JObject)a));
                    else if (kind == "count") results.Add(CheckCount(doc, (JObject)a));
                    else if (kind == "solid_volume") results.Add(CheckSolidVolume(doc, (JObject)a));
                    else results.Add(new { kind, pass = false, error = "unknown kind (bbox|count|solid_volume)" });
                }
                catch (Exception ex)
                {
                    results.Add(new { kind, pass = false, error = ex.Message });
                }
                allPass = allPass && (bool)((dynamic)results[results.Count - 1]).pass;
            }
            return CommandResult.Ok(new { pass = allPass, assertions = results });
        }

        private static object CheckBox(Document doc, JObject a)
        {
            var tol = a.Value<double?>("tol_mm") ?? 10;
            var boxObj = a["box_mm"] as JObject;
            if (boxObj == null) return new { kind = "bbox", pass = false, error = "box_mm is required." };

            var idsToken = a["ids"] as JArray;
            List<Element> elements;
            if (idsToken != null && idsToken.Count > 0)
            {
                elements = new List<Element>();
                var missing = new List<long>();
                foreach (var t in idsToken)
                {
                    var e = doc.GetElement(RevitCompat.ToElementId(t.Value<long>()));
                    if (e == null) missing.Add(t.Value<long>());
                    else elements.Add(e);
                }
                if (missing.Count > 0)
                    return new { kind = "bbox", pass = false, error = "elements not found", missing };
            }
            else
            {
                var category = a.Value<string>("category");
                var level = a.Value<string>("level");
                elements = FilterElements(doc, category, level);
            }

            XYZ min = null, max = null;
            foreach (var e in elements)
            {
                var box = e.get_BoundingBox(null);
                if (box == null) continue;
                min = min == null ? box.Min : new XYZ(Math.Min(min.X, box.Min.X), Math.Min(min.Y, box.Min.Y), Math.Min(min.Z, box.Min.Z));
                max = max == null ? box.Max : new XYZ(Math.Max(max.X, box.Max.X), Math.Max(max.Y, box.Max.Y), Math.Max(max.Z, box.Max.Z));
            }
            if (min == null) return new { kind = "bbox", pass = false, error = "no measurable geometry in scope." };

            var exp = ReadBox(boxObj);
            if (exp == null) return new { kind = "bbox", pass = false, error = "box_mm needs min/max with x_mm/y_mm/z_mm." };
            var actual = new { min_mm = P(min), max_mm = P(max) };
            var dx = new[] { Math.Abs(min.X * 304.8 - exp[0]), Math.Abs(min.Y * 304.8 - exp[1]), Math.Abs(min.Z * 304.8 - exp[2]) };
            var dx2 = new[] { Math.Abs(max.X * 304.8 - exp[3]), Math.Abs(max.Y * 304.8 - exp[4]), Math.Abs(max.Z * 304.8 - exp[5]) };
            var worst = Math.Max(dx[0], Math.Max(dx[1], Math.Max(dx[2], Math.Max(dx2[0], Math.Max(dx2[1], dx2[2])))));
            return new { kind = "bbox", pass = worst <= tol, worst_mm = worst, tol_mm = tol, actual };
        }

        private static object CheckCount(Document doc, JObject a)
        {
            var expect = a.Value<int?>("expect");
            var minExpect = a.Value<int?>("min_expect");
            var maxExpect = a.Value<int?>("max_expect");
            var category = a.Value<string>("category");
            var level = a.Value<string>("level");
            var n = FilterElements(doc, category, level).Count;
            if (!VerifyCountPolicy.TryEvaluate(n, expect, minExpect, maxExpect, out var pass, out var error))
                return new { kind = "count", pass = false, actual = n, error };
            return new { kind = "count", pass, actual = n, expect, min_expect = minExpect, max_expect = maxExpect };
        }

        private static object CheckSolidVolume(Document doc, JObject a)
        {
            var minVolume = a.Value<double?>("min_volume_m3");
            var maxVolume = a.Value<double?>("max_volume_m3");
            if (!VerifySolidVolumePolicy.TryEvaluate(0, 1, minVolume, maxVolume, out _, out var policyError))
                return new { kind = "solid_volume", pass = false, error = policyError };

            var ids = a["ids"] as JArray;
            var category = a.Value<string>("category");
            var level = a.Value<string>("level");
            if ((ids == null || ids.Count == 0)
                && string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(level))
                return new { kind = "solid_volume", pass = false, error = "Specify ids or a category/level scope." };

            var elements = new List<Element>();
            if (ids != null && ids.Count > 0)
            {
                if (ids.Count > MaxSolidVolumeElements)
                    return new { kind = "solid_volume", pass = false, error = "ids contains " + ids.Count + " entries; provide at most " + MaxSolidVolumeElements + "." };
                var missing = new List<long>();
                var seen = new HashSet<long>();
                foreach (var token in ids)
                {
                    if (token.Type != JTokenType.Integer)
                        return new { kind = "solid_volume", pass = false, error = "ids must contain only integer element IDs." };
                    var id = token.Value<long>();
                    if (!RevitCompat.CanRepresentElementId(id))
                        return new { kind = "solid_volume", pass = false, error = RevitCompat.ElementIdRangeError(id) };
                    if (!seen.Add(id)) continue;
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null) missing.Add(id);
                    else elements.Add(element);
                }
                if (missing.Count > 0)
                    return new { kind = "solid_volume", pass = false, error = "elements not found", missing };
            }
            else
            {
                elements = FilterElements(doc, category, level, MaxSolidVolumeElements + 1);
            }

            if (elements.Count == 0)
                return new { kind = "solid_volume", pass = false, error = "No elements matched the requested scope." };
            if (elements.Count > MaxSolidVolumeElements)
                return new { kind = "solid_volume", pass = false, error = "Scope contains more than " + MaxSolidVolumeElements + " elements; narrow it to at most " + MaxSolidVolumeElements + "." };

            var measurements = new List<object>();
            var offendingIds = new List<long>();
            var errors = new List<object>();
            foreach (var element in elements)
            {
                var id = RevitCompat.GetId(element.Id);
                try
                {
                    var totalVolume = 0.0;
                    var solidCount = 0;
                    AccumulateSolidVolume(element.get_Geometry(new Options()), 0, ref totalVolume, ref solidCount);
                    var volumeM3 = totalVolume * CubicFeetToCubicMeters;
                    if (!VerifySolidVolumePolicy.TryEvaluate(volumeM3, solidCount, minVolume, maxVolume, out var pass, out var error))
                        return new { kind = "solid_volume", pass = false, error };
                    measurements.Add(new { id, solid_count = solidCount, has_solid = solidCount > 0, volume_m3 = volumeM3 });
                    if (!pass) offendingIds.Add(id);
                }
                catch (Exception ex)
                {
                    offendingIds.Add(id);
                    errors.Add(new { id, error = ex.Message });
                }
            }

            return new
            {
                kind = "solid_volume",
                pass = offendingIds.Count == 0,
                measured_count = measurements.Count,
                min_volume_m3 = minVolume,
                max_volume_m3 = maxVolume,
                measurements,
                offending_ids = offendingIds,
                errors = errors.Count > 0 ? errors : null
            };
        }

        private static void AccumulateSolidVolume(GeometryElement geometry, int depth, ref double totalVolume, ref int solidCount)
        {
            if (geometry == null) return;
            if (depth > 8) throw new InvalidOperationException("Geometry instance nesting exceeds the depth limit.");
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid solid)
                {
                    if (solid.Faces.Size == 0) continue;
                    var volume = solid.Volume;
                    if (double.IsNaN(volume) || double.IsInfinity(volume) || volume <= 1e-9) continue;
                    totalVolume += volume;
                    solidCount++;
                }
                else if (item is GeometryInstance instance)
                {
                    AccumulateSolidVolume(instance.GetInstanceGeometry(), depth + 1, ref totalVolume, ref solidCount);
                }
            }
        }

        private static List<Element> FilterElements(Document doc, string category, string level, int? stopAfter = null)
        {
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            var result = new List<Element>();
            ElementId levelId = null;
            if (!string.IsNullOrWhiteSpace(level))
            {
                foreach (Level lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    if (lv.Name.Equals(level, StringComparison.OrdinalIgnoreCase)) { levelId = lv.Id; break; }
                if (levelId == null) return result;
            }
            foreach (var e in collector)
            {
                if (!string.IsNullOrWhiteSpace(category))
                {
                    var cn = "";
                    try { cn = e.Category?.Name ?? ""; } catch { }
                    if (cn.IndexOf(category, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                if (levelId != null && (e.LevelId == null || !e.LevelId.Equals(levelId))) continue;
                result.Add(e);
                if (stopAfter.HasValue && result.Count >= stopAfter.Value) break;
            }
            return result;
        }

        private static double[] ReadBox(JObject box)
        {
            try
            {
                var min = box["min"] as JObject;
                var max = box["max"] as JObject;
                if (min == null || max == null) return null;
                return new[]
                {
                    min.Value<double>("x_mm"), min.Value<double>("y_mm"), min.Value<double>("z_mm"),
                    max.Value<double>("x_mm"), max.Value<double>("y_mm"), max.Value<double>("z_mm")
                };
            }
            catch { return null; }
        }

        private static object P(XYZ p) => new { x_mm = p.X * 304.8, y_mm = p.Y * 304.8, z_mm = p.Z * 304.8 };
    }
}
