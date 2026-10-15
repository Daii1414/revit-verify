using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class QaTrendTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtverify-qa-" + Guid.NewGuid().ToString("N"));

        public QaTrendTests()
        {
            Directory.CreateDirectory(_dir);
            QueryTools.QaTrendRootOverride = _dir;
        }

        [Fact]
        public async Task Record_then_show_with_summary()
        {
            Assert.Contains("\"recorded\": true", await QueryTools.QaTrend("record", 70, "m1", 2, 3, "first"));
            Assert.Contains("\"recorded\": true", await QueryTools.QaTrend("record", 90, "m1", 3, 3, "second"));

            var json = JObject.Parse(await QueryTools.QaTrend("show"));
            Assert.Equal(2, json.Value<int>("count"));
            Assert.Equal(90, json["entries"][0].Value<int>("score"));
            Assert.Equal(70, json["entries"][1].Value<int>("score"));
            Assert.Equal(70, json["summary"].Value<int>("first_score"));
            Assert.Equal(90, json["summary"].Value<int>("last_score"));
            Assert.Equal(20, json["summary"].Value<int>("delta"));
            Assert.Equal(90, json["summary"].Value<int>("best"));
            Assert.Equal(70, json["summary"].Value<int>("worst"));
        }

        [Fact]
        public async Task Record_rejects_bad_input()
        {
            Assert.StartsWith("Error:", await QueryTools.QaTrend("record", null));
            Assert.StartsWith("Error:", await QueryTools.QaTrend("record", -1));
            Assert.StartsWith("Error:", await QueryTools.QaTrend("record", 101));
            Assert.StartsWith("Error:", await QueryTools.QaTrend("record", 50, null, 4, 3));
            Assert.StartsWith("Error:", await QueryTools.QaTrend("record", 50, null, null, null, new string('x', 501)));
            Assert.StartsWith("Error:", await QueryTools.QaTrend("bogus"));
            Assert.StartsWith("Error:", await QueryTools.QaTrend("show", null, null, null, null, null, 0));
        }

        [Fact]
        public async Task Show_filters_by_model_and_limit()
        {
            await QueryTools.QaTrend("record", 10, "a");
            await QueryTools.QaTrend("record", 20, "b");
            await QueryTools.QaTrend("record", 30, "a");

            var json = JObject.Parse(await QueryTools.QaTrend("show", null, "a"));
            Assert.Equal(2, json.Value<int>("count"));
            Assert.All(json["entries"], e => Assert.Equal("a", e.Value<string>("model_key")));

            json = JObject.Parse(await QueryTools.QaTrend("show", null, null, null, null, null, 1));
            Assert.Equal(1, json.Value<int>("count"));
            Assert.Equal(30, json["entries"][0].Value<int>("score"));

            json = JObject.Parse(await QueryTools.QaTrend("show", null, "zzz"));
            Assert.Equal(0, json.Value<int>("count"));
            Assert.True(json["summary"] == null || json["summary"].Type == JTokenType.Null);
        }

        [Fact]
        public async Task Store_prunes_beyond_200()
        {
            for (var i = 0; i < 205; i++)
                await QueryTools.QaTrend("record", i % 101, "m");

            var json = JObject.Parse(await QueryTools.QaTrend("show", null, null, null, null, null, 200));
            Assert.Equal(200, json.Value<int>("count"));
        }

        [Fact]
        public async Task Blocked_in_read_only_mode()
        {
            var original = ServerState.Config;
            ServerState.Config = new RvtMcpConfig { ReadOnly = true };
            try
            {
                var output = await QueryTools.QaTrend("show");
                Assert.Contains("read_only_mode", output);
            }
            finally
            {
                ServerState.Config = original;
            }
        }

        public void Dispose()
        {
            QueryTools.QaTrendRootOverride = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }
    }
}
