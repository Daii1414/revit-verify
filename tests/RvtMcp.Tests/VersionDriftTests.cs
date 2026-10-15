using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>Product version must not drift between manifests: server.json and
    /// all shipping project files declare the same triplet. (Git tags are release
    /// operations and are intentionally not asserted here.)</summary>
    public sealed class VersionDriftTests
    {
        [Fact]
        public void All_manifests_declare_the_same_version()
        {
            var root = RepoRoot();
            var serverJson = JObject.Parse(File.ReadAllText(Path.Combine(root, "server.json")));
            var expected = serverJson.Value<string>("version");
            Assert.Matches(@"^\d+\.\d+\.\d+$", expected);

            foreach (var project in new[]
            {
                Path.Combine(root, "src", "server", "RvtMcp.Server.csproj"),
                Path.Combine(root, "src", "plugin-r25", "RvtMcp.Plugin.R25.csproj"),
                Path.Combine(root, "src", "plugin-r26", "RvtMcp.Plugin.R26.csproj"),
            })
            {
                var text = File.ReadAllText(project);
                var match = Regex.Match(text, @"<Version>([^<]+)</Version>");
                Assert.True(match.Success, project + " declares no <Version>.");
                Assert.Equal(expected, match.Groups[1].Value.Trim());
            }
        }

        private static string RepoRoot([CallerFilePath] string testFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
    }
}
