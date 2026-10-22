// Usage:
//   stdio (default):  RvtMcp.Server.exe              — spawned by Claude/GPT/Cursor
//   HTTP SSE:          RvtMcp.Server.exe --http 8200  — for Ollama/LM Studio/custom
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{

    [McpServerToolType, Toolset("links")]
    public class LinksTools
    {
        [McpServerTool(Name = "revit_list_linked_models", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List external Revit linked models and instances.")]
        public static async Task<string> ListLinkedModels(bool includeInstances = true, bool includeUnloaded = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_linked_models", new { include_instances = includeInstances, include_unloaded = includeUnloaded });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_list_linked_cad", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List imported or linked CAD files in the model.")]
        public static async Task<string> ListLinkedCad(bool includeImports = true, bool includeLinks = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_linked_cad", new { include_imports = includeImports, include_links = includeLinks });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_cad_extract", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Extract bounded layer and vector geometry data from CAD imports or links already in the model. Arc center, radius, normal and sweep are included; polylines report whether their endpoints coincide. Does not modify the model. Revit may not preserve source text or block metadata.")]
        public static async Task<string> CadExtract(
            [System.ComponentModel.Description("One to 20 CAD import/link instance IDs returned by revit_list_linked_cad.")] long[] instance_ids,
            [System.ComponentModel.Description("Maximum entities returned across this call (1-1000), distributed across the selected instances.")] int entity_limit = 200,
            [System.ComponentModel.Description("Include bounded tessellated curve/polyline vertices in millimetres; arc parameters are included regardless. Response size grows with geometry complexity.")] bool include_vertices = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("cad_extract", new { instance_ids, entity_limit, include_vertices });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_cad_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Create a deterministic, versioned, read-only axis plan and optional straight-wall candidates from revit_cad_extract output. Wall rules pair parallel boundaries and report incomplete analysis; no geometry changes are applied.")]
        public static string CadPlan(
            [System.ComponentModel.Description("Complete JSON response from revit_cad_extract, including results and provenance.")] string extraction_json,
            [System.ComponentModel.Description("Versioned JSON rules: version and exact axis layers and/or wall_layers. wall_layers requires wall_thickness_mm; axis labels and tolerances are optional.")] string rules_json)
        {
            try { return Handlers.CadPlanBuilder.Build(extraction_json, rules_json).ToString(Formatting.Indented); }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [McpServerTool(Name = "revit_cad_apply", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Apply verified CAD wall candidates as Revit walls with a local provenance record; restore them with Revit Undo (batch journal). Requires a CadPlanIntegrity-valid revit_cad_plan result, explicit candidate selection (or all unverified), target level and height, dry_run=false AND confirm=true. At most 20 walls per apply (one undo step). Params: plan_json (required), selection_json (optional JSON array of 1-based candidate_index), level (required), height_mm (default 3000), dry_run (default true), confirm (default false), max_count (default 20).")]
        public static async Task<string> CadApply(string plan_json, string selection_json = null, string level = "", double height_mm = 3000, bool dry_run = true, int max_count = 20, bool confirm = false)
        {
            var blocked = ServerState.BlockIfReadOnly("cad_apply");
            if (blocked != null) return blocked;

            try
            {
                JObject plan;
                try { plan = JObject.Parse(plan_json ?? "{}"); }
                catch { return "Error: plan_json must be a JSON object from revit_cad_plan."; }
                if (!RvtMcp.Plugin.Handlers.CadPlanIntegrity.Validate(plan, out var integrityError))
                    return "Error: " + integrityError;
                var all = plan["wall_candidates"] as JArray ?? new JArray();
                var usable = all.OfType<JObject>()
                    .Where(c => string.Equals(c.Value<string>("status"), "candidate_unverified", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                List<JObject> selected;
                if (string.IsNullOrWhiteSpace(selection_json))
                {
                    selected = usable;
                }
                else
                {
                    JArray wanted;
                    try { wanted = JArray.Parse(selection_json); }
                    catch { return "Error: selection_json must be a JSON array of 1-based candidate_index values."; }
                    var byIndex = usable.ToDictionary(
                        c => c.Value<int>("candidate_index"),
                        c => c);
                    selected = new List<JObject>();
                    foreach (var token in wanted)
                    {
                        if (token.Type != JTokenType.Integer || !byIndex.TryGetValue(token.Value<int>(), out var candidate))
                            return "Error: selection_json references unknown or non-applicable candidate: " + token.ToString(Formatting.None) + ".";
                        selected.Add(candidate);
                    }
                }
                var levelName = (level ?? "").Trim();
                if (levelName.Length == 0 || levelName.Length > 128)
                    return "Error: level is required (1-128 characters).";
                if (!(height_mm > 0) || height_mm > 30000 || double.IsNaN(height_mm) || double.IsInfinity(height_mm))
                    return "Error: height_mm must be within 1-30000.";
                if (max_count < 1 || max_count > 20)
                    return "Error: max_count must be 1-20 (one undo step per apply).";
                if (selected.Count > max_count)
                    return "Error: " + selected.Count + " candidates selected (max_count=" + max_count + "). Narrow the selection or split applies.";
                if (selected.Count == 0)
                    return "Error: no applicable candidates in scope.";

                var preview = new JArray();
                foreach (var candidate in selected)
                {
                    preview.Add(new JObject
                    {
                        ["candidate_index"] = candidate.Value<int>("candidate_index"),
                        ["start_mm"] = candidate["start_mm"]?.DeepClone(),
                        ["end_mm"] = candidate["end_mm"]?.DeepClone(),
                        ["length_mm"] = candidate["length_mm"]?.DeepClone(),
                        ["thickness_mm"] = candidate["thickness_mm"]?.DeepClone(),
                    });
                }
                if (dry_run)
                    return JsonConvert.SerializeObject(new
                    {
                        dry_run = true,
                        count = selected.Count,
                        level = levelName,
                        height_mm,
                        candidates = preview,
                        apply_id = (string)null
                    }, Formatting.Indented);
                if (!confirm)
                    return "Error: pass confirm=true alongside dry_run=false to apply. Preview first with dry_run=true.";

                var commands = new JArray();
                foreach (var candidate in selected)
                {
                    var start = candidate["start_mm"] as JObject;
                    var end = candidate["end_mm"] as JObject;
                    if (start == null || end == null)
                        return "Error: candidate " + candidate.Value<int>("candidate_index") + " has no start_mm/end_mm.";
                    commands.Add(new JObject
                    {
                        ["command"] = "create_line_based_element",
                        ["params"] = new JObject
                        {
                            ["elementType"] = "wall",
                            ["startX"] = start.Value<double?>("x_mm"),
                            ["startY"] = start.Value<double?>("y_mm"),
                            ["endX"] = end.Value<double?>("x_mm"),
                            ["endY"] = end.Value<double?>("y_mm"),
                            ["level"] = levelName,
                            ["height"] = height_mm,
                        },
                    });
                }
                var batch = await ToolGateway.SendToRevit("batch_execute",
                    new { commands, continueOnError = false });
                var results = batch["results"] as JArray ?? new JArray();
                if (batch.Value<bool?>("rolledBack") == true)
                    return JsonConvert.SerializeObject(new
                    {
                        applied = false,
                        error = "Batch rolled back; nothing was created.",
                        batch,
                    }, Formatting.Indented);
                var mappings = new JArray();
                var createdIds = new List<long>();
                var idx = 0;
                foreach (var item in results.OfType<JObject>())
                {
                    var data = item["data"] as JObject;
                    var ids = new List<long>();
                    if (data != null)
                    {
                        foreach (var key in new[] { "elementId", "element_id" })
                        {
                            var token = data[key];
                            if (token != null && token.Type == JTokenType.Integer) ids.Add(token.Value<long>());
                        }
                        foreach (var key in new[] { "elementIds", "element_ids", "createdIds", "created_ids" })
                        {
                            if (data[key] is JArray arr)
                                ids.AddRange(arr.Where(x => x.Type == JTokenType.Integer).Select(x => x.Value<long>()));
                        }
                    }
                    createdIds.AddRange(ids);
                    mappings.Add(new JObject
                    {
                        ["candidate_index"] = idx < selected.Count ? selected[idx].Value<int>("candidate_index") : (int?)null,
                        ["ok"] = item.Value<bool?>("ok") ?? false,
                        ["element_ids"] = new JArray(ids.Distinct().OrderBy(x => x).Select(x => new JValue(x)).ToArray()),
                    });
                    idx++;
                }
                if (createdIds.Count == 0)
                    return JsonConvert.SerializeObject(new
                    {
                        applied = false,
                        error = "Batch reported success but yielded no element IDs; nothing was journaled.",
                        batch,
                    }, Formatting.Indented);
                var record = CadApplyStore.Record(
                    plan.Value<string>("plan_sha256"),
                    plan.Value<string>("rules_version"),
                    levelName, height_mm, mappings);
                return JsonConvert.SerializeObject(new
                {
                    applied = true,
                    apply_id = record.Value<string>("apply_id"),
                    count = createdIds.Count,
                    level = levelName,
                    height_mm,
                    mappings,
                    batch,
                }, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_cad_audit", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Compare versioned CAD axes against Revit grids and straight-wall candidates against Revit wall location lines and widths. Incomplete CAD analysis, unmatched Revit walls, or unsupported wall geometry fail the audit. Returns matching and offending IDs; never changes the model.")]
        public static async Task<string> CadAudit(
            [System.ComponentModel.Description("Complete JSON output from revit_cad_plan; its plan hash is verified before comparison.")] string plan_json,
            [System.ComponentModel.Description("Maximum permitted axis coordinate deviation in millimetres; default 10.")] double tolerance_mm = 10)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("cad_audit", new { plan_json, tolerance_mm });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_import_cad_to_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Import or link a CAD drawing (.dwg, .dxf) into a specific view.")]
        public static async Task<string> ImportCadToView(string path, long? viewId = null, bool link = false, string placement = "origin", string unit = "default", bool thisViewOnly = true, bool visibleLayersOnly = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("import_cad_to_view", new { path, view_id = viewId, link, placement, unit, this_view_only = thisViewOnly, visible_layers_only = visibleLayersOnly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_link_revit_model", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Link a Revit model (.rvt) into the project. timeout_seconds: plugin wait 1-900s, default 600 - linking a large model can exceed 60s.")]
        public static async Task<string> LinkRevitModel(string path, string placement = "origin", bool relative = false, bool reuseExistingType = false, int timeout_seconds = 600)
        {
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            try
            {
                var result = await ToolGateway.SendToRevit("link_revit_model", new { path, placement, relative, reuse_existing_type = reuseExistingType }, timeout_seconds);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_unload_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Unload a Revit link type by type or instance ID." +
            " Changes link loading outside a document transaction. Save a model backup first; Revit Undo may not restore this operation.")]
        public static async Task<string> UnloadLink(long? linkTypeId = null, long? linkInstanceId = null, string scope = "all_users")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("unload_link", new { link_type_id = linkTypeId, link_instance_id = linkInstanceId, scope });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_reload_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Reload a Revit link type by type or instance ID. timeout_seconds: plugin wait 1-900s, default 600 - reloading a large link can exceed 60s." +
            " Changes link loading outside a document transaction. Save a model backup first; Revit Undo may not restore this operation.")]
        public static async Task<string> ReloadLink(long? linkTypeId = null, long? linkInstanceId = null, int timeout_seconds = 600)
        {
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            try
            {
                var result = await ToolGateway.SendToRevit("reload_link", new { link_type_id = linkTypeId, link_instance_id = linkInstanceId }, timeout_seconds);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_get_link_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Retrieve elements from a Revit link instance document.")]
        public static async Task<string> GetLinkElements(long linkInstanceId, string category = "", int limit = 500, bool includeBoundingBox = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_link_elements", new { link_instance_id = linkInstanceId, category, limit, include_bounding_box = includeBoundingBox });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_get_project_coordinate_system", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read the complete project coordinate setup: immutable Internal Origin, Project Base Point, Survey Point, active and named Project Locations, coordinates under each location, angle to True North, and geographic site data. Lengths are returned in mm; angles and latitude/longitude in degrees.")]
        public static async Task<string> GetProjectCoordinateSystem()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_project_coordinate_system", new { });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_get_link_coordinate_system", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Inspect a Revit or CAD link's instance and total transforms and map its origins into host internal/shared coordinates. For a loaded Revit link, also returns its Internal Origin, Project Base Point, Survey Point, and all linked Project Locations with ids. Use the returned Project Location id as linkedProjectLocationId for revit_publish_coordinates_to_link.")]
        public static async Task<string> GetLinkCoordinateSystem(long linkInstanceId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_link_coordinate_system", new { link_instance_id = linkInstanceId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_acquire_coordinates_from_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace host shared coordinates; restore them with Revit Undo. Acquire shared coordinates from a Revit link or linked CAD instance into the host project. Requires confirm=true and modifies the host shared-coordinate system.")]
        public static async Task<string> AcquireCoordinatesFromLink(long linkInstanceId, bool confirm = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("acquire_coordinates_from_link", new { link_instance_id = linkInstanceId, confirm });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_publish_coordinates_to_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Publish host shared coordinates to a loaded Revit link. Call revit_get_link_coordinate_system first to discover linkedProjectLocationId; omit it to target the linked active Project Location. Requires confirm=true. CAD links are not supported." +
            " Publishes changes to the linked model. Back up that model first; restoring its prior file may be required.")]
        public static async Task<string> PublishCoordinatesToLink(long linkInstanceId, long? linkedProjectLocationId = null, bool confirm = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("publish_coordinates_to_link", new { link_instance_id = linkInstanceId, linked_project_location_id = linkedProjectLocationId, confirm });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }

        [McpServerTool(Name = "revit_set_project_base_point", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace base point or survey point values; restore them with Revit Undo. Set project base point or survey point parameters.")]
        public static async Task<string> SetProjectBasePoint(double eastWest, double northSouth, double elevation = 0, double angleToTrueNorth = 0, string pointKind = "project_base_point", bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_project_base_point", new { east_west = eastWest, north_south = northSouth, elevation, angle_to_true_north = angleToTrueNorth, point_kind = pointKind, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return ToolErrors.Format(ex); }
        }
    }
}
