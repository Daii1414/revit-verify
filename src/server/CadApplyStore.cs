using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>Local provenance journal for revit_cad_apply runs: which plan,
    /// which candidates, which Revit element IDs. Read-only history; revisions
    /// are separate records sharing the same plan_sha256.</summary>
    internal static class CadApplyStore
    {
        public const int MaxRecords = 200;

        internal static string RootOverride { get; set; }

        private static string StorePath()
        {
            var dir = RootOverride
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RvtVerify", "cad");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "applies.json");
        }

        internal static JObject Record(string planSha256, string rulesVersion, string level,
            double heightMm, JArray mappings)
        {
            var record = new JObject
            {
                ["apply_id"] = Guid.NewGuid().ToString("N"),
                ["plan_sha256"] = planSha256,
                ["rules_version"] = rulesVersion,
                ["level"] = level,
                ["height_mm"] = heightMm,
                ["mappings"] = mappings,
                ["batch_undone"] = false,
                ["created_utc"] = DateTimeOffset.UtcNow.ToString("o"),
            };
            var path = StorePath();
            JArray all;
            try
            {
                all = File.Exists(path)
                    ? JToken.Parse(File.ReadAllText(path, Encoding.UTF8)) as JArray ?? new JArray()
                    : new JArray();
            }
            catch
            {
                all = new JArray();
            }
            all.Add(record);
            while (all.Count > MaxRecords) all.RemoveAt(0);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, all.ToString(Formatting.None), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return record;
        }
    }
}
