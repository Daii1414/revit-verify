using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>Revit-free installation doctor: validates manifests, assemblies,
    /// coexistence with the release plugin, config JSON and data-dir writability
    /// from metadata only. Never contacts Revit, never migrates data.</summary>
    internal static class DoctorReport
    {
        public static readonly string[] SupportedYears = { "2025", "2026" };

        public sealed class Check
        {
            public string Name { get; set; }
            public bool Ok { get; set; }
            public string Detail { get; set; }
        }

        public static List<Check> Run(string appDataRoot, string localAppDataRoot, string serverVersion)
        {
            var checks = new List<Check>();
            var addInIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var year in SupportedYears)
            {
                var addinsDir = Path.Combine(appDataRoot, "Autodesk", "Revit", "Addins", year);
                var manifest = Path.Combine(addinsDir, "RvtVerify.R" + year.Substring(2) + ".addin");
                if (!File.Exists(manifest))
                {
                    checks.Add(new Check { Name = "manifest-" + year, Ok = false, Detail = "Missing: " + manifest });
                    continue;
                }
                string text;
                try { text = File.ReadAllText(manifest); }
                catch (Exception ex)
                {
                    checks.Add(new Check { Name = "manifest-" + year, Ok = false, Detail = "Unreadable: " + ex.Message });
                    continue;
                }
                var id = Regex.Match(text, @"<AddInId>\s*(\{?[0-9a-fA-F-]{32,38}\}?)\s*</AddInId>").Groups[1].Value;
                if (string.IsNullOrEmpty(id))
                {
                    checks.Add(new Check { Name = "manifest-" + year, Ok = false, Detail = "No <AddInId> in " + manifest });
                    continue;
                }
                addInIds[year] = id;
                checks.Add(new Check { Name = "manifest-" + year, Ok = true, Detail = "AddInId " + id });

                var dll = Path.Combine(addinsDir, "RvtVerify", "RvtVerify.Plugin.dll");
                if (!File.Exists(dll))
                {
                    checks.Add(new Check { Name = "plugin-dll-" + year, Ok = false, Detail = "Missing: " + dll });
                    continue;
                }
                string productVersion;
                try { productVersion = FileVersionInfo.GetVersionInfo(dll).ProductVersion; }
                catch (Exception ex)
                {
                    checks.Add(new Check { Name = "plugin-dll-" + year, Ok = false, Detail = "Unreadable version: " + ex.Message });
                    continue;
                }
                var match = string.Equals(NormalizeVersion(productVersion), NormalizeVersion(serverVersion), StringComparison.OrdinalIgnoreCase);
                checks.Add(new Check
                {
                    Name = "plugin-dll-" + year,
                    Ok = match,
                    Detail = match
                        ? "RvtVerify.Plugin.dll " + productVersion + " matches server " + serverVersion
                        : "RvtVerify.Plugin.dll " + productVersion + " differs from server " + serverVersion + "; install both from the same release"
                });
            }

            foreach (var year in SupportedYears)
            {
                var release = Path.Combine(appDataRoot, "Autodesk", "Revit", "Addins", year, "RvtMcp.R" + year.Substring(2) + ".addin");
                if (!File.Exists(release) || !addInIds.TryGetValue(year, out var ours)) continue;
                string text;
                try { text = File.ReadAllText(release); }
                catch (Exception ex)
                {
                    checks.Add(new Check { Name = "coexist-" + year, Ok = false, Detail = "Release manifest unreadable: " + ex.Message });
                    continue;
                }
                var theirs = Regex.Match(text, @"<AddInId>\s*(\{?[0-9a-fA-F-]{32,38}\}?)\s*</AddInId>").Groups[1].Value;
                var distinct = !string.IsNullOrEmpty(theirs) && !string.Equals(theirs, ours, StringComparison.OrdinalIgnoreCase);
                checks.Add(new Check
                {
                    Name = "coexist-" + year,
                    Ok = distinct,
                    Detail = distinct
                        ? "Release plugin present with a different AddInId; side-by-side load is safe"
                        : "Release manifest shares our AddInId; reinstall one of the plugins"
                });
            }

            var dataDir = Path.Combine(localAppDataRoot, "RvtVerify");
            try
            {
                Directory.CreateDirectory(dataDir);
                var probe = Path.Combine(dataDir, ".doctor-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "doctor");
                File.Delete(probe);
                checks.Add(new Check { Name = "data-dir", Ok = true, Detail = dataDir + " is writable" });
            }
            catch (Exception ex)
            {
                checks.Add(new Check { Name = "data-dir", Ok = false, Detail = dataDir + " is not writable: " + ex.Message });
            }

            var config = Path.Combine(localAppDataRoot, "RvtVerify", "rvtmcp.config.json");
            if (!File.Exists(config))
            {
                checks.Add(new Check { Name = "config", Ok = true, Detail = "Absent; server defaults apply" });
            }
            else
            {
                try
                {
                    JToken.Parse(File.ReadAllText(config));
                    checks.Add(new Check { Name = "config", Ok = true, Detail = config + " parses as JSON" });
                }
                catch (Exception ex)
                {
                    checks.Add(new Check { Name = "config", Ok = false, Detail = config + " is not valid JSON: " + ex.Message });
                }
            }

            return checks;
        }

        public static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            var match = Regex.Match(version.Trim(), @"^\d+(\.\d+){0,3}");
            if (!match.Success) return "";
            var parts = match.Value.Split('.');
            return string.Join(".", parts.Take(3));
        }
    }
}
