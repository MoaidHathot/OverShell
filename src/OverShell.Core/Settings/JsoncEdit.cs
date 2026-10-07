using System.Text;
using System.Text.Json;

namespace OverShell.Core.Settings;

/// <summary>
/// Changes one value in JSONC text and nothing else. The settings file is the user's: it carries
/// their comments, their blank lines, their order, so a tool that wants to flip one key cannot
/// parse the file and print it back (the comments would be gone). This scans the text instead -
/// comments and strings skipped as the reader does - finds the value under a key path and
/// replaces exactly that span, or adds the member (and the objects on the way to it) where it
/// belongs, indented like its neighbours, in the file's own line ending.
/// </summary>
public static class JsoncEdit
{
    /// <summary>
    /// Sets the value at <paramref name="path"/> (<c>["window", "terminalOpacity"]</c>) to
    /// <paramref name="json"/>, a value already in JSON form (<c>0.85</c>, <c>"mica"</c>,
    /// <c>true</c>, <c>null</c>). Returns the new text, or null with <paramref name="error"/>
    /// set - when the text is not an object, or when the result would not read back, in which
    /// case nothing should be written.
    /// </summary>
    public static string? Set(string text, IReadOnlyList<string> path, string json, out string? error)
    {
        error = null;
        if (path.Count == 0)
        {
            error = "empty setting path";
            return null;
        }

        string result;
        try
        {
            var scanner = new Scanner(text);
            scanner.SkipTrivia();
            if (scanner.AtEnd)
            {
                // Nothing but comments or whitespace: the object is ours to start.
                var nl = NewLineOf(text);
                var head = text.TrimEnd();
                result = (head.Length == 0 ? string.Empty : head + nl) + "{" + nl + Member(path, 0, json, "  ", nl, multiLine: true) + nl + "}" + nl;
            }
            else if (scanner.Current != '{')
            {
                error = "the file does not start with an object";
                return null;
            }
            else
            {
                result = SetInObject(text, scanner.Position, path, 0, json);
            }
        }
        catch (FormatException e)
        {
            error = e.Message;
            return null;
        }

        if (Jsonc.Parse(result, out var parseError) is null)
        {
            error = $"the edit would leave the file unreadable ({parseError ?? "empty"}); nothing was changed";
            return null;
        }

        return result;
    }

