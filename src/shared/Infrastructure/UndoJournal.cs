using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    // Design adapted from Horizun Revit MCP undo journal (Apache-2.0):
    // per-batch inverses with save-stamp + drift guards. v1 scope: op
    // "created" only (covers agent building); moves/parameter writes are
    // recorded as not-undoable rather than guessed.
    public sealed class UndoEntry
    {
        public string Op { get; set; } = "created";
        public List<long> ElementIds { get; set; } = new List<long>();
    }

    public sealed class UndoBatch
    {
        public string Id { get; set; }
        public string CreatedUtc { get; set; }
        public string Tool { get; set; }
        public string SaveStamp { get; set; }
        public List<UndoEntry> Entries { get; set; } = new List<UndoEntry>();
        public bool Undone { get; set; }
    }

    public static class UndoJournal
    {
        internal static string StoreRootOverride { get; set; }

        public static string StoreDir()
        {
            var dir = StoreRootOverride
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RvtVerify", "undo");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string PathFor(string documentTitle)
        {
            var safe = string.Concat((documentTitle ?? "unsaved").Select(
                c => char.IsLetterOrDigit(c) ? c : '_'));
            if (safe.Length > 80) safe = safe.Substring(0, 80);
            return Path.Combine(StoreDir(), safe + ".json");
        }

        public static List<UndoBatch> Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<UndoBatch>();
                var arr = JToken.Parse(File.ReadAllText(path, Encoding.UTF8)) as JArray;
                return arr?.ToObject<List<UndoBatch>>() ?? new List<UndoBatch>();
            }
            catch { return new List<UndoBatch>(); }
        }

        public static void Save(string path, List<UndoBatch> batches)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JArray.FromObject(batches).ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8);
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }

        public static UndoBatch Record(string documentTitle, string saveStamp, string tool, List<long> createdIds)
        {
            var batch = new UndoBatch
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                Tool = tool,
                SaveStamp = saveStamp,
                Entries = new List<UndoEntry> { new UndoEntry { Op = "created", ElementIds = createdIds } }
            };
            try
            {
                var path = PathFor(documentTitle);
                var all = Load(path);
                all.Add(batch);
                Save(path, all);
            }
            catch { }
            return batch;
        }

        /// <summary>Extract created element ids from a handler result payload
        /// by convention (elementId / elementIds / added.ids / ids).</summary>
        public static List<long> ExtractCreatedIds(object data)
        {
            var ids = new List<long>();
            try
            {
                var obj = data as JObject ?? (data == null ? null : JObject.FromObject(data));
                if (obj == null) return ids;
                var token = obj["elementId"] ?? obj["elementIds"] ?? obj["ids"];
                if (token == null && obj["added"] is JObject added) token = added["ids"];
                if (token is JValue v && v.Value<long?>() is long single)
                    ids.Add(single);
                else if (token is JArray arr)
                    foreach (var t in arr)
                        if (t.Value<long?>() is long id) ids.Add(id);
            }
            catch { }
            return ids.Distinct().ToList();
        }
    }
}
