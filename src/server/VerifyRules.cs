using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>Strict YAML-subset parser for the verify_run_rules schema (no external
    /// dependency). Supports block maps/lists (spaces only), comments, quoted scalars
    /// and flow lists of scalars. Anchors, tags, multi-line scalars, flow maps and tabs
    /// are rejected with a precise line error. Numbers become long/double, true/false
    /// become bool, ~/null/empty become null.</summary>
    internal static class VerifyRulesYamlParser
    {
        public const int MaxInputChars = 65536;
        public const int MaxRules = 10;
        public const int MaxAssertionsPerRule = 10;
        public const int MaxIdLength = 64;
        public const int MaxDescriptionLength = 1000;
        public const int MaxModelKeyLength = 256;

        private static readonly string[] TopKeys = { "version", "model_key", "rules" };
        private static readonly string[] RuleKeys = { "id", "description", "weight", "assertions" };
        private static readonly string[] AssertKinds = { "bbox", "count", "solid_volume", "edge_closure" };
        private static readonly Regex IdPattern = new Regex("^[a-z0-9_]{1,64}$", RegexOptions.Compiled);

        public sealed class RuleSet
        {
            public string ModelKey { get; set; }
            public List<Rule> Rules { get; } = new List<Rule>();
        }

        public sealed class Rule
        {
            public string Id { get; set; }
            public string Description { get; set; } = "";
            public int Weight { get; set; } = 1;
            public List<JObject> Assertions { get; } = new List<JObject>();
        }

        public sealed class ParseException : Exception
        {
            public int Line { get; }
            public ParseException(int line, string message) : base("line " + line + ": " + message)
            {
                Line = line;
            }
        }

        public static RuleSet Parse(string yaml)
        {
            if (yaml == null) throw new ParseException(1, "rules_yaml is required.");
            if (yaml.Length > MaxInputChars)
                throw new ParseException(1, "rules_yaml exceeds " + MaxInputChars + " characters.");
            var doc = new Reader(yaml).ReadDocument();
            if (!(doc is Dict top))
                throw new ParseException(1, "top level must be a mapping with 'version' and 'rules'.");
            foreach (var key in top.Keys)
                if (Array.IndexOf(TopKeys, key) < 0)
                    throw new ParseException(top.Lines[key], "unknown top-level key '" + key + "' (allowed: version, model_key, rules).");
            if (!top.TryGetValue("version", out var version) || !(version is Int v) || v.Value != 1)
                throw new ParseException(top.Lines.TryGetValue("version", out var vl) ? vl : 1, "version must be 1.");
            var rules = new RuleSet();
            if (top.TryGetValue("model_key", out var mk))
            {
                var key = AsString(mk, top.Lines["model_key"], "model_key");
                if (string.IsNullOrWhiteSpace(key) || key.Length > MaxModelKeyLength)
                    throw new ParseException(top.Lines["model_key"], "model_key must be 1.." + MaxModelKeyLength + " characters.");
                rules.ModelKey = key;
            }
            if (!top.TryGetValue("rules", out var rl) || !(rl is Seq list) || list.Items.Count == 0)
                throw new ParseException(top.Lines.TryGetValue("rules", out var rl2) ? rl2 : 1, "'rules' must be a non-empty list.");
            if (list.Items.Count > MaxRules)
                throw new ParseException(list.Line, "'rules' holds " + list.Items.Count + " entries; at most " + MaxRules + " are allowed.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.Items)
                rules.Rules.Add(ReadRule(item, seen));
            return rules;
        }

        private static Rule ReadRule(Node node, HashSet<string> seen)
        {
            if (!(node is Dict map))
                throw new ParseException(node.Line, "each rule must be a mapping with 'id' and 'assertions'.");
            foreach (var key in map.Keys)
                if (Array.IndexOf(RuleKeys, key) < 0)
                    throw new ParseException(map.Lines[key], "unknown rule key '" + key + "' (allowed: id, description, weight, assertions).");
            if (!map.TryGetValue("id", out var idNode))
                throw new ParseException(node.Line, "rule is missing required 'id'.");
            var id = AsString(idNode, map.Lines["id"], "id");
            if (!IdPattern.IsMatch(id ?? ""))
                throw new ParseException(map.Lines["id"], "id must match [a-z0-9_], at most " + MaxIdLength + " characters.");
            if (!seen.Add(id))
                throw new ParseException(map.Lines["id"], "duplicate rule id '" + id + "'.");
            var rule = new Rule { Id = id };
            if (map.TryGetValue("description", out var desc))
            {
                var text = AsString(desc, map.Lines["description"], "description") ?? "";
                if (text.Length > MaxDescriptionLength)
                    throw new ParseException(map.Lines["description"], "description exceeds " + MaxDescriptionLength + " characters.");
                rule.Description = text;
            }
            if (map.TryGetValue("weight", out var weight))
            {
                if (!(weight is Int w) || w.Value < 1 || w.Value > 100)
                    throw new ParseException(map.Lines["weight"], "weight must be an integer 1..100.");
                rule.Weight = (int)w.Value;
            }
            if (!map.TryGetValue("assertions", out var al) || !(al is Seq asserts) || asserts.Items.Count == 0)
                throw new ParseException(map.Lines.TryGetValue("assertions", out var al2) ? al2 : node.Line, "'assertions' must be a non-empty list.");
            if (asserts.Items.Count > MaxAssertionsPerRule)
                throw new ParseException(asserts.Line, "'assertions' holds " + asserts.Items.Count + " entries; at most " + MaxAssertionsPerRule + " per rule.");
            foreach (var item in asserts.Items)
                rule.Assertions.Add(ReadAssertion(item));
            return rule;
        }

        private static JObject ReadAssertion(Node node)
        {
            if (!(node is Dict map))
                throw new ParseException(node.Line, "each assertion must be a mapping with 'kind'.");
            if (!map.TryGetValue("kind", out var kindNode))
                throw new ParseException(node.Line, "assertion is missing required 'kind'.");
            var kind = AsString(kindNode, map.Lines["kind"], "kind");
            if (Array.IndexOf(AssertKinds, kind) < 0)
                throw new ParseException(map.Lines["kind"], "unknown kind '" + kind + "' (allowed: bbox, count, solid_volume, edge_closure).");
            if (map.TryGetValue("ids", out var ids))
            {
                if (!(ids is Seq seq))
                    throw new ParseException(map.Lines["ids"], "'ids' must be a list of integers.");
                foreach (var item in seq.Items)
                    if (!(item is Int))
                        throw new ParseException(item.Line, "'ids' must contain only integers.");
            }
            if (map.TryGetValue("box_mm", out var box) && !(box is Dict))
                throw new ParseException(map.Lines["box_mm"], "'box_mm' must be a mapping with min/max.");
            var obj = new JObject();
            foreach (var key in map.Keys)
                obj[key] = ToJson(map[key]);
            return obj;
        }

        private static string AsString(Node node, int line, string what)
        {
            if (node is Str s) return s.Value;
            if (node is Null) return null;
            throw new ParseException(line, "'" + what + "' must be a string.");
        }

        private static JToken ToJson(Node node)
        {
            if (node is Null) return JValue.CreateNull();
            if (node is Bool b) return new JValue(b.Value);
            if (node is Int i) return new JValue(i.Value);
            if (node is Flt f) return new JValue(f.Value);
            if (node is Str s) return new JValue(s.Value);
            if (node is Seq seq)
            {
                var arr = new JArray();
                foreach (var item in seq.Items) arr.Add(ToJson(item));
                return arr;
            }
            if (node is Dict map)
            {
                var obj = new JObject();
                foreach (var key in map.Keys) obj[key] = ToJson(map[key]);
                return obj;
            }
            throw new InvalidOperationException("Unreachable node type.");
        }

        private abstract class Node
        {
            public int Line { get; set; }
        }

        private sealed class Null : Node { }
        private sealed class Bool : Node { public bool Value; }
        private sealed class Int : Node { public long Value; }
        private sealed class Flt : Node { public double Value; }
        private sealed class Str : Node { public string Value; }

        private sealed class Seq : Node
        {
            public List<Node> Items { get; } = new List<Node>();
        }

        private sealed class Dict : Node
        {
            public List<string> Keys { get; } = new List<string>();
            public Dictionary<string, Node> Values { get; } = new Dictionary<string, Node>(StringComparer.Ordinal);
            public Dictionary<string, int> Lines { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
            public Node this[string key] => Values[key];
            public bool TryGetValue(string key, out Node node) => Values.TryGetValue(key, out node);
        }

        private sealed class Reader
        {
            private readonly List<Entry> _lines = new List<Entry>();

            private sealed class Entry
            {
                public int Number;
                public int Indent;
                public string Text;
            }

            public Reader(string yaml)
            {
                var normalized = yaml.Replace("\r\n", "\n").Replace('\r', '\n');
                var raw = normalized.Split('\n');
                for (var n = 0; n < raw.Length; n++)
                {
                    var line = raw[n];
                    if (line.IndexOf('\t') >= 0)
                        throw new ParseException(n + 1, "tabs are not allowed; indent with spaces.");
                    var indent = 0;
                    while (indent < line.Length && line[indent] == ' ') indent++;
                    var text = StripComment(line.Substring(indent)).TrimEnd();
                    if (text.Length == 0) continue;
                    _lines.Add(new Entry { Number = n + 1, Indent = indent, Text = text });
                }
                if (_lines.Count > 0 && _lines[0].Text == "---") _lines.RemoveAt(0);
                if (_lines.Count > 0 && _lines[_lines.Count - 1].Text == "...") _lines.RemoveAt(_lines.Count - 1);
            }

            public Node ReadDocument()
            {
                if (_lines.Count == 0) throw new ParseException(1, "rules_yaml is empty.");
                var pos = 0;
                var node = ReadNode(0, ref pos);
                if (pos != _lines.Count)
                    throw new ParseException(_lines[pos].Number, "unexpected content (indent does not match any open block).");
                return node;
            }

            private Node ReadNode(int indent, ref int pos)
            {
                if (pos >= _lines.Count)
                    throw new ParseException(_lines[_lines.Count - 1].Number, "unexpected end of document.");
                var first = _lines[pos];
                if (first.Indent != indent)
                    throw new ParseException(first.Number, "expected indent " + indent + ", found " + first.Indent + ".");
                return first.Text.StartsWith("-", StringComparison.Ordinal) && IsDash(first.Text)
                    ? (Node)ReadList(indent, ref pos)
                    : ReadMap(indent, ref pos);
            }

            private static bool IsDash(string text) =>
                text.Length == 1 || text[1] == ' ' || text[1] == '\t';

            private Dict ReadMap(int indent, ref int pos)
            {
                var map = new Dict { Line = _lines[pos].Number };
                while (pos < _lines.Count && _lines[pos].Indent == indent && !IsDash(_lines[pos].Text))
                {
                    var entry = _lines[pos];
                    var colon = SplitKey(entry.Text);
                    if (colon < 0)
                        throw new ParseException(entry.Number, "expected 'key: value'.");
                    var key = Unquote(entry.Text.Substring(0, colon).Trim(), entry.Number, true);
                    if (key.Length == 0)
                        throw new ParseException(entry.Number, "empty mapping key.");
                    if (map.Values.ContainsKey(key))
                        throw new ParseException(entry.Number, "duplicate key '" + key + "'.");
                    var rest = entry.Text.Substring(colon + 1).Trim();
                    pos++;
                    Node value;
                    if (rest.Length == 0)
                    {
                        if (pos >= _lines.Count || _lines[pos].Indent <= indent)
                            value = new Null { Line = entry.Number };
                        else
                            value = ReadNode(_lines[pos].Indent, ref pos);
                    }
                    else
                    {
                        value = ReadScalar(rest, entry.Number);
                    }
                    map.Keys.Add(key);
                    map.Values[key] = value;
                    map.Lines[key] = entry.Number;
                }
                if (map.Keys.Count == 0)
                    throw new ParseException(_lines[pos].Number, "expected a mapping entry.");
                return map;
            }

            private Seq ReadList(int indent, ref int pos)
            {
                var seq = new Seq { Line = _lines[pos].Number };
                while (pos < _lines.Count && _lines[pos].Indent == indent && IsDash(_lines[pos].Text))
                {
                    var entry = _lines[pos];
                    var rest = entry.Text.Length == 1 ? "" : entry.Text.Substring(1).TrimStart();
                    pos++;
                    Node item;
                    if (rest.Length == 0)
                    {
                        if (pos >= _lines.Count || _lines[pos].Indent <= indent)
                            throw new ParseException(entry.Number, "list item has no value.");
                        item = ReadNode(_lines[pos].Indent, ref pos);
                    }
                    else if (!rest.Contains(": ") && !rest.EndsWith(":", StringComparison.Ordinal))
                    {
                        item = ReadScalar(rest, entry.Number);
                    }
                    else
                    {
                        // "- key: value" opens an inline map continued on deeper lines.
                        // The continuation sets the map indent (any indent deeper than the dash).
                        var mapIndent = (pos < _lines.Count && _lines[pos].Indent > indent)
                            ? _lines[pos].Indent
                            : indent + 2;
                        var inline = new List<Entry> { new Entry { Number = entry.Number, Indent = mapIndent, Text = rest } };
                        var injected = new List<Entry>(_lines);
                        injected.InsertRange(pos, inline);
                        _lines.Clear();
                        _lines.AddRange(injected);
                        item = ReadMap(mapIndent, ref pos);
                    }
                    item.Line = item.Line == 0 ? entry.Number : item.Line;
                    seq.Items.Add(item);
                }
                return seq;
            }

            private static int SplitKey(string text)
            {
                var inSingle = false;
                var inDouble = false;
                for (var i = 0; i < text.Length; i++)
                {
                    var ch = text[i];
                    if (ch == '\'' && !inDouble) inSingle = !inSingle;
                    else if (ch == '"' && !inSingle)
                    {
                        var backslashes = 0;
                        for (var k = i - 1; k >= 0 && text[k] == '\\'; k--) backslashes++;
                        if (backslashes % 2 == 0) inDouble = !inDouble;
                    }
                    else if (ch == ':' && !inSingle && !inDouble)
                    {
                        if (i + 1 == text.Length || text[i + 1] == ' ') return i;
                    }
                }
                return -1;
            }

            private static string StripComment(string text)
            {
                var inSingle = false;
                var inDouble = false;
                for (var i = 0; i < text.Length; i++)
                {
                    var ch = text[i];
                    if (ch == '\'' && !inDouble) inSingle = !inSingle;
                    else if (ch == '"' && !inSingle)
                    {
                        var backslashes = 0;
                        for (var k = i - 1; k >= 0 && text[k] == '\\'; k--) backslashes++;
                        if (backslashes % 2 == 0) inDouble = !inDouble;
                    }
                    else if (ch == '#' && !inSingle && !inDouble && (i == 0 || text[i - 1] == ' '))
                    {
                        return text.Substring(0, i);
                    }
                }
                return text;
            }

            private static string Unquote(string text, int line, bool isKey)
            {
                if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'')
                    return text.Substring(1, text.Length - 2).Replace("''", "'");
                if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                    return Unescape(text.Substring(1, text.Length - 2), line);
                if (text.Length == 0) return "";
                var first = text[0];
                if ("*&!|>@`%?".IndexOf(first) >= 0)
                    throw new ParseException(line, (isKey ? "invalid mapping key " : "unsupported value ") + "'" + text + "' (anchors, tags and block scalars are not supported; quote the string).");
                if ((first == '{' || first == '[') && !(text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal)))
                    throw new ParseException(line, "flow maps are not supported; use block mappings.");
                return text;
            }

            private static string Unescape(string text, int line)
            {
                var sb = new StringBuilder(text.Length);
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\\') { sb.Append(text[i]); continue; }
                    if (++i >= text.Length)
                        throw new ParseException(line, "dangling backslash in double-quoted string.");
                    switch (text[i])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        default: throw new ParseException(line, "unsupported escape '\\" + text[i] + "'.");
                    }
                }
                return sb.ToString();
            }

            private static Node ReadScalar(string text, int line)
            {
                if (text.StartsWith("[", StringComparison.Ordinal))
                    return ReadFlowList(text, line);
                var unquoted = Unquote(text, line, false);
                if (!ReferenceEquals(unquoted, text))
                    return new Str { Line = line, Value = unquoted };
                var t = text.Trim();
                if (t.Length == 0 || t == "~" || t.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return new Null { Line = line };
                if (t.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return new Bool { Line = line, Value = true };
                if (t.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return new Bool { Line = line, Value = false };
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return new Int { Line = line, Value = l };
                if ((t.IndexOf('.') >= 0 || t.IndexOf('e') >= 0 || t.IndexOf('E') >= 0)
                    && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    && !double.IsNaN(d) && !double.IsInfinity(d))
                    return new Flt { Line = line, Value = d };
                return new Str { Line = line, Value = t };
            }

            private static Node ReadFlowList(string text, int line)
            {
                if (!text.EndsWith("]", StringComparison.Ordinal))
                    throw new ParseException(line, "unterminated flow list (missing ']').");
                var seq = new Seq { Line = line };
                var inner = text.Substring(1, text.Length - 2).Trim();
                if (inner.Length == 0) return seq;
                foreach (var part in SplitFlow(inner, line))
                {
                    var item = part.Trim();
                    if (item.Length == 0)
                        throw new ParseException(line, "empty item in flow list.");
                    if (item[0] == '[' || item[0] == '{')
                        throw new ParseException(line, "nested collections are not supported in flow lists.");
                    var scalar = ReadScalar(item, line);
                    if (scalar is Seq || scalar is Dict)
                        throw new ParseException(line, "nested collections are not supported in flow lists.");
                    seq.Items.Add(scalar);
                }
                return seq;
            }

            private static List<string> SplitFlow(string text, int line)
            {
                var parts = new List<string>();
                var depth = 0;
                var current = new StringBuilder();
                var inSingle = false;
                var inDouble = false;
                for (var i = 0; i < text.Length; i++)
                {
                    var ch = text[i];
                    if (ch == '\'' && !inDouble) inSingle = !inSingle;
                    else if (ch == '"' && !inSingle) inDouble = !inDouble;
                    if (!inSingle && !inDouble)
                    {
                        if (ch == '[') depth++;
                        else if (ch == ']') depth--;
                        else if (ch == ',' && depth == 0)
                        {
                            parts.Add(current.ToString());
                            current.Clear();
                            continue;
                        }
                    }
                    current.Append(ch);
                }
                if (inSingle || inDouble || depth != 0)
                    throw new ParseException(line, "unterminated quote or bracket in flow list.");
                parts.Add(current.ToString());
                return parts;
            }
        }
    }

    /// <summary>Aggregates per-rule verify_assert responses into the run envelope.
    /// The sender is injected so tests can replay canned plugin payloads.</summary>
    internal static class VerifyRulesRunner
    {
        public const int MaxOffendersPerRule = 200;

        public static async Task<JObject> RunAsync(
            VerifyRulesYamlParser.RuleSet rules,
            bool stopOnFirstFail,
            Func<string, object, Task<JObject>> send)
        {
            var ruleResults = new List<JObject>();
            var skipped = new List<string>();
            var passedWeight = 0;
            var totalWeight = 0;
            string staleError = null;
            string modelKey = null;
            var aborted = false;

            foreach (var rule in rules.Rules)
            {
                totalWeight += rule.Weight;
                var payload = new JArray();
                foreach (var assertion in rule.Assertions) payload.Add(assertion.DeepClone());
                JObject envelope;
                try
                {
                    envelope = await send("verify_assert", new { assertions = payload });
                }
                catch (Exception ex)
                {
                    ruleResults.Add(FailRule(rule, "transport: " + ex.Message));
                    if (stopOnFirstFail) { aborted = true; break; }
                    continue;
                }

                var outcome = EvaluateRule(rule, envelope, rules.ModelKey, ref modelKey, out var stale);
                ruleResults.Add(outcome);
                if (stale != null) { staleError = stale; aborted = true; break; }
                if (outcome.Value<bool>("pass")) passedWeight += rule.Weight;
                else if (stopOnFirstFail) { aborted = true; break; }
            }

            if (aborted)
            {
                var done = new HashSet<string>(ruleResults.Select(r => r.Value<string>("id")), StringComparer.Ordinal);
                foreach (var rule in rules.Rules)
                    if (!done.Contains(rule.Id)) skipped.Add(rule.Id);
            }

            var passed = ruleResults.Count > 0 && ruleResults.All(r => r.Value<bool>("pass")) && staleError == null;
            return new JObject
            {
                ["pass"] = passed,
                ["score"] = totalWeight == 0 ? 0 : (int)Math.Round(100.0 * passedWeight / totalWeight),
                ["rules_total"] = rules.Rules.Count,
                ["rules_passed"] = ruleResults.Count(r => r.Value<bool>("pass")),
                ["weight_total"] = totalWeight,
                ["weight_passed"] = passedWeight,
                ["model_key"] = modelKey,
                ["stale_refused"] = staleError != null,
                ["error"] = staleError,
                ["rules"] = new JArray(ruleResults.ToArray()),
                ["skipped"] = new JArray(skipped.Select(s => new JValue(s)).ToArray()),
            };
        }

        private static JObject FailRule(VerifyRulesYamlParser.Rule rule, string error)
        {
            return new JObject
            {
                ["id"] = rule.Id,
                ["description"] = rule.Description,
                ["weight"] = rule.Weight,
                ["pass"] = false,
                ["assertions"] = new JArray(),
                ["offending_ids"] = new JArray(),
                ["offenders_truncated"] = false,
                ["missing_ids"] = new JArray(),
                ["errors"] = new JArray(new JObject { ["error"] = error }),
            };
        }

        private static JObject EvaluateRule(
            VerifyRulesYamlParser.Rule rule, JObject envelope, string pinnedKey,
            ref string modelKey, out string stale)
        {
            stale = null;
            var offending = new List<long>();
            var missing = new List<long>();
            var errors = new List<JObject>();
            var echoed = new JArray();
            var truncated = false;

            if (envelope == null || !Flag(envelope, "success", "Success"))
            {
                errors.Add(new JObject { ["error"] = Text(envelope, "error", "Error") ?? "verify_assert failed without an error message." });
                return BuildRule(rule, false, echoed, offending, truncated, missing, errors);
            }

            var dataValue = envelope["data"] ?? envelope["Data"];
            JObject dataObj = null;
            if (dataValue is JObject direct) dataObj = direct;
            else if (dataValue != null && dataValue.Type == JTokenType.String)
            {
                try { dataObj = JObject.Parse(dataValue.Value<string>()); }
                catch { dataObj = null; }
            }
            if (dataObj == null)
            {
                errors.Add(new JObject { ["error"] = "verify_assert returned no data payload." });
                return BuildRule(rule, false, echoed, offending, truncated, missing, errors);
            }

            var key = dataObj.Value<string>("model_key");
            if (modelKey == null) modelKey = key;
            if (pinnedKey != null && !string.Equals(key, pinnedKey, StringComparison.Ordinal))
            {
                stale = key == null
                    ? "stale model: response carries no model_key, cannot prove the model matches the pinned key. Recapture and re-pin."
                    : "stale model: model_key mismatch (pinned '" + pinnedKey + "', got '" + key + "'). Recapture and re-pin; do not trust these results.";
                return BuildRule(rule, false, echoed, offending, truncated, missing, errors);
            }

            var pass = dataObj.Value<bool?>("pass") ?? false;
            var items = dataObj["assertions"] as JArray;
            if (items != null)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (!(items[i] is JObject a)) continue;
                    var echo = new JObject
                    {
                        ["kind"] = a.Value<string>("kind"),
                        ["pass"] = a.Value<bool?>("pass") ?? false,
                    };
                    var err = a.Value<string>("error");
                    if (!string.IsNullOrEmpty(err))
                    {
                        echo["error"] = err;
                        if (errors.Count < 10) errors.Add(new JObject { ["assertion"] = i, ["error"] = err });
                    }
                    echoed.Add(echo);
                    CollectIds(a, "offending_ids", offending, ref truncated);
                    CollectIds(a, "missing", missing, ref truncated);
                    CollectIds(a, "missingIds", missing, ref truncated);
                }
            }
            if (!pass && errors.Count == 0 && echoed.Count > 0)
            {
                var failed = echoed.Count(e => (e as JObject).Value<bool?>("pass") != true);
                if (failed > 0) errors.Add(new JObject { ["error"] = failed + " assertion(s) failed; see offending_ids." });
            }
            return BuildRule(rule, pass, echoed, offending, truncated, missing, errors);
        }

        private static JObject BuildRule(
            VerifyRulesYamlParser.Rule rule, bool pass, JArray echoed,
            List<long> offending, bool truncated, List<long> missing, List<JObject> errors)
        {
            return new JObject
            {
                ["id"] = rule.Id,
                ["description"] = rule.Description,
                ["weight"] = rule.Weight,
                ["pass"] = pass,
                ["assertions"] = echoed,
                ["offending_ids"] = new JArray(offending.Distinct().OrderBy(x => x).Select(x => new JValue(x)).ToArray()),
                ["offenders_truncated"] = truncated,
                ["missing_ids"] = new JArray(missing.Distinct().OrderBy(x => x).Select(x => new JValue(x)).ToArray()),
                ["errors"] = new JArray(errors.ToArray()),
            };
        }

        private static void CollectIds(JObject assertion, string key, List<long> sink, ref bool truncated)
        {
            if (!(assertion[key] is JArray arr)) return;
            foreach (var token in arr)
            {
                if (token.Type != JTokenType.Integer) continue;
                if (sink.Count >= MaxOffendersPerRule) { truncated = true; return; }
                sink.Add(token.Value<long>());
            }
        }

        private static bool Flag(JObject obj, params string[] names)
        {
            if (obj == null) return false;
            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type == JTokenType.Boolean) return token.Value<bool>();
            }
            return false;
        }

        private static string Text(JObject obj, params string[] names)
        {
            if (obj == null) return null;
            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type == JTokenType.String) return token.Value<string>();
            }
            return null;
        }
    }
}
