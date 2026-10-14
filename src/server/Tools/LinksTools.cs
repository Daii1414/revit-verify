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
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_linked_cad", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List imported or linked CAD files in the model.")]
        public static async Task<string> ListLinkedCad(bool includeImports = true, bool includeLinks = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_linked_cad", new { include_imports = includeImports, include_links = includeLinks });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_cad_extract", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Extract bounded layer and vector geometry data from CAD imports or links already in the model. Does not modify the model. Revit may not preserve source text or block metadata.")]
        public static async Task<string> CadExtract(
            [System.ComponentModel.Description("One to 20 CAD import/link instance IDs returned by revit_list_linked_cad.")] long[] instance_ids,
            [System.ComponentModel.Description("Maximum entities returned across this call (1-1000), distributed across the selected instances.")] int entity_limit = 200,
            [System.ComponentModel.Description("Include tessellated curve/polyline vertices in millimetres; response size grows with geometry complexity.")] bool include_vertices = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("cad_extract", new { instance_ids, entity_limit, include_vertices });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_cad_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Create a deterministic, versioned, read-only axis candidate plan from revit_cad_extract output. Never applies geometry changes.")]
        public static string CadPlan(
            [System.ComponentModel.Description("Complete JSON response from revit_cad_extract, including results and provenance.")] string extraction_json,
            [System.ComponentModel.Description("Versioned JSON rules: version, exact x_axis_layers/y_axis_layers, optional ordered labels, and tolerances.")] string rules_json)
        {
            try { return Handlers.CadPlanBuilder.Build(extraction_json, rules_json).ToString(Formatting.Indented); }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [McpServerTool(Name = "revit_cad_audit", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Compare a versioned CAD axis plan against existing straight Revit grids. Returns matching and offending Revit element IDs; never changes the model.")]
        public static async Task<string> CadAudit(
            [System.ComponentModel.Description("Complete JSON output from revit_cad_plan; its plan hash is verified before comparison.")] string plan_json,
            [System.ComponentModel.Description("Maximum permitted axis coordinate deviation in millimetres; default 10.")] double tolerance_mm = 10)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("cad_audit", new { plan_json, tolerance_mm });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_import_cad_to_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Import or link a CAD drawing (.dwg, .dxf) into a specific view.")]
        public static async Task<string> ImportCadToView(string path, long? viewId = null, bool link = false, string placement = "origin", string unit = "default", bool thisViewOnly = true, bool visibleLayersOnly = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("import_cad_to_view", new { path, view_id = viewId, link, placement, unit, this_view_only = thisViewOnly, visible_layers_only = visibleLayersOnly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
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
            catch (Exception ex) { return $"Error: {ex.Message}"; }
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
            catch (Exception ex) { return $"Error: {ex.Message}"; }
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
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_link_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Retrieve elements from a Revit link instance document.")]
        public static async Task<string> GetLinkElements(long linkInstanceId, string category = "", int limit = 500, bool includeBoundingBox = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_link_elements", new { link_instance_id = linkInstanceId, category, limit, include_bounding_box = includeBoundingBox });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_project_coordinate_system", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read the complete project coordinate setup: immutable Internal Origin, Project Base Point, Survey Point, active and named Project Locations, coordinates under each location, angle to True North, and geographic site data. Lengths are returned in mm; angles and latitude/longitude in degrees.")]
        public static async Task<string> GetProjectCoordinateSystem()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_project_coordinate_system", new { });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_link_coordinate_system", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Inspect a Revit or CAD link's instance and total transforms and map its origins into host internal/shared coordinates. For a loaded Revit link, also returns its Internal Origin, Project Base Point, Survey Point, and all linked Project Locations with ids. Use the returned Project Location id as linkedProjectLocationId for revit_publish_coordinates_to_link.")]
        public static async Task<string> GetLinkCoordinateSystem(long linkInstanceId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_link_coordinate_system", new { link_instance_id = linkInstanceId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_acquire_coordinates_from_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace host shared coordinates; restore them with Revit Undo. Acquire shared coordinates from a Revit link or linked CAD instance into the host project. Requires confirm=true and modifies the host shared-coordinate system.")]
        public static async Task<string> AcquireCoordinatesFromLink(long linkInstanceId, bool confirm = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("acquire_coordinates_from_link", new { link_instance_id = linkInstanceId, confirm });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
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
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_project_base_point", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace base point or survey point values; restore them with Revit Undo. Set project base point or survey point parameters.")]
        public static async Task<string> SetProjectBasePoint(double eastWest, double northSouth, double elevation = 0, double angleToTrueNorth = 0, string pointKind = "project_base_point", bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_project_base_point", new { east_west = eastWest, north_south = northSouth, elevation, angle_to_true_north = angleToTrueNorth, point_kind = pointKind, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