    // The file is read by people: a + or a # in a value stays itself rather than becoming \u002B.
    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The JSON form of a value for <see cref="Set"/>: strings quoted and escaped, numbers invariant, booleans lower-case.</summary>
    public static string ToJson(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => JsonSerializer.Serialize(s, Plain),
        double d => d.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture),
        int or long or short or byte => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
        _ => JsonSerializer.Serialize(value, Jsonc.SerializerOptions),
    };

    private static string SetInObject(string text, int open, IReadOnlyList<string> path, int depth, string json)
    {
        var scanner = new Scanner(text) { Position = open + 1 };
        var key = path[depth];
        var last = depth == path.Count - 1;
        var firstKeyStart = -1;
        var lastMemberEnd = -1;
        var trailingComma = -1;

        while (true)
        {
            scanner.SkipTrivia();
            if (scanner.AtEnd)
            {
                throw new FormatException("unterminated object");
            }

            if (scanner.Current == '}')
            {
                break;
            }

            if (scanner.Current != '"')
            {
                throw new FormatException($"expected a key at offset {scanner.Position}");
            }

            if (firstKeyStart < 0)
            {
                firstKeyStart = scanner.Position;
            }

            var name = scanner.ReadString();
            scanner.SkipTrivia();
            if (scanner.AtEnd || scanner.Current != ':')
            {
                throw new FormatException($"expected ':' after \"{name}\"");
            }

            scanner.Position++;
            scanner.SkipTrivia();
            var valueStart = scanner.Position;
            scanner.SkipValue();
            var valueEnd = scanner.Position;

            // The reader matches keys case-insensitively (Jsonc.NodeOptions), so a file that
            // spells a key differently is still the same setting.
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                if (last)
                {
                    return Splice(text, valueStart, valueEnd, json);
                }

                if (text[valueStart] == '{')
                {
                    return SetInObject(text, valueStart, path, depth + 1, json);
                }

                // The key holds a scalar or an array where the path needs an object: the object replaces it.
                return Splice(text, valueStart, valueEnd, Member(path, depth + 1, json, string.Empty, NewLineOf(text), multiLine: false, objectOnly: true));
            }

            lastMemberEnd = valueEnd;
            trailingComma = -1;
            scanner.SkipTrivia();
            if (scanner.AtEnd)
            {
                throw new FormatException("unterminated object");
            }

            if (scanner.Current == ',')
            {
                trailingComma = scanner.Position;
                scanner.Position++;
                continue;
            }

            if (scanner.Current == '}')
            {
                break;
            }

            throw new FormatException($"expected ',' or '}}' at offset {scanner.Position}");
        }

        return InsertMember(text, open, scanner.Position, firstKeyStart, lastMemberEnd, trailingComma, path, depth, json);
    }

    private static string InsertMember(string text, int open, int close, int firstKeyStart, int lastMemberEnd, int trailingComma, IReadOnlyList<string> path, int depth, string json)
    {
        var nl = NewLineOf(text);
        // An object written on one line stays on its line; an empty top-level object (a file
        // that is just `{ }`) grows into the multi-line shape every other file has.
        var multiLine = text.AsSpan(open, close - open).Contains('\n') || (depth == 0 && lastMemberEnd < 0);
        if (!multiLine)
        {
            var inline = Member(path, depth, json, string.Empty, nl, multiLine: false);
            if (lastMemberEnd < 0)
            {
                return Splice(text, open + 1, close, " " + inline + " ");
            }

            return trailingComma >= 0
                ? Splice(text, trailingComma + 1, trailingComma + 1, " " + inline)
                : Splice(text, lastMemberEnd, lastMemberEnd, ", " + inline);
        }

        var braceIndent = IndentOfLine(text, open);
        var indent = firstKeyStart >= 0 ? IndentOfLine(text, firstKeyStart) : braceIndent + "  ";
        var member = Member(path, depth, json, indent, nl, multiLine: true);
        if (lastMemberEnd < 0)
        {
            return Splice(text, open + 1, close, nl + member + nl + braceIndent);
        }

        // After the last member's line, so a comment following that value on its line stays
        // with it; the comma goes right after the value when the member had none, and the new
        // member keeps a trailing comma when the object's style has one.
        var afterLast = trailingComma >= 0 ? trailingComma + 1 : lastMemberEnd;
        var lineEnd = Math.Min(EndOfLine(text, afterLast), close);
        var sb = new StringBuilder(text.Length + member.Length + 8);
        if (trailingComma >= 0)
        {
            sb.Append(text, 0, lineEnd).Append(nl).Append(member).Append(',');
        }
        else
        {
            sb.Append(text, 0, lastMemberEnd).Append(',').Append(text, lastMemberEnd, lineEnd - lastMemberEnd).Append(nl).Append(member);
        }

        sb.Append(text, lineEnd, text.Length - lineEnd);
        return sb.ToString();
    }

    /// <summary>
    /// <c>"key": value</c> for the path from <paramref name="depth"/> on, the value being
    /// <paramref name="json"/> wrapped in one object per remaining segment. Multi-line members
    /// start with <paramref name="indent"/> and nest two spaces deeper per level; inline ones
    /// are a single line. <paramref name="objectOnly"/> gives the value alone, without the
    /// first key.
    /// </summary>
    private static string Member(IReadOnlyList<string> path, int depth, string json, string indent, string nl, bool multiLine, bool objectOnly = false)
    {
        if (objectOnly)
        {
            return depth >= path.Count ? json : "{ " + Member(path, depth, json, string.Empty, nl, multiLine: false) + " }";
        }

        var key = JsonSerializer.Serialize(path[depth], Plain);
        if (depth == path.Count - 1)
        {
            return $"{indent}{key}: {json}";
        }

        return multiLine
            ? $"{indent}{key}: {{{nl}{Member(path, depth + 1, json, indent + "  ", nl, true)}{nl}{indent}}}"
            : $"{key}: {{ {Member(path, depth + 1, json, string.Empty, nl, false)} }}";
    }

    private static string Splice(string text, int start, int end, string replacement) =>
        string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));

    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int EndOfLine(string text, int from)
    {
        var i = text.IndexOfAny(['\r', '\n'], from);
        return i < 0 ? text.Length : i;
    }

    private static string IndentOfLine(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var i = lineStart;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
        {
            i++;
        }

        return text.Substring(lineStart, i - lineStart);
    }

    /// <summary>Walks JSONC: whitespace and both comment forms are trivia; strings and containers are skipped whole.</summary>
    private sealed class Scanner(string text)
    {
        public int Position { get; set; }

        public bool AtEnd => Position >= text.Length;

        public char Current => text[Position];

        public void SkipTrivia()
        {
            while (!AtEnd)
            {
                var c = Current;
                if (char.IsWhiteSpace(c))
                {
                    Position++;
                    continue;
                }

                if (c == '/' && Position + 1 < text.Length)
                {
                    if (text[Position + 1] == '/')
                    {
                        Position = EndOfLine(text, Position);
                        continue;
                    }

                    if (text[Position + 1] == '*')
                    {
                        var end = text.IndexOf("*/", Position + 2, StringComparison.Ordinal);
                        Position = end < 0 ? text.Length : end + 2;
                        continue;
                    }
                }

                break;
            }
        }

        /// <summary>At an opening quote: returns the decoded string and stops after the closing quote.</summary>
        public string ReadString()
        {
            var start = Position;
            var i = start + 1;
            while (i < text.Length && text[i] != '"')
            {
                i += text[i] == '\\' ? 2 : 1;
            }

            if (i >= text.Length)
            {
                throw new FormatException($"unterminated string at offset {start}");
            }

            Position = i + 1;
            try
            {
                return JsonSerializer.Deserialize<string>(text.AsSpan(start, Position - start)) ?? string.Empty;
            }
            catch (JsonException)
            {
                throw new FormatException($"malformed string at offset {start}");
            }
        }

        public void SkipValue()
        {
            if (AtEnd)
            {
                throw new FormatException("expected a value");
            }

            switch (Current)
            {
                case '"':
                    ReadString();
                    return;
                case '{':
                case '[':
                    SkipContainer();
                    return;
                default:
                    // A number or a literal runs to the next delimiter.
                    while (!AtEnd && !char.IsWhiteSpace(Current) && Current is not (',' or '}' or ']' or '/'))
                    {
                        Position++;
                    }

                    return;
            }
        }

        private void SkipContainer()
        {
            var start = Position;
            var depth = 0;
            while (true)
            {
                SkipTrivia();
                if (AtEnd)
                {
                    throw new FormatException($"unterminated object or array at offset {start}");
                }

                var c = Current;
                if (c == '"')
                {
                    ReadString();
                    continue;
                }

                Position++;
                if (c is '{' or '[')
                {
                    depth++;
                }
                else if (c is '}' or ']' && --depth == 0)
                {
                    return;
                }
            }
        }
    }
}
