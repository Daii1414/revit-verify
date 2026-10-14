using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class MemoryNotesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtverify-memory-" + Guid.NewGuid().ToString("N"));

        public MemoryNotesTests()
        {
            Directory.CreateDirectory(_dir);
            MetaTools.MemoryRootOverride = _dir;
        }

        [Fact]
        public async Task Save_then_read_roundtrip()
        {
            var saved = await MetaTools.CreateMemoryNote("walls 200mm", "church");
            Assert.Contains("\"saved\": true", saved);

            var read = await MetaTools.ReadMemoryNotes();
            var json = JObject.Parse(read);
            Assert.Equal(1, json.Value<int>("count"));
            Assert.Equal("walls 200mm", json["notes"][0].Value<string>("text"));
            Assert.Equal("church", json["notes"][0].Value<string>("model"));
            Assert.NotEmpty(json["notes"][0].Value<string>("id"));
        }

        [Fact]
        public async Task Read_filters_by_model()
        {
            await MetaTools.CreateMemoryNote("global note", null);
            await MetaTools.CreateMemoryNote("church note", "church");

            var read = await MetaTools.ReadMemoryNotes("church");
            Assert.Equal(1, JObject.Parse(read).Value<int>("count"));
        }

        [Fact]
        public async Task Save_rejects_empty_and_oversize()
        {
            Assert.Contains("Error", await MetaTools.CreateMemoryNote("  "));
            Assert.Contains("Error", await MetaTools.CreateMemoryNote(new string('x', 2001)));
        }

        public void Dispose()
        {
            MetaTools.MemoryRootOverride = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }
    }
}
