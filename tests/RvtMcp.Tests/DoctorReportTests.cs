using System;
using System.IO;
using System.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class DoctorReportTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvtverify-doctor-" + Guid.NewGuid().ToString("N"));
        private string AppData => Path.Combine(_root, "roaming");
        private string LocalAppData => Path.Combine(_root, "local");

        private string ServerVersion =>
            typeof(DoctorReportTests).Assembly.GetName().Version.ToString();

        private void WriteManifest(string year, string file, string addInId)
        {
            var dir = Path.Combine(AppData, "Autodesk", "Revit", "Addins", year);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><RevitAddIns><AddIn Type=\"Application\">" +
                "<Name>X</Name><Assembly>RvtVerify\\RvtVerify.Plugin.dll</Assembly>" +
                "<AddInId>" + addInId + "</AddInId></AddIn></RevitAddIns>");
        }

        private void WritePluginDll(string year, string sourceAssemblyPath)
        {
            var dir = Path.Combine(AppData, "Autodesk", "Revit", "Addins", year, "RvtVerify");
            Directory.CreateDirectory(dir);
            File.Copy(sourceAssemblyPath, Path.Combine(dir, "RvtVerify.Plugin.dll"), true);
        }

        private string SelfAssembly => typeof(DoctorReportTests).Assembly.Location;
        private string OtherAssembly => typeof(Newtonsoft.Json.JsonConvert).Assembly.Location;

        [Fact]
        public void Healthy_pair_matches_versions()
        {
            var id25 = Guid.NewGuid().ToString("B");
            var id26 = Guid.NewGuid().ToString("B");
            WriteManifest("2025", "RvtVerify.R25.addin", id25);
            WriteManifest("2026", "RvtVerify.R26.addin", id26);
            WritePluginDll("2025", SelfAssembly);
            WritePluginDll("2026", SelfAssembly);

            var checks = DoctorReport.Run(AppData, LocalAppData, ServerVersion);

            Assert.All(checks, c => Assert.True(c.Ok, c.Name + ": " + c.Detail));
            Assert.Contains(checks, c => c.Name == "manifest-2025");
            Assert.Contains(checks, c => c.Name == "plugin-dll-2026");
        }

        [Fact]
        public void Missing_manifest_fails_that_year_only()
        {
            WriteManifest("2025", "RvtVerify.R25.addin", Guid.NewGuid().ToString("B"));
            WritePluginDll("2025", SelfAssembly);

            var checks = DoctorReport.Run(AppData, LocalAppData, ServerVersion);

            Assert.False(checks.Single(c => c.Name == "manifest-2026").Ok);
            Assert.True(checks.Single(c => c.Name == "manifest-2025").Ok);
        }

        [Fact]
        public void Version_mismatch_fails_with_same_release_guidance()
        {
            WriteManifest("2025", "RvtVerify.R25.addin", Guid.NewGuid().ToString("B"));
            WritePluginDll("2025", OtherAssembly);

            var checks = DoctorReport.Run(AppData, LocalAppData, ServerVersion);

            var dll = checks.Single(c => c.Name == "plugin-dll-2025");
            Assert.False(dll.Ok);
            Assert.Contains("same release", dll.Detail);
        }

        [Fact]
        public void Shared_addin_id_with_release_plugin_fails_coexistence()
        {
            var shared = Guid.NewGuid().ToString("B");
            WriteManifest("2025", "RvtVerify.R25.addin", shared);
            WritePluginDll("2025", SelfAssembly);
            WriteManifest("2025", "RvtMcp.R25.addin", shared);

            var checks = DoctorReport.Run(AppData, LocalAppData, ServerVersion);

            Assert.False(checks.Single(c => c.Name == "coexist-2025").Ok);
        }

        [Fact]
        public void Distinct_release_addin_id_passes_coexistence()
        {
            WriteManifest("2025", "RvtVerify.R25.addin", Guid.NewGuid().ToString("B"));
            WritePluginDll("2025", SelfAssembly);
            WriteManifest("2025", "RvtMcp.R25.addin", Guid.NewGuid().ToString("B"));

            var checks = DoctorReport.Run(AppData, LocalAppData, ServerVersion);

            Assert.True(checks.Single(c => c.Name == "coexist-2025").Ok);
        }

        [Fact]
        public void Bad_config_json_fails_missing_config_passes()
        {
            var dir = Path.Combine(LocalAppData, "RvtVerify");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "rvtmcp.config.json"), "{oops");

            var checks = DoctorReport.Run(AppData, LocalAppData, ServerVersion);

            Assert.False(checks.Single(c => c.Name == "config").Ok);

            File.Delete(Path.Combine(dir, "rvtmcp.config.json"));
            checks = DoctorReport.Run(AppData, LocalAppData, ServerVersion);

            Assert.True(checks.Single(c => c.Name == "config").Ok);
            Assert.True(checks.Single(c => c.Name == "data-dir").Ok);
        }

        [Theory]
        [InlineData("1.2.3.4", "1.2.3")]
        [InlineData("0.1.0", "0.1.0")]
        [InlineData("0.1.0.0", "0.1.0")]
        [InlineData("0.1.0+22006a3f6cc630", "0.1.0")]
        [InlineData(null, "")]
        [InlineData("", "")]
        public void NormalizeVersion_compares_release_triplets(string input, string expected)
        {
            Assert.Equal(expected, DoctorReport.NormalizeVersion(input));
        }

        [Fact]
        public void PrintHelp_mentions_doctor()
        {
            var program = typeof(ToolGateway).Assembly.GetType("RvtMcp.Server.Program");
            Assert.NotNull(program);
            var method = program.GetMethod("PrintHelp",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try { method.Invoke(null, null); }
            finally { Console.SetOut(original); }
            Assert.Contains("--doctor", writer.ToString());
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }
    }
}
