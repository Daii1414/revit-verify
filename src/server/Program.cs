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
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Any(a => a == "--help" || a == "-h"))
            {
                PrintHelp();
                return;
            }

            if (args.Any(a => a == "--doctor"))
            {
                Environment.ExitCode = RunDoctor();
                return;
            }

            // A9 3-layer config precedence (JSON < env < CLI). The --target selector and
            // transport mode (--http) stay as separate CLI parses for now; A3 toolsets
            // gating uses RvtMcpConfig.
            if (!LegacyDataMigration.TryMigrateOnce(Console.Error.WriteLine))
            { Environment.ExitCode = 1; return; }
            AuthToken.CleanupLegacyDiscoveryFiles();
            var config = RvtMcpConfig.Load(args);
            config.ValidateResponseLimits();
            ServerState.Config = config;
            ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(
                ToolsetFilter.Resolve(config), config);
            if (!string.IsNullOrWhiteSpace(config.Target))
            {
                if (!TryResolveTargetSelector(config.Target.Trim(), out var selector, out var targetError))
                {
                    Console.Error.WriteLine(
                        "[RvtMcp] Invalid --target value '" + config.Target + "'. " + targetError);
                    Environment.Exit(1);
                    return;
                }
                RevitTargetBinding.Initialize(selector);
            }

            // Warm up the target-binding code paths on a background thread so the
            // first tool call's connect+handshake isn't penalized by one-time JIT.
            // The scan may delete proven-dead descriptor files (same as a list call)
            // but must not bind, connect, or change binding state.
            new Thread(WarmUpTargetBinding) { IsBackground = true, Name = "RvtMcp.TargetWarmup" }.Start();

            var bakePaths = new BakePaths();
            TryInitializeBakeStorage(bakePaths, out _);

            // Initialize memory system (shared across tool classes + resources)
            var session = new Memory.SessionContext(config.EnableCallLogOrDefault);
            ToolGateway.Session = session;
            ToolGateway.History = new Memory.ChangeHistoryStore();
            ToolGateway.UsageLogger = new UsageEventLogger(bakePaths, config);
            RevitResources.Session = session;

            int httpIndex = Array.IndexOf(args, "--http");
            if (httpIndex >= 0)
            {
                if (httpIndex + 1 >= args.Length || !int.TryParse(args[httpIndex + 1], out var port)
                    || port < 1 || port > 65535)
                {
                    Console.Error.WriteLine("[RvtMcp] Invalid --http argument. Expected: --http <port> (1-65535)");
                    Environment.Exit(1);
                    return;
                }
                await RunHttpSse(config, port);
            }
            else
            {
                await RunStdio(config);
            }
        }

        /// <summary>JIT warm-up: exercise selector parse, descriptor scan/probe,
        /// binding plan and the §6.10 envelope paths once, off the tool-call path.</summary>
        private static void WarmUpTargetBinding()
        {
            try
            {
                SelectorParser.TryParse(HostProduct.Revit, "auto", out _, out _, out _);
                var scan = RevitTargetBinding.Scan();
                var binding = RevitTargetBinding.Binding;
                binding.CaptureGeneration();
                binding.PlanCall(0, scan.Live);
                MetaTools.TargetEnvelope(new Bimwright.Targeting.TargetPayload
                {
                    Code = Bimwright.Targeting.TargetCode.NoTarget,
                    Message = "warmup",
                    NextStep = "warmup",
                    Candidates = scan.Live.Count == 0
                        ? new List<Bimwright.Targeting.CandidateInfo>
                            { new Bimwright.Targeting.CandidateInfo("warmup", 2024, 0, false, null) }
                        : Array.Empty<Bimwright.Targeting.CandidateInfo>()
                });
                ToolGateway.WarmUpWire();
            }
            catch { /* warm-up is best-effort */ }
        }

        internal static LegacyBakedToolImportResult InitializeBakeStorage(BakePaths paths)
        {
            if (paths == null)
                throw new ArgumentNullException(nameof(paths));

            using var db = new BakeDb(paths);
            db.Migrate();
            var importer = new LegacyBakedToolImporter(paths, db, new ToolBakerAuditLog(paths.AuditJsonl));
            return importer.ImportIfNeeded();
        }

        internal static bool TryInitializeBakeStorage(BakePaths paths, out LegacyBakedToolImportResult result)
        {
            try
            {
                result = InitializeBakeStorage(paths);
                return true;
            }
            catch (Exception ex)
            {
                result = null;
                var pathHint = paths?.Root ?? "(unknown path)";
                Console.Error.WriteLine(
                    $"[RvtMcp] Warning: ToolBaker bake storage initialization failed for {pathHint}. " +
                    "The MCP server will continue; baked-tool migration/import can be retried on next startup. " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static async Task RunStdio(RvtMcpConfig config)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = Host.CreateApplicationBuilder();
            RegisterHistoryRecovery(builder.Services, config);
            // stdio MCP stdout must contain JSON-RPC only. ClearProviders() removes default
            // host logging, but the MCP SDK (and any transitive package) may re-add a Console
            // provider that writes to stdout. AddConsole with LogToStandardErrorThreshold=Trace
            // forces every console log line — from any provider re-added downstream — to stderr.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithStdioServerTransport();
            mcp = RuntimeToolFilter.Register(mcp, config, ToolGateway.Session);
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();
            mcp.WithPrompts<RevitPrompts>();
            var app = builder.Build();
            await app.RunAsync();
        }

        private static async Task RunHttpSse(RvtMcpConfig config, int port)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = WebApplication.CreateBuilder();
            RegisterHistoryRecovery(builder.Services, config);
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithHttpTransport();
            mcp = RuntimeToolFilter.Register(mcp, config, ToolGateway.Session);
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();
            mcp.WithPrompts<RevitPrompts>();

            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                var host = context.Request.Host.Host;
                if (host != "127.0.0.1" && host != "localhost")
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Forbidden: non-localhost host");
                    return;
                }
                await next();
            });

            app.MapMcp();

            Console.Error.WriteLine($"[RvtMcp] SSE server listening on http://127.0.0.1:{port}");
            Console.Error.WriteLine($"[RvtMcp] Toolsets enabled: {string.Join(",", enabled.OrderBy(n => n))}");
            await app.RunAsync();
        }

        private static void RegisterHistoryRecovery(IServiceCollection services, RvtMcpConfig config)
        {
            if (config.EnableChangeHistoryOrDefault && !config.ReadOnlyOrDefault)
                services.AddHostedService(_ => new Memory.HistoryRecoveryService(ToolGateway.History));
        }

        static int RunDoctor()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var serverVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "";
            var checks = DoctorReport.Run(appData, localAppData, serverVersion);
            var report = new Newtonsoft.Json.Linq.JObject
            {
                ["ok"] = checks.All(c => c.Ok),
                ["checks"] = new Newtonsoft.Json.Linq.JArray(checks.Select(c => new Newtonsoft.Json.Linq.JObject
                {
                    ["name"] = c.Name,
                    ["ok"] = c.Ok,
                    ["detail"] = c.Detail
                }).ToArray())
            };
            Console.WriteLine(report.ToString(Newtonsoft.Json.Formatting.None));
            return report.Value<bool>("ok") ? 0 : 1;
        }

        private static void PrintHelp()
        {
            var usage = string.Join("\n", new[]
            {
                "rvt-mcp — Revit MCP server (bimwright.dev)",
                "",
                "Usage: rvt-mcp [options]",
                "",
                "Transport:",
                "  --http <port>           Run HTTP SSE on 127.0.0.1:<port> (1-65535). Default = stdio.",
                "  --doctor                Check manifests, assemblies, coexistence, config and data dir without Revit; exit 0/1.",
                "",
                "Routing:",
                "  --target <selector>     Pin the initial target: auto | a 4-digit calendar",
                "                          year (2022..2027) | pid:<n> | id:revit-<year>-<pid>.",
                "                          Legacy R-codes (R22..R27) are rejected in v0.5+.",
                "                          Default: auto — ordered scan of revit-*.json files in",
                "                          %LOCALAPPDATA%\\RvtVerify\\.",
                "",
                "Tool exposure (A3 Progressive Disclosure):",
                "  --toolsets <csv>        Comma list of toolsets to enable. Default: " + string.Join(",", ToolsetFilter.DefaultOn) + ".",
                "                          Known toolsets: " + string.Join(", ", ToolsetFilter.KnownToolsets),
                "                          Use 'all' to expose every toolset.",
                "  --read-only             Expose only tools annotated ReadOnly=true (no document/file writes).",
                "  --enable-send-code      Expose arbitrary C# execution (default ON, independent of ToolBaker).",
                "  --disable-send-code     Hide send_code_to_revit. --read-only also hides it.",
                "  --enable-call-log       Persist tool-call params/results in server and plugin logs (default OFF).",
                "  --disable-call-log      Suppress call logs, including the send_code body journal.",
                "  --enable-change-history / --disable-change-history (default ON)",
                "                          Local per-model change/reason history, independent of call logs.",
                "",
                "Response limits (UTF-8 bytes, integers >=1024; warn <= strong <= budget <= max):",
                "  --enable-response-guard / --disable-response-guard (default ON)",
                "  --response-warn-bytes <n>         Default 65536.",
                "  --response-strong-warn-bytes <n>  Default 262144.",
                "  --response-budget-bytes <n>       Default 716800 (pretty-print headroom).",
                "  --max-response-bytes <n>          Default 1048576; remains active with guard OFF.",
                "  --spill-retention-hours <n>       Hours a spill file is kept before cleanup (1-8760).",
                "                                    Default 36; a missing or invalid value uses 36.",
                "",
                "ToolBaker:",
                "  --enable-toolbaker      Allow ToolBaker tools (default ON).",
                "  --disable-toolbaker     Disable ToolBaker tools.",
                "  --enable-adaptive-bake  Enable adaptive ToolBaker suggestions (default OFF).",
                "  --disable-adaptive-bake Disable adaptive ToolBaker suggestions.",
                "  --cache-send-code-bodies",
                "                          Cache send_code_to_revit code bodies locally (default OFF).",
                "  --no-cache-send-code-bodies",
                "                          Disable local send_code_to_revit code body caching.",
                "  --persist-send-code-bodies",
                "                          Opt-in TTL journal for send_code bodies (default OFF; needs call-log ON).",
                "                          Stamps %LOCALAPPDATA%\\RvtVerify\\rvtmcp.config.json so the",
                "                          Revit plugin (sole journal writer) can see the window.",
                "  --persist-send-code-bodies-for <ttl>",
                "                          Duration for the journal window (1h–2d; e.g. 4h, 2d, 90m).",
                "                          Out-of-range values are clamped. Default 4h.",
                "  --no-persist-send-code-bodies",
                "                          Disable send_code body journal and clear the JSON stamp.",
                "",
                "Transport security (S7):",
                "  --allow-lan-bind        (plugin-side only — set BIMWRIGHT_ALLOW_LAN_BIND env var in",
                "                          the Revit process environment; server-side flag is documented",
                "                          here for future cross-process propagation.)",
                "",
                "Env vars (override JSON, overridden by CLI):",
                "  BIMWRIGHT_TARGET, BIMWRIGHT_TOOLSETS, BIMWRIGHT_READ_ONLY,",
                "  BIMWRIGHT_ALLOW_LAN_BIND, BIMWRIGHT_ENABLE_TOOLBAKER,",
                "  BIMWRIGHT_ENABLE_SEND_CODE, BIMWRIGHT_ENABLE_CALL_LOG, BIMWRIGHT_ENABLE_RESPONSE_GUARD,",
                "  BIMWRIGHT_RESPONSE_WARN_BYTES, BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES,",
                "  BIMWRIGHT_RESPONSE_BUDGET_BYTES, BIMWRIGHT_MAX_RESPONSE_BYTES,",
                "  BIMWRIGHT_SPILL_RETENTION_HOURS,",
                "  BIMWRIGHT_ENABLE_ADAPTIVE_BAKE, BIMWRIGHT_CACHE_SEND_CODE_BODIES,",
                "  BIMWRIGHT_PERSIST_SEND_CODE_BODIES, BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL",
                "  (Persist env stamps JSON on first enable; after TTL expiry sticky env=1 does",
                "   NOT revive — use --persist-send-code-bodies to re-enable explicitly.)",
                "",
                "Config file (lowest precedence):",
                "  %LOCALAPPDATA%\\RvtVerify\\rvtmcp.config.json",
                "",
                "Other:",
                "  -h, --help              Show this help and exit.",
            });
            Console.WriteLine(usage);
        }

        // MCP InitializeResult metadata. ServerInstructions is the single most important
        // signal for Tool Search discoverability — Claude Code/Desktop's Tool Search ranks
        // servers by (1) instructions, (2) literal tool name. Without this populated, an
        // agent asking "list Revit tools" returns nothing even though 224 tools are exposed.
        // Anthropic truncates this field at 2KB; the keyword-dense first paragraph carries
        // the discoverability load if the SDK or proxy truncates later.
        // InformationalVersion carries "+githash"; report clean semver to MCP clients.
        internal static readonly string ServerVersion =
            (Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0").Split('+')[0];

        private static void ConfigureMcpServerOptions(ModelContextProtocol.Server.McpServerOptions opts)
        {
            opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "rvt-verify",
                Title = "RvtVerify",
                Version = ServerVersion,
                Description = "Self-verifying Model Context Protocol gateway for Autodesk Revit 2022-2027",
                WebsiteUrl = "https://github.com/Daii1414/revit-verify"
            };
            // A newer release known from the add-in's cached daily check (no network here).
            opts.ServerInstructions = RvtMcp.Plugin.Update.UpdateNoticeText.ComposeInstructions(
                ServerInstructionsText, RvtMcp.Plugin.Update.UpdateNoticeText.ForAgent(UpdateStatus.Current()));
        }

        // Anthropic Tool Search truncates this at 2 KB; the constant below is kept
        // under 2048 UTF-8 bytes. Lead with the keyword paragraph (highest discriminative
        // signal for queries like "list Revit tools"), then a compact toolset-name index
        // — 2 examples per toolset — so semantic search for individual ops still resolves.
        private const string ServerInstructionsText =
