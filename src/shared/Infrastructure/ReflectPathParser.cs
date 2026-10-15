using System;

namespace RvtMcp.Plugin
{
    /// <summary>Strict path grammar for reflect_get (no Revit API): a single
    /// lowercase word, or param:&lt;Name&gt;. Nested paths are rejected.</summary>
    internal static class ReflectPathParser
    {
        public const int MaxPathLength = 256;
        public const int MaxParameterNameLength = 128;

        private static readonly string[] FixedWords =
        {
            "id", "name", "category", "type", "level", "host", "location", "box"
        };

        public sealed class ParsedPath
        {
            public string Kind { get; set; }
            public string ParameterName { get; set; }
        }

        public sealed class ParseException : Exception
        {
            public ParseException(string message) : base(message) { }
        }

        public static ParsedPath Parse(string path)
        {
            var text = (path ?? "").Trim();
            if (text.Length == 0)
                throw new ParseException("path is required (allowed: " + string.Join(", ", FixedWords) + ", param:<Name>).");
            if (text.Length > MaxPathLength)
                throw new ParseException("path exceeds " + MaxPathLength + " characters.");
            if (text.IndexOf('.') >= 0)
                throw new ParseException("nested paths are not supported; use one of: " + string.Join(", ", FixedWords) + ", param:<Name>.");
            if (text.StartsWith("param:", StringComparison.OrdinalIgnoreCase))
            {
                var name = text.Substring("param:".Length).Trim();
                if (name.Length == 0 || name.Length > MaxParameterNameLength)
                    throw new ParseException("param: name must contain 1.." + MaxParameterNameLength + " characters.");
                if (name.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
                    throw new ParseException("param: name must be a single line.");
                return new ParsedPath { Kind = "param", ParameterName = name };
            }
            var word = text.ToLowerInvariant();
            if (Array.IndexOf(FixedWords, word) < 0)
                throw new ParseException("unknown path '" + text + "' (allowed: " + string.Join(", ", FixedWords) + ", param:<Name>).");
            return new ParsedPath { Kind = word };
        }
    }
}
