using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Tool errors reach the agent as plain text. They must not carry absolute paths:
    /// the plugin side masks them, and the server side has to do the same.
    /// </summary>
    public class ToolErrorSanitizationTests
    {
        [Fact]
        public void Format_masks_windows_paths()
        {
            var formatted = Server.ToolErrors.Format(
                new System.IO.IOException("failed to read C:\\Users\\daii\\Documents\\model.rvt"));

            Assert.DoesNotContain("C:\\Users\\daii", formatted);
            Assert.Contains("model.rvt", formatted);
        }

        [Fact]
        public void Format_masks_unc_paths()
        {
            var formatted = Server.ToolErrors.Format(
                new System.IO.IOException("cannot open \\\\server\\share\\project.rvt"));

            Assert.DoesNotContain("\\\\server\\share", formatted);
            Assert.Contains("project.rvt", formatted);
        }

        [Fact]
        public void Format_keeps_the_message_readable()
        {
            var formatted = Server.ToolErrors.Format(new System.InvalidOperationException("no open document"));

            Assert.Contains("no open document", formatted);
            Assert.StartsWith("Error: ", formatted);
        }

        [Fact]
        public void Format_with_context_names_the_failing_step()
        {
            var formatted = Server.ToolErrors.Format(new System.InvalidOperationException("boom"), "Export");

            Assert.Contains("Export", formatted);
            Assert.Contains("boom", formatted);
        }

        [Fact]
        public void Format_survives_a_null_exception()
        {
            Assert.Equal("Error: unknown failure.", Server.ToolErrors.Format(null));
        }
    }
}
