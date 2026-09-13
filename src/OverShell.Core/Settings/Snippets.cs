using System.Text.Json.Nodes;

namespace OverShell.Core.Settings;

/// <summary>A reusable prompt, sent to an agent as if typed.</summary>
public sealed record Snippet(string Name, string Text, string? Description);

/// <summary>
/// <c>%APPDATA%\OverShell\snippets.jsonc</c>: an array of <c>{ "name", "text", "description"? }</c>.
/// Each becomes a <c>snippet.&lt;name&gt;</c> command and a prompt-bar entry. A missing file
/// is an empty list; a broken one is reported.
/// </summary>
public static class Snippets
{
    public static IReadOnlyList<Snippet> Load(string path, List<string> problems)
    {
        var node = Jsonc.ReadFile(path, out var error);
        if (error is not null)
        {
            problems.Add($"{path}: {error}");
            return [];
        }

        return Parse(node, problems, Path.GetFileName(path));
    }

    public static IReadOnlyList<Snippet> Parse(JsonNode? node, List<string> problems, string sourceName)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            problems.Add($"{sourceName}: expected an array of snippets");
            return [];
        }

        var list = new List<Snippet>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            if (item is not JsonObject o)
            {
                problems.Add($"{sourceName}: snippet entries must be objects");
                continue;
            }

            var name = Str(o, "name");
            var text = Str(o, "text");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(text))
            {
                problems.Add($"{sourceName}: a snippet needs \"name\" and \"text\"");
                continue;
            }

            if (!seen.Add(name))
            {
                problems.Add($"{sourceName}: duplicate snippet '{name}'");
                continue;
            }

            list.Add(new Snippet(name.Trim(), text, Str(o, "description")));
        }

        return list;
    }

    /// <summary>The command id for a snippet: lower-case, spaces and punctuation collapsed to dashes.</summary>
    public static string CommandId(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        }

        return "snippet." + collapsed.Trim('-');
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
