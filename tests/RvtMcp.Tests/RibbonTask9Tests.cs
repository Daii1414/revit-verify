using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class RibbonTask9Tests
    {
        [Fact]
        public void Ribbon_exposes_commands_instead_of_language_combo()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", "shared", "RibbonSetup.cs"));
            Assert.Contains("SettingsButton", source);
            Assert.Contains("LanguageButton", source);
            Assert.Contains("ShowSettingsCommand", source);
            Assert.Contains("ShowSettingsLanguageCommand", source);
            Assert.DoesNotContain("LanguageCombo", source);
            Assert.DoesNotContain("ComboBoxData", source);
            Assert.DoesNotContain("AddLanguageCombo", source);
            // The Language button caption is invariant ("Language") by contract — no L.T key.
            Assert.DoesNotContain("ribbon.language.text", source);
        }

        [Fact]
        public void Idling_refreshes_settings_and_language_button_chrome()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", "shared", "Infrastructure", "IdlingUpdater.cs"));
            Assert.Contains("_ribbon.SettingsButton.ItemText", source);
            Assert.Contains("_ribbon.LanguageButton.ItemText", source);
            Assert.DoesNotContain("LanguageCombo", source);
            Assert.DoesNotContain("ribbon.language.text", source);
        }

        [Theory]
        [InlineData("plugin-r25")]
        [InlineData("plugin-r26")]
        public void Every_plugin_shell_has_settings_and_language_icon_members(string pluginFolder)
        {
            // The icon generator lives in src/shared and is compiled into every shell
            // through the shared glob, so one copy serves both plugin years.
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", "shared", "IconGenerator.cs"));
            Assert.Contains("Settings32", source);
            Assert.Contains("Settings16", source);
            Assert.Contains("Language32", source);
            Assert.Contains("Language16", source);
            Assert.Contains("CreateSettingsIcon", source);
            Assert.Contains("CreateLanguageIcon", source);

            // The shell must actually pull the shared sources in, otherwise the icons
            // would be missing from the built plugin.
            var project = File.ReadAllText(
                Path.Combine(GetRepoRoot(), "src", pluginFolder, pluginFolder.Replace("plugin-", "RvtMcp.Plugin.") + ".csproj"));
            Assert.Contains("..\\shared\\**\\*.cs", project);
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
