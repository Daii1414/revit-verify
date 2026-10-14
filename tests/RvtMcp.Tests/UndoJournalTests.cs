using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class UndoJournalTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtverify-undo-" + Guid.NewGuid().ToString("N"));

        public UndoJournalTests()
        {
            Directory.CreateDirectory(_dir);
            UndoJournal.StoreRootOverride = _dir;
        }

        [Fact]
        public void ExtractCreatedIds_reads_conventions()
        {
            Assert.Equal(new long[] { 7 },
                UndoJournal.ExtractCreatedIds(new JObject { ["elementId"] = 7 }));
            Assert.Equal(new long[] { 1, 2 },
                UndoJournal.ExtractCreatedIds(new JObject { ["elementIds"] = new JArray(1, 2) }));
            Assert.Equal(new long[] { 3 },
                UndoJournal.ExtractCreatedIds(new JObject { ["added"] = new JObject { ["ids"] = new JArray(3) } }));
            Assert.Empty(UndoJournal.ExtractCreatedIds(new JObject { ["count"] = 5 }));
            Assert.Empty(UndoJournal.ExtractCreatedIds(null));
        }

        [Fact]
        public void Record_roundtrip_marks_undone()
        {
            var batch = UndoJournal.Record("My Model.rvt", "stamp#1", "batch_execute", new System.Collections.Generic.List<long> { 10, 20 });
            Assert.False(string.IsNullOrWhiteSpace(batch.Id));

            var path = UndoJournal.PathFor("My Model.rvt");
            var loaded = UndoJournal.Load(path);
            Assert.Single(loaded);
            Assert.Equal(2, loaded[0].Entries[0].ElementIds.Count);

            loaded[0].Undone = true;
            UndoJournal.Save(path, loaded);
            Assert.True(UndoJournal.Load(path)[0].Undone);
        }

        [Fact]
        public void Unknown_paths_load_empty()
        {
            Assert.Empty(UndoJournal.Load(Path.Combine(_dir, "nope.json")));
        }

        public void Dispose()
        {
            UndoJournal.StoreRootOverride = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }
    }
}
