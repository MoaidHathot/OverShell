using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using OverShell.Core.Agents;

namespace OverShell.Core.Tabs;

public enum TabLabelMode { Session, Project, Harness }

public sealed record TabLabelEntry(string Key, string NamingId, string Text, string Project, bool Manual);

/// <summary>Stable tab identity is separate from changing activity summaries and status icons.</summary>
public static partial class TabLabels
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace { get; }

    public static string Clean(string? text) => Whitespace.Replace(
        string.Concat((text ?? string.Empty).Where(c => !char.IsControl(c) || char.IsWhiteSpace(c))), " ").Trim();

    public static bool TryMode(string? value, out TabLabelMode mode)
    {
        mode = value?.Trim().ToLowerInvariant() switch
        {
            "project" => TabLabelMode.Project,
            "harness" => TabLabelMode.Harness,
            _ => TabLabelMode.Session,
        };
        return value?.Trim().ToLowerInvariant() is "session" or "project" or "harness";
    }

    public static string? SessionName(string? title)
    {
        var clean = Clean(title);
        if (clean.Length == 0 || clean.Equals("New session", StringComparison.OrdinalIgnoreCase) || clean.Equals("Child session", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var prefix in new[] { "New session - ", "Child session - " })
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(clean[prefix.Length..], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
        return clean;
    }

    /// <summary>Uses the title actually emitted by the visible application, without its status decoration.</summary>
    public static string? FromTerminal(string? raw, string? harness, string? harnessName, string? profile, string? project)
    {
        var title = Clean(TitleText.ForDisplay(raw));
        if (title.StartsWith("OC |", StringComparison.OrdinalIgnoreCase))
        {
            // Split only twice: a session's own name may contain a pipe.
            var parts = title.Split('|', 3);
            return parts.Length == 3 ? SessionName(parts[2]) : null;
        }

        foreach (var name in new[] { harnessName, harness })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var separator in new[] { " | ", " - ", " — ", ": " })
            {
                if (title.StartsWith(name + separator, StringComparison.OrdinalIgnoreCase))
                    title = title[(name.Length + separator.Length)..].Trim();
            }
        }

        if (new[] { harnessName, harness, profile, project, "OC", "PowerShell", "Windows PowerShell", "Command Prompt", "pwsh", "cmd" }.Any(name => string.Equals(title, name, StringComparison.OrdinalIgnoreCase)) ||
            !title.Any(char.IsLetterOrDigit) ||
            Path.IsPathFullyQualified(title)) return null;
        return SessionName(title);
    }

    public static string Primary(TabLabelMode mode, bool agent, string? session, string? terminal, string project, string harness, string profile)
    {
        var task = SessionName(session) ?? terminal;
        if (!agent) return Clean(terminal ?? profile);
        return mode switch
        {
            TabLabelMode.Harness => Clean(harness),
            TabLabelMode.Project when project.Length > 0 => task is { Length: > 0 } && !task.Equals(project, StringComparison.OrdinalIgnoreCase) ? $"{project} — {task}" : project,
            _ => task ?? (project.Length > 0 ? project : Clean(harness.Length > 0 ? harness : profile)),
        };
    }

    /// <summary>Prefixes remain visible when a long title is ellipsized. Manual names always win.</summary>
    public static IReadOnlyDictionary<string, string> Prefixes(IReadOnlyList<TabLabelEntry> tabs)
    {
        var prefixes = tabs.ToDictionary(t => t.Key, _ => string.Empty, StringComparer.Ordinal);
        foreach (var group in tabs.GroupBy(t => t.Text, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var tab in group.Where(t => !t.Manual && t.Project.Length > 0))
            {
                if (!tab.Text.Equals(tab.Project, StringComparison.OrdinalIgnoreCase) && !tab.Text.StartsWith(tab.Project + " — ", StringComparison.OrdinalIgnoreCase))
                    prefixes[tab.Key] = tab.Project + " — ";
            }
        }

        var groups = tabs.GroupBy(t => prefixes[t.Key] + t.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        var used = new HashSet<string>(groups.Where(g => g.Count() == 1).Select(g => g.Key)
            .Concat(tabs.Where(t => t.Manual).Select(t => t.Text)), StringComparer.OrdinalIgnoreCase);
        var tokens = tabs.ToDictionary(t => t.Key, t => IsNamingId(t.NamingId) ? t.NamingId.ToLowerInvariant() : Hash(t.Key), StringComparer.Ordinal);
        foreach (var duplicate in tokens.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).ToArray())
            foreach (var item in duplicate) tokens[item.Key] += Hash(item.Key);

        foreach (var group in groups.Where(g => g.Count() > 1))
        {
            foreach (var tab in group.Where(t => !t.Manual).OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                var token = tokens[tab.Key];
                var length = 4;
                while (length < token.Length && tokens.Any(t => t.Key != tab.Key && t.Value.StartsWith(token[..length], StringComparison.Ordinal))) length++;
                var projectPrefix = prefixes[tab.Key];
                var attempt = 0;
                string prefix;
                do
                {
                    prefix = $"[{token[..length]}{(attempt == 0 ? string.Empty : $"-{attempt + 1}")}] " + projectPrefix;
                    if (used.Add(prefix + tab.Text)) break;
                    if (length < token.Length) length++;
                    else attempt++;
                } while (true);
                prefixes[tab.Key] = prefix;
            }
        }

        return prefixes;
    }

    public static bool IsNamingId(string? value) => value is { Length: >= 8 and <= 32 } && value.All(char.IsAsciiHexDigit);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
