using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class JobTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtverify-jobs-" + Guid.NewGuid().ToString("N"));

        public JobTests()
        {
            Directory.CreateDirectory(_dir);
            RevitJobStore.RootOverride = _dir;
        }

        private static JObject OkResult()
        {
            return new JObject { ["ok"] = true, ["path"] = "C:\\out\\model.pdf" };
        }

        private static async Task<JObject> WaitForTerminal(string jobId, int tries = 200)
        {
            for (var i = 0; i < tries; i++)
            {
                var output = JObject.Parse(await MetaTools.Job("status", null, null, 600, jobId));
                var status = output.Value<string>("status");
                if (status == "complete" || status == "failed" || status == "canceled")
                    return output;
                await Task.Delay(50);
            }
            throw new TimeoutException("Job " + jobId + " did not finish in time.");
        }

        [Fact]
        public async Task Submit_runs_to_complete_with_preview()
        {
            string jobId = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) =>
                {
                    Assert.Equal("export_pdf", command);
                    Assert.Equal(600, timeout);
                    return Task.FromResult(OkResult());
                },
                async () =>
                {
                    var submitted = JObject.Parse(await MetaTools.Job("submit", "export_pdf", "{\"a\":1}"));
                    jobId = submitted.Value<string>("job_id");
                    Assert.False(string.IsNullOrEmpty(jobId));
                    var done = await WaitForTerminal(jobId);
                    Assert.Equal("complete", done.Value<string>("status"));
                    Assert.Contains("model.pdf", done.Value<string>("preview"));
                    Assert.True(done.Value<double>("elapsed_s") >= 0);
                });
            Assert.False(string.IsNullOrEmpty(jobId));
        }

        [Fact]
        public async Task Submit_rejects_unknown_command_bad_timeout_and_bad_json()
        {
            var calls = 0;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => { calls++; return Task.FromResult(new JObject()); },
                async () =>
                {
                    Assert.StartsWith("Error:", await MetaTools.Job("submit", "nope"));
                    Assert.StartsWith("Error:", await MetaTools.Job("submit", "export_pdf", null, 0));
                    Assert.StartsWith("Error:", await MetaTools.Job("submit", "export_pdf", null, 9999));
                    Assert.StartsWith("Error:", await MetaTools.Job("submit", "export_pdf", "not-json"));
                    Assert.StartsWith("Error:", await MetaTools.Job("bogus"));
                    Assert.StartsWith("Error:", await MetaTools.Job("status"));
                    Assert.StartsWith("Error:", await MetaTools.Job("cancel"));
                });
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task Cancel_before_dispatch_never_runs()
        {
            var gate = new TaskCompletionSource<JObject>();
            string jobId = null;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => gate.Task,
                async () =>
                {
                    jobId = JObject.Parse(await MetaTools.Job("submit", "clash_detection")).Value<string>("job_id");
                    var canceled = JObject.Parse(await MetaTools.Job("cancel", null, null, 600, jobId));
                    Assert.True(canceled.Value<bool>("canceled"));
                    gate.SetResult(OkResult());
                    var done = await WaitForTerminal(jobId);
                    Assert.Equal("canceled", done.Value<string>("status"));
                    Assert.Contains("detached", done.Value<string>("note"));
                    Assert.Contains("model.pdf", done.Value<string>("preview"));
                });
        }

        [Fact]
        public async Task Cancel_after_finish_reports_not_canceled()
        {
            await Capture.WithSendOverride(
                (command, parameters, timeout) => Task.FromResult(OkResult()),
                async () =>
                {
                    var jobId = JObject.Parse(await MetaTools.Job("submit", "export_ifc")).Value<string>("job_id");
                    await WaitForTerminal(jobId);
                    var canceled = JObject.Parse(await MetaTools.Job("cancel", null, null, 600, jobId));
                    Assert.False(canceled.Value<bool>("canceled"));

                    var unknown = JObject.Parse(await MetaTools.Job("status", null, null, 600, "nope"));
                    Assert.Equal("unknown", unknown.Value<string>("status"));
                    var unknownCancel = JObject.Parse(await MetaTools.Job("cancel", null, null, 600, "nope"));
                    Assert.False(unknownCancel.Value<bool>("canceled"));
                });
        }

        [Fact]
        public async Task List_shows_summaries_and_prunes_at_100()
        {
            await Capture.WithSendOverride(
                (command, parameters, timeout) => Task.FromResult(OkResult()),
                async () =>
                {
                    for (var i = 0; i < 103; i++)
                        await MetaTools.Job("submit", "export_dwg");
                    JObject listed = null;
                    for (var i = 0; i < 200; i++)
                    {
                        listed = JObject.Parse(await MetaTools.Job("list", null, null, 600, null, 100));
                        if (!listed["jobs"].Any(j => j.Value<string>("status") == "running"
                            || j.Value<string>("status") == "queued"))
                            break;
                        await Task.Delay(50);
                    }
                    Assert.True(listed.Value<int>("count") <= 100);
                    Assert.All(listed["jobs"], j => Assert.Null(j["preview"]));
                });
        }

        [Fact]
        public async Task Preview_truncates_huge_payloads()
        {
            await Capture.WithSendOverride(
                (command, parameters, timeout) => Task.FromResult(new JObject { ["blob"] = new string('x', 9000) }),
                async () =>
                {
                    var jobId = JObject.Parse(await MetaTools.Job("submit", "export_nwc")).Value<string>("job_id");
                    var done = await WaitForTerminal(jobId);
                    Assert.Contains("[truncated", done.Value<string>("preview"));
                });
        }

        [Fact]
        public async Task Blocked_in_read_only_mode()
        {
            var original = ServerState.Config;
            ServerState.Config = new RvtMcpConfig { ReadOnly = true };
            try
            {
                Assert.Contains("read_only_mode", await MetaTools.Job("list"));
            }
            finally
            {
                ServerState.Config = original;
            }
        }

        public void Dispose()
        {
            RevitJobStore.RootOverride = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }
    }
}
