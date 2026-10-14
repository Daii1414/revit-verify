using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Reads the vector geometry Revit exposes for already-imported or linked CAD.
    /// It deliberately does not import, link, or otherwise modify the active document.
    /// </summary>
    public sealed class CadExtractHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;
        private const int MaxInstances = 20;
        private const int DefaultEntityLimit = 200;
        private const int MaxEntityLimit = 1000;
        private const int MaxVerticesPerEntity = 128;

        public string Name => "cad_extract";
        public string Description => "Read bounded layer and vector-geometry data from CAD imports or links already present in the model. Does not modify the model. Revit does not preserve all source text or block metadata.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""instance_ids""],
  ""properties"": {
    ""instance_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 20 },
    ""entity_limit"": { ""type"": ""integer"", ""description"": ""Maximum returned entities across the entire call, distributed across the selected instances."", ""default"": 200, ""minimum"": 1, ""maximum"": 1000 },
    ""include_vertices"": { ""type"": ""boolean"", ""default"": false }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var ids = request["instance_ids"] as JArray;
            if (ids == null || ids.Count == 0) return CommandResult.Fail("instance_ids must be a non-empty array.");
            if (ids.Count > MaxInstances) return CommandResult.Fail("instance_ids accepts at most " + MaxInstances + " CAD instances per call.");

            var limit = request.Value<int?>("entity_limit") ?? DefaultEntityLimit;
            if (limit < 1 || limit > MaxEntityLimit)
                return CommandResult.Fail("entity_limit must be between 1 and " + MaxEntityLimit + ".");
            if (limit < ids.Count)
                return CommandResult.Fail("entity_limit must be at least the number of requested instances so each instance can be represented.");
            var includeVertices = request.Value<bool?>("include_vertices") ?? false;

            var results = new List<object>();
            var failed = new List<object>();
            var baseQuota = limit / ids.Count;
            var remainder = limit % ids.Count;
            for (var index = 0; index < ids.Count; index++)
            {
                var token = ids[index];
                if (token.Type != JTokenType.Integer)
                {
                    failed.Add(new { instance_id = (long?)null, error = "Each instance_ids value must be an integer." });
                    continue;
                }
                var id = token.Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    failed.Add(new { instance_id = id, error = RevitCompat.ElementIdRangeError(id) });
                    continue;
                }

                var element = doc.GetElement(RevitCompat.ToElementId(id));
                if (!(element is ImportInstance cad))
                {
                    failed.Add(new { instance_id = id, error = "Element is not a CAD import or link in this document." });
                    continue;
                }

                var instanceLimit = baseQuota + (index < remainder ? 1 : 0);
                try { results.Add(Extract(doc, cad, instanceLimit, includeVertices)); }
                catch (Exception ex) { failed.Add(new { instance_id = id, error = "CAD geometry extraction failed: " + ex.Message }); }
            }

            return CommandResult.Ok(new
            {
                requested = ids.Count,
                returned = results.Count,
                results,
                failed,
                read_only = true,
                limitations = new[]
                {
                    "Revit exposes imported CAD as converted geometry; this is not a lossless DWG/DXF parser.",
                    "Source text, block attributes, and original entity metadata may be unavailable or converted to curves."
                }
            });
        }

        private static object Extract(Document doc, ImportInstance cad, int entityLimit, bool includeVertices)
        {
            var entities = new List<object>();
            var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var geometryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            var total = 0;
            var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
            var geometry = cad.get_Geometry(options);
            if (geometry != null)
                foreach (GeometryObject item in geometry)
                    Traverse(doc, item, entities, layerCounts, geometryCounts, warnings, ref total, entityLimit, includeVertices, 0);

            var box = cad.get_BoundingBox(null);
            object bounds = box == null ? null : new
            {
                min_mm = Point(box.Min),
                max_mm = Point(box.Max)
            };

            return new
            {
                instance_id = RevitCompat.GetId(cad.Id),
                type_id = RevitCompat.GetId(cad.GetTypeId()),
                name = cad.Name ?? string.Empty,
                is_linked = cad.IsLinked,
                source_path = ReadSourcePath(doc, cad),
                bounding_box = bounds,
                total_entity_count = total,
                returned_entity_count = entities.Count,
                truncated = total > entities.Count,
                layers = layerCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => new { name = x.Key, entity_count = x.Value }).ToArray(),
                geometry_counts = geometryCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => new { type = x.Key, count = x.Value }).ToArray(),
                entities,
                warnings
            };
        }

        private static void Traverse(Document doc, GeometryObject item, List<object> entities,
            Dictionary<string, int> layerCounts, Dictionary<string, int> geometryCounts,
            List<string> warnings, ref int total, int entityLimit, bool includeVertices, int depth)
        {
            if (item == null) return;
            if (depth > 16) { warnings.Add("Geometry nesting exceeded the safety limit of 16."); return; }

            if (item is GeometryInstance instance)
            {
                try
                {
                    var nested = instance.GetInstanceGeometry();
                    if (nested != null)
                        foreach (GeometryObject child in nested)
                            Traverse(doc, child, entities, layerCounts, geometryCounts, warnings, ref total, entityLimit, includeVertices, depth + 1);
                }
                catch (Exception ex) { warnings.Add("Could not read nested CAD geometry: " + ex.Message); }
                return;
            }

            var type = GeometryType(item);
            if (type == null) return;
            var layer = ResolveLayer(doc, item.GraphicsStyleId) ?? "(unassigned)";
            total++;
            Increment(layerCounts, layer);
            Increment(geometryCounts, type);
            if (entities.Count >= entityLimit) return;

            if (item is Curve curve)
            {
                var entry = new Dictionary<string, object>
                {
                    ["layer"] = layer,
                    ["type"] = type,
                    ["length_mm"] = Math.Round(curve.Length * FeetToMm, 3),
                    ["is_bound"] = curve.IsBound
                };
                if (curve is Arc arc)
                {
                    entry["center_mm"] = Point(arc.Center);
                    entry["radius_mm"] = Math.Round(arc.Radius * FeetToMm, 3);
                    entry["normal"] = Vector(arc.Normal);
                    if (CadArcMetrics.TryGetSweepDegrees(arc.Length, arc.Radius, arc.IsCyclic, out var sweepDegrees))
                        entry["sweep_degrees"] = Math.Round(sweepDegrees, 6);
                    else
                        warnings.Add("An arc had invalid length or radius; its sweep angle was omitted.");
                }
                if (curve.IsBound)
                {
                    entry["start_mm"] = Point(curve.GetEndPoint(0));
                    entry["end_mm"] = Point(curve.GetEndPoint(1));
                    if (includeVertices)
                    {
                        var points = curve.Tessellate();
                        entry["vertices_mm"] = points.Take(MaxVerticesPerEntity).Select(Point).ToArray();
                        entry["vertices_truncated"] = points.Count > MaxVerticesPerEntity;
                    }
                }
                entities.Add(entry);
            }
            else if (item is PolyLine polyline)
            {
                var points = polyline.GetCoordinates();
                var entry = new Dictionary<string, object>
                {
                    ["layer"] = layer,
                    ["type"] = type,
                    ["vertex_count"] = points.Count,
                    ["endpoints_coincident"] = points.Count > 2 && points[0].DistanceTo(points[points.Count - 1]) <= 1e-6
                };
                if (includeVertices)
                {
                    entry["vertices_mm"] = points.Take(MaxVerticesPerEntity).Select(Point).ToArray();
                    entry["vertices_truncated"] = points.Count > MaxVerticesPerEntity;
                }
                entities.Add(entry);
            }
            else
            {
                entities.Add(new { layer, type });
            }
        }

        private static string GeometryType(GeometryObject item)
        {
            if (item is Curve curve) return "curve:" + curve.GetType().Name;
            if (item is PolyLine) return "polyline";
            if (item is Solid) return "solid";
            if (item is Mesh) return "mesh";
            if (item is Point) return "point";
            return null;
        }

        private static string ResolveLayer(Document doc, ElementId styleId)
        {
            if (styleId == null || styleId == ElementId.InvalidElementId) return null;
            var style = doc.GetElement(styleId) as GraphicsStyle;
            return style?.GraphicsStyleCategory?.Name;
        }

        private static string ReadSourcePath(Document doc, ImportInstance cad)
        {
            try
            {
                var typeId = cad.GetTypeId();
                if (!ExternalFileUtils.IsExternalFileReference(doc, typeId)) return null;
                var reference = ExternalFileUtils.GetExternalFileReference(doc, typeId);
                var path = reference?.GetPath();
                return path == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch { return null; }
        }

        private static object Point(XYZ p) => new
        {
            x_mm = Math.Round(p.X * FeetToMm, 3),
            y_mm = Math.Round(p.Y * FeetToMm, 3),
            z_mm = Math.Round(p.Z * FeetToMm, 3)
        };

        private static object Vector(XYZ v) => new
        {
            x = Math.Round(v.X, 9),
            y = Math.Round(v.Y, 9),
            z = Math.Round(v.Z, 9)
        };

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var value);
            counts[key] = value + 1;
        }
    }
}
