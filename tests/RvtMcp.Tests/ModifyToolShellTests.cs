using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class ModifyToolShellTests
    {
        private static readonly string[] ModifyToolsInClass =
        {
            "revit_assign_elements_to_workset",
            "revit_change_element_type",
            "revit_color_elements",
            "revit_operate_element",
            "revit_set_element_parameter_values",
            "revit_set_type_parameter_values",
            "revit_transform_elements"
        };

        [Fact]
        public void Every_modify_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(ModifyTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(ModifyToolsInClass, declared);
        }

        [Fact]
        public async Task Transform_move_sends_vector()
        {
            var sent = await Capture.Send(() => ModifyTools.TransformElements(
                "move", "[1,2]", "{\"x_mm\":100,\"y_mm\":0,\"z_mm\":0}"));

            Assert.Equal("transform_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal("move", json.Value<string>("op"));
            Assert.Equal(2, json["element_ids"].Count());
            Assert.Equal(100, json["vector_mm"]["x_mm"].ToObject<double>());
        }

        [Fact]
        public async Task Transform_mirror_sends_axis_and_copy_flag()
        {
            var sent = await Capture.Send(() => ModifyTools.TransformElements(
                "mirror", "[7]", null, null, 0,
                "{\"a\":{\"x_mm\":0,\"y_mm\":0,\"z_mm\":0},\"b\":{\"x_mm\":5000,\"y_mm\":0,\"z_mm\":0}}",
                true));

            Assert.Equal("transform_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal("mirror", json.Value<string>("op"));
            Assert.Equal(5000, json["axis_mm"]["b"]["x_mm"].ToObject<double>());
            Assert.True(json.Value<bool>("mirror_copy"));
        }
    }
}
