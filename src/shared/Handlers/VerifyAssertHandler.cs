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
        public string Name => "verify_assert";
        public string Description => "Check measurable post-conditions against the model: bounding boxes and element counts with tolerances. Read-only. Returns per-assertion pass/fail with offending ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""assertions"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""bbox"",""count""]},""ids"":{""type"":""array"",""items"":{""type"":""integer""}},""category"":{""type"":""string""},""level"":{""type"":""string""},""box_mm"":{""type"":""object""},""tol_mm"":{""type"":""number"",""default"":10},""expect"":{""type"":""integer""}}},""description"":""bbox: union box of ids (or category+level filter) must match box_mm within tol_mm per axis. count: instance count must equal expect.""}},""required"":[""assertions""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var arr = req["assertions"] as JArray;
            if (arr == null || !arr.Any())
                return CommandResult.Fail("assertions non-empty array is required.");

            var results = new List<object>();
            var allPass = true;
            foreach (var a in arr)
            {
                var kind = (a.Value<string>("kind") ?? "").ToLowerInvariant();
                try
                {
                    if (kind == "bbox") results.Add(CheckBox(doc, (JObject)a));
                    else if (kind == "count") results.Add(CheckCount(doc, (JObject)a));
                    else results.Add(new { kind, pass = false, error = "unknown kind (bbox|count)" });
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
            if (a["expect"] == null) return new { kind = "count", pass = false, error = "expect is required." };
            var expect = a.Value<int>("expect");
            var category = a.Value<string>("category");
            var level = a.Value<string>("level");
            var n = FilterElements(doc, category, level).Count;
            return new { kind = "count", pass = n == expect, actual = n, expect };
        }

        private static List<Element> FilterElements(Document doc, string category, string level)
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
                if (levelId != null && e.LevelId != null && !e.LevelId.Equals(levelId)) continue;
                result.Add(e);
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
