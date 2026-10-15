using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ReflectPathParserTests
    {
        [Theory]
        [InlineData("id")]
        [InlineData("name")]
        [InlineData("category")]
        [InlineData("type")]
        [InlineData("level")]
        [InlineData("host")]
        [InlineData("location")]
        [InlineData("box")]
        public void Parses_fixed_words(string path)
        {
            var parsed = ReflectPathParser.Parse(path);

            Assert.Equal(path, parsed.Kind);
            Assert.Null(parsed.ParameterName);
        }

        [Fact]
        public void Parses_parameter_name()
        {
            var parsed = ReflectPathParser.Parse("param:Mark");

            Assert.Equal("param", parsed.Kind);
            Assert.Equal("Mark", parsed.ParameterName);
        }

        [Fact]
        public void Parses_case_insensitive_words()
        {
            Assert.Equal("name", ReflectPathParser.Parse("Name").Kind);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a.b")]
        [InlineData("bogus")]
        [InlineData("param:")]
        [InlineData("param:   ")]
        public void Rejects_malformed_paths(string path)
        {
            Assert.Throws<ReflectPathParser.ParseException>(
                () => ReflectPathParser.Parse(path));
        }

        [Fact]
        public void Rejects_overlong_path()
        {
            Assert.Throws<ReflectPathParser.ParseException>(
                () => ReflectPathParser.Parse(new string('x', 300)));
        }
    }
}