@"RvtVerify — MCP gateway for Autodesk Revit 2022-2027. Use whenever user works with .rvt, Revit, BIM, walls, doors, windows, floors, ceilings, roofs, levels, grids, rooms, sheets, schedules, families, views, view templates, view filters, MEP (ducts, pipes, trays, conduits, HVAC, lighting, plumbing), structural (columns, beams, foundations, rebar), dimensions, tags, annotations, keynotes, worksets, phases, links, parameters, materials, IFC, DWG, NWC, PDF.

Multi-Revit: list via revit_list_available_targets (a year can repeat); pin with revit_switch_target(""pid:<n>""); never guess. Defaults: query,create,view,meta. --toolsets all for export/clash. Do not retry clash/export after 60s timeout.

Before model changes, use the revit_change prompt: survey, agree scope, record why.

Tools (prefix revit_<verb>_<noun>, lengths in mm):
- query: get_current_view_info, ai_element_filter, get_element_details
- create: create_grid, create_room
- modify: operate_element, set_element_parameter_values
- delete: delete_element
- view: create_view, capture_view_image
- sheets: create_sheet, renumber_sheets, align_viewports, viewport_geometry
- schedule: create_schedule, list_schedules
- families: list_loaded_families, load_family_from_path
- mep: create_duct, analyze_mep_network
- annotation: tag_elements, create_dimensions
- graphics: create_view_filter, override_element_graphics
- export: export_pdf, export_dwg, export_ifc, export_nwc
- materials: list_materials, assign_material_to_element
- geometry: clash_detection, measure_distance_between_elements
- rooms: list_rooms, compute_room_finishes
- links: project coordinates, link/acquire/publish
- parameters: create_shared_parameter, create_project_parameter
- organization: apply_view_template, save_selection
- workflows: workflow_clash_review, workflow_model_audit
- structural: create_structural_column, create_rebar_set
- meta: open_model, send_code_to_revit, batch_execute
- lint: find_untagged_elements, get_model_warnings_summary
- toolbaker: list_baked_tools, run_baked_tool";

        /// <summary>Parse a --target/BIMWRIGHT_TARGET/config selector (§6.1); R-codes get the educational error.</summary>
        internal static bool TryResolveTargetSelector(string raw, out TargetSelector selector, out string error)
        {
            if (SelectorParser.TryParse(HostProduct.Revit, raw, out selector, out var selError, out var selMessage))
            {
                error = null;
                return true;
            }
            error = "Expected: auto | a 4-digit Revit calendar year 2022..2027 | pid:<n> | id:revit-<year>-<pid>. "
                + (selError == SelectorError.RCode
                    ? "Note: legacy R-codes (R22..R27) are no longer accepted in v0.5; use the year directly."
                    : selMessage ?? "Unrecognized selector.");
            return false;
        }

        internal static bool IncludeSendCode(HashSet<string> enabled, RvtMcpConfig config)
        {
            var sendCodeOn = config == null || config.EnableSendCodeOrDefault;
            return sendCodeOn
                && (enabled.Contains("meta") || enabled.Contains("toolbaker"));
        }

        internal static IEnumerable<MethodInfo> ResolveRegisteredToolMethods(HashSet<string> enabled, RvtMcpConfig config)
        {
            return ResolveRegisteredToolTypes(enabled, config)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Where(method => config?.ReadOnlyOrDefault != true
                    || method.GetCustomAttribute<McpServerToolAttribute>().ReadOnly
                    // revit_switch_target is annotated ReadOnly=false (it mutates session
                    // routing) but must stay exposed in read-only sessions so the agent
                    // can always recover the target binding (spec §6.8/§6.11).
                    || method.GetCustomAttribute<McpServerToolAttribute>().Name == "revit_switch_target");
        }

        internal static IMcpServerBuilder RegisterToolsets(IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config)
        {
            return mcp.WithTools(ResolveRegisteredToolMethods(enabled, config)
                .Select(method => McpServerTool.Create(method, (object)null)));
        }

        internal static Type[] ResolveRegisteredToolTypes(HashSet<string> enabled, RvtMcpConfig config)
        {
            var types = new List<Type>();
            if (enabled.Contains("query"))      types.Add(typeof(QueryTools));
            if (enabled.Contains("create"))     types.Add(typeof(CreateTools));
            if (enabled.Contains("modify"))     types.Add(typeof(ModifyTools));
            if (enabled.Contains("delete"))     types.Add(typeof(DeleteTools));
            if (enabled.Contains("view"))       types.Add(typeof(ViewTools));
            if (enabled.Contains("schedule"))   types.Add(typeof(ScheduleTools));
            if (enabled.Contains("families"))   types.Add(typeof(FamiliesTools));
            if (enabled.Contains("graphics"))   types.Add(typeof(GraphicsTools));
            if (enabled.Contains("export"))     types.Add(typeof(ExportTools));
            if (enabled.Contains("annotation")) types.Add(typeof(AnnotationTools));
            if (enabled.Contains("mep"))        types.Add(typeof(MepTools));
            if (enabled.Contains("sheets"))      types.Add(typeof(SheetsTools));
            if (enabled.Contains("materials"))   types.Add(typeof(MaterialsTools));
            if (enabled.Contains("geometry"))    types.Add(typeof(GeometryTools));
            if (enabled.Contains("rooms"))       types.Add(typeof(RoomsTools));
            if (enabled.Contains("links"))       types.Add(typeof(LinksTools));
            if (enabled.Contains("parameters"))   types.Add(typeof(ParametersTools));
            if (enabled.Contains("organization")) types.Add(typeof(OrganizationTools));
            if (enabled.Contains("workflows"))    types.Add(typeof(WorkflowsTools));
            if (enabled.Contains("toolbaker"))  types.Add(typeof(ToolbakerTools));
            if (enabled.Contains("toolbaker") && config?.EnableAdaptiveBakeOrDefault == true)
                types.Add(typeof(AdaptiveBakeTools));
            if (IncludeSendCode(enabled, config)) types.Add(typeof(SendCodeTools));
            if (enabled.Contains("meta"))       types.Add(typeof(MetaTools));
            if (enabled.Contains("meta"))       types.Add(typeof(ChangeHistoryTools));
            if (enabled.Contains("lint"))       types.Add(typeof(LintTools));
            if (enabled.Contains("structural")) types.Add(typeof(StructuralTools));
            return types.ToArray();
        }
    }

}
