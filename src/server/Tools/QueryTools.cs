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

    // =====================================================================
    // Toolset classes — one per aspect #3 §A3 group. Registration happens in
    // Program.RegisterToolsets() driven by config.Toolsets. Each method wraps
    // ToolGateway.SendToRevit with a catch-all that surfaces the error to the
    // MCP client as plain text instead of throwing.
    // =====================================================================

    [McpServerToolType, Toolset("query")]
    public class QueryTools
    {
        [McpServerTool(Name = "revit_get_current_view_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get active view info. Returns viewName, viewType (FloorPlan/Section/3D/Sheet), level, scale, detailLevel, displayStyle. Call before creating elements to know active level.")]
        public static async Task<string> GetCurrentViewInfo()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_current_view_info");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_selected_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get currently selected Revit elements with paging. Use startIndex + maxResults (default 200, hard max 1000).")]
        public static async Task<string> GetSelectedElements(int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_selected_elements", new { start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_available_family_types", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List loadable family types. Returns {familyName, typeName, typeId} grouped by category. Optional: filter by category (e.g. 'Walls', 'Doors', 'Pipes' — NOT 'OST_Walls'). Feed typeId into create_point_based_element.")]
        public static async Task<string> GetAvailableFamilyTypes(string category = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_available_family_types", new { category });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_ai_element_filter", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Filter elements by category + parameter. Numeric values in mm (auto-converted). category uses human name ('Pipes', NOT 'OST_Pipes'). Operators: equals/contains/startswith/greaterthan/lessthan. select=true highlights results. Example: category='Pipes', parameterName='Diameter', parameterValue='200', operator='greaterthan', select=true.")]
        public static async Task<string> AiElementFilter(string category, string parameterName = "", string parameterValue = "", string @operator = "equals", int limit = 100, bool select = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("ai_element_filter", new { category, parameterName, parameterValue, @operator, limit, select });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_resolve_targets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Resolve fuzzy user terms to exact Revit names (levels, categories, families, types) with ids. Call before creating or filtering when the exact name is unknown. Params: term (required substring), kinds subset of level/category/family/type (default all), limit (default 20).")]
        public static async Task<string> ResolveTargets(string term, string[] kinds = null, int limit = 20)
        {
            var blocked = ServerState.BlockIfReadOnly("resolve_targets");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("resolve_targets", new { term, kinds, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_reflect_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read one property of one element by path without send_code. Path is one of: id, name, category, type, level, host, location, box, param:<Name> (instance first, then type; no nesting). Unset values return null with a note instead of failing. Read-only. Params: element_id (required), path (required).")]
        public static async Task<string> ReflectGet(long element_id, string path)
        {
            var blocked = ServerState.BlockIfReadOnly("reflect_get");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("reflect_get", new { element_id, path });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_verify_assert", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Check measurable post-conditions: bbox, exact/ranged count, per-element solid volume, edge closure (every inspectable solid edge has two adjacent faces and nonzero volume; ids or category/level scope, max 200), and hosting/level support. Read-only; edge closure does not detect self-intersections and is not a complete watertightness proof. Support checks hosting and level references only, not structural capacity. Params: assertions_json (required JSON array).")]
        public static async Task<string> VerifyAssert(string assertions_json)
        {
            var blocked = ServerState.BlockIfReadOnly("verify_assert");
            if (blocked != null) return blocked;

            try
            {
                var parsed = JArray.Parse(assertions_json);
                var result = await ToolGateway.SendToRevit("verify_assert", new { assertions = parsed });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_verify_run_rules", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Run a YAML QA ruleset: each rule fans out to verify_assert kinds (bbox/count/solid_volume/edge_closure/support) or, with snapshot_json, to compare_model_snapshot. Returns per-rule pass/fail with offender IDs, a weighted score 0-100, and refuses stale runs when model_key (copied from capture_model_snapshot) mismatches the live model. Snapshot rules rely on the compare identity gate and report findings as offenders. Read-only. Params: rules_yaml (required schema v1: version/rules[id/description/weight/assertions]), stop_on_first_fail (default false). Max 10 rules, 10 assertions per rule, 64 KiB input.")]
        public static async Task<string> VerifyRunRules(string rules_yaml, bool stop_on_first_fail = false)
        {
            var blocked = ServerState.BlockIfReadOnly("verify_run_rules");
            if (blocked != null) return blocked;

            VerifyRulesYamlParser.RuleSet rules;
            try
            {
                rules = VerifyRulesYamlParser.Parse(rules_yaml);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }

            try
            {
                var result = await VerifyRulesRunner.RunAsync(
                    rules, stop_on_first_fail, (command, parameters) => ToolGateway.SendToRevit(command, parameters));
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_qa_trend", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Record and review local QA score history (typically revit_verify_run_rules scores). Stored locally, never sent anywhere. Params: operation ('record'|'show', default show), score (0-100, record only), model_key/rules_passed/rules_total/note (record metadata, note max 500 chars), model_key filter + limit 1-200 (show). Max 200 entries, oldest pruned automatically.")]
        public static async Task<string> QaTrend(string operation = "show", int? score = null, string model_key = null, int? rules_passed = null, int? rules_total = null, string note = null, int limit = 20)
        {
            await Task.Yield();
            var blocked = ServerState.BlockIfReadOnly("qa_trend");
            if (blocked != null) return blocked;
            try
            {
                var op = (operation ?? "").ToLowerInvariant();
                if (op == "record") return RecordQaEntry(score, model_key, rules_passed, rules_total, note);
                if (op == "show") return ShowQaTrend(model_key, limit);
                return "Error: operation must be 'record' or 'show'.";
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        internal static string QaTrendRootOverride { get; set; }

        private static string QaTrendPath()
        {
            var dir = QaTrendRootOverride
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RvtVerify", "qa");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "trend.json");
        }

        private static JArray LoadQaTrend()
        {
            var path = QaTrendPath();
            if (!File.Exists(path))
                return new JArray();
            try
            {
                var parsed = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
                return parsed as JArray ?? new JArray();
            }
            catch
            {
                return new JArray();
            }
        }

        private static void SaveQaTrend(JArray entries)
        {
            var path = QaTrendPath();
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, entries.ToString(Formatting.None), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static string RecordQaEntry(int? score, string modelKey, int? passed, int? total, string note)
        {
            if (!score.HasValue || score.Value < 0 || score.Value > 100)
                return "Error: score 0-100 is required.";
            if ((passed ?? 0) < 0 || (total ?? 0) < 0)
                return "Error: rules_passed/rules_total must be non-negative.";
            if (passed.HasValue && total.HasValue && passed.Value > total.Value)
                return "Error: rules_passed cannot exceed rules_total.";
            var text = (note ?? "").Trim();
            if (text.Length > 500)
                return "Error: note exceeds 500 chars.";
            var entries = LoadQaTrend();
            entries.Add(new JObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["score"] = score.Value,
                ["rules_passed"] = passed,
                ["rules_total"] = total,
                ["model_key"] = (modelKey ?? "").Trim(),
                ["note"] = text,
                ["created_utc"] = DateTimeOffset.UtcNow.ToString("o")
            });
            while (entries.Count > 200) entries.RemoveAt(0);
            SaveQaTrend(entries);
            return JsonConvert.SerializeObject(new { recorded = true, score = score.Value, entries = entries.Count }, Formatting.Indented);
        }

        private static string ShowQaTrend(string modelKey, int limit)
        {
            if (limit < 1 || limit > 200)
                return "Error: limit must be 1-200.";
            var filter = (modelKey ?? "").Trim();
            var matches = LoadQaTrend()
                .OfType<JObject>()
                .Where(e => filter.Length == 0 || string.Equals(e.Value<string>("model_key") ?? "", filter, StringComparison.Ordinal))
                .Reverse()
                .Take(limit)
                .ToArray();
            JObject summary = null;
            if (matches.Length > 0)
            {
                var scores = matches.Select(e => e.Value<int>("score")).ToArray();
                summary = new JObject
                {
                    ["count"] = matches.Length,
                    ["first_score"] = scores.Last(),
                    ["last_score"] = scores.First(),
                    ["delta"] = scores.First() - scores.Last(),
                    ["best"] = scores.Max(),
                    ["worst"] = scores.Min(),
                };
            }
            return JsonConvert.SerializeObject(new
            {
                count = matches.Length,
                filter = filter,
                summary,
                entries = matches
            }, Formatting.Indented);
        }

        [McpServerTool(Name = "revit_capture_model_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Capture a bounded, hashed snapshot of explicit element IDs or categories in a saved model. Returns privacy-minimized JSON to pass to revit_compare_model_snapshot; no file or model changes. Geometry hashes use approximate Revit tessellation. Provide exactly one of element_ids or category_ids in scope_json.")]
        public static async Task<string> CaptureModelSnapshot(string scope_json)
        {
            try
            {
                var parsed = JObject.Parse(scope_json ?? "{}");
                var result = await ToolGateway.SendToRevit("capture_model_snapshot", parsed);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_compare_model_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Compare a snapshot JSON returned by revit_capture_model_snapshot against the active model. Reports added/removed/modified/unchanged/unverified elements; incomplete evidence never passes. Read-only. Params: snapshot_json.")]
        public static async Task<string> CompareModelSnapshot(string snapshot_json)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("compare_model_snapshot", new { snapshot_json });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_check_model_health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Single-call model health: warnings by severity, element/CAD/link/in-place counts, 0-100 score with grade (A-F) and recommendations. Read-only. One call instead of warnings + statistics separately. Params: maxElements (default 100000), max_warning_types (default 25).")]
        public static async Task<string> CheckModelHealth(int? maxElements = null, int max_warning_types = 25)
        {
            var blocked = ServerState.BlockIfReadOnly("check_model_health");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("check_model_health", new { maxElements, max_warning_types });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_model_statistics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Count elements grouped by category (Walls, Doors, Pipes, etc.). Call to understand project scope before detailed queries. Iteration is capped at maxElements (default 100000); raise it for full accuracy on very large models at the cost of a longer UI freeze.")]
        public static async Task<string> AnalyzeModelStatistics(int? maxElements = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_model_statistics", new { maxElements });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_material_quantities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Sum material quantities (area m², volume m³) by category. Narrow with materialNameFilter and page with startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetMaterialQuantities(string category, string materialNameFilter = "", int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_material_quantities", new { category, material_name_filter = materialNameFilter, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read detailed metadata for one or more elements. Returns identity, category, type, level, workset, phase, owner view, design option, group/assembly ids, location, and bounding box in mm.")]
        public static async Task<string> GetElementDetails(long[] elementIds)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_details", new { elementIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read instance parameters for one or more elements. Returns storage type, read-only state, display value, raw value, and data/spec ids.")]
        public static async Task<string> GetElementParameters(long[] elementIds, bool includeReadOnly = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_parameters", new { elementIds, includeReadOnly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_type_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read type parameters from explicit type ids or from the types of provided element ids.")]
        public static async Task<string> GetTypeParameters(long[] elementIds = null, long[] typeIds = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_type_parameters", new { elementIds, typeIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_project_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List project/shared parameter bindings, including instance/type binding kind and bound categories.")]
        public static async Task<string> ListProjectParameters(bool includeCategories = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_project_parameters", new { includeCategories });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_relationships", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read host, group, assembly, owner view, design option, family nesting, and dependent-element relationships for elements.")]
        public static async Task<string> GetElementRelationships(long[] elementIds, bool includeDependents = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_relationships", new { elementIds, includeDependents });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_survey_change_impact", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [System.ComponentModel.Description("Survey ten relationship groups for explicit active-document targets before proposing a change. Returns found/none/not_checked, coverage, exact/lower-bound counts and stopFlags; never an authorization to write. Linked contents and engineering correctness are not covered. Views/schedules are not scanned unless maxViews > 0; native view calls can exceed the soft time budget. Observational parameters are not trusted history baselines.")]
        public static async Task<string> SurveyChangeImpact(
            [System.ComponentModel.Description("1-25 active-document element IDs; never an implicit whole-model scope.")] long[] elementIds,
            [System.ComponentModel.Description("Required scope threshold agreed for this request, 1-200000. Counts target/propagation candidates, not predicted modifications. No fixed default.")] int scopeThreshold,
            [System.ComponentModel.Description("instance|type|unknown. Type means editing a type definition; assigning a different type to instances is instance.")] string changeKind = "unknown",
            [System.ComponentModel.Description("1 or 2. Depth2 expands type/group/join/physical-connector neighbors only.")] int depth = 1,
            [System.ComponentModel.Description("sheets|all; applies only when maxViews > 0.")] string viewScope = "sheets",
            [System.ComponentModel.Description("Optional explicit phase ID (0 can be valid); otherwise use a valid active-view phase, never the last project phase.")] long? phaseId = null,
            [System.ComponentModel.Description("Optional IDs of up to 100 views within viewScope. Does not implicitly enable view scanning.")] long[] viewIds = null,
            [System.ComponentModel.Description("Bounding-box candidate padding in millimeters, 0-10000; this is not clash detection.")] double proximityPaddingMm = 100,
            [System.ComponentModel.Description("Returned ID/evidence preview cap per group, 1-200; counts remain independent of this cap.")] int maxIdsPerRelation = 100,
            [System.ComponentModel.Description("Soft elapsed-time budget, 1-30000 ms. Stops between native API calls; cannot preempt a native call.")] int budgetMs = 10000,
            [System.ComponentModel.Description("0-100. Default 0 skips view/schedule iteration. Set a positive value only for an explicitly scoped presentation survey.")] int maxViews = 0,
            [System.ComponentModel.Description("Shared scan-work cap, 1-200000; exhausted coverage remains partial.")] int maxScannedElements = 50000,
            [System.ComponentModel.Description("Distinct discovered-node cap per relation and propagation scope, 1-20000.")] int maxGraphNodes = 10000)
        {
            try
            {
                var request = new JObject
                {
                    ["elementIds"] = elementIds == null ? JValue.CreateNull() : new JArray(elementIds),
                    ["scopeThreshold"] = scopeThreshold, ["changeKind"] = changeKind, ["depth"] = depth,
                    ["viewScope"] = viewScope, ["proximityPaddingMm"] = proximityPaddingMm,
                    ["maxIdsPerRelation"] = maxIdsPerRelation, ["budgetMs"] = budgetMs,
                    ["maxViews"] = maxViews, ["maxScannedElements"] = maxScannedElements, ["maxGraphNodes"] = maxGraphNodes
                };
                if (phaseId.HasValue) request["phaseId"] = phaseId.Value;
                if (viewIds != null) request["viewIds"] = new JArray(viewIds);
                RvtMcp.Plugin.Survey.SurveyOptions.Parse(request);
                var result = await ToolGateway.SendToRevit("survey_change_impact", request);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_groups", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List model/detail/attached groups with type, owner view, parent, and optional member ids.")]
        public static async Task<string> ListGroups(string groupKind = "all", bool includeMembers = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_groups", new { groupKind, includeMembers });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_group_members", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read a group instance and a page of member elements. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetGroupMembers(long groupId, int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_group_members", new { groupId, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_assemblies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List a page of assembly instances. maxMembersPerAssembly caps optional member-id previews.")]
        public static async Task<string> ListAssemblies(bool includeMembers = false, int startIndex = 0, int maxResults = 100, int maxMembersPerAssembly = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_assemblies", new { includeMembers, start_index = startIndex, max_results = maxResults, max_members_per_assembly = maxMembersPerAssembly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_assembly_members", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read an assembly instance and a page of members. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetAssemblyMembers(long assemblyId, int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_assembly_members", new { assemblyId, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_worksets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List document worksets and active workset. Optionally includes per-workset element counts.")]
        public static async Task<string> ListWorksets(bool includeElementCounts = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_worksets", new { includeElementCounts });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
