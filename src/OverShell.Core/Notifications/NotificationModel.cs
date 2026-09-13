using System.Text.RegularExpressions;
using OverShell.Core.Agents;

namespace OverShell.Core.Notifications;

/// <summary>Why the user is being told something.</summary>
public enum NotificationKind
{
    Blocked,
    Done,
    Error,
    Exited,
}

/// <summary>Everything a sink may want to show or template into a command line.</summary>
public sealed record NotificationEvent(
    NotificationKind Kind,
    string TabId,
    string TabLabel,
    string? Harness,
    string? HarnessDisplayName,
    string? Project,
    string? WorkingDirectory,
    string Message,
    string? Detail,
    DateTimeOffset At,
    bool TabIsActive,
    bool WindowIsFocused)
{
    public static NotificationKind KindFor(AgentState state) => state switch
    {
        AgentState.Blocked => NotificationKind.Blocked,
        AgentState.Error => NotificationKind.Error,
        AgentState.Exited => NotificationKind.Exited,
        _ => NotificationKind.Done,
    };

    /// <summary>Values available to <see cref="ArgTemplate"/>: <c>{tab.id}</c>, <c>{kind}</c>, <c>{message}</c>…</summary>
    public IReadOnlyDictionary<string, string> TemplateValues() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["kind"] = Kind.ToString().ToLowerInvariant(),
        ["tab.id"] = TabId,
        ["tab.label"] = TabLabel,
        ["tab.harness"] = Harness ?? string.Empty,
        ["tab.harnessName"] = HarnessDisplayName ?? string.Empty,
        ["tab.project"] = Project ?? string.Empty,
        ["tab.cwd"] = WorkingDirectory ?? string.Empty,
        ["message"] = Message,
        ["detail"] = Detail ?? string.Empty,
        ["time"] = At.ToString("HH:mm"),
        ["title"] = Title(),
    };

    /// <summary>
    /// "Harness · label", except that an agent tab without a user label is labelled with the
    /// harness name already — then the project tells the tabs apart, not a repeated name.
    /// </summary>
    private string Title()
    {
        if (string.IsNullOrEmpty(HarnessDisplayName))
        {
            return TabLabel;
        }

        if (!string.Equals(TabLabel, HarnessDisplayName, StringComparison.Ordinal))
        {
            return $"{HarnessDisplayName} · {TabLabel}";
        }

        return string.IsNullOrEmpty(Project) ? HarnessDisplayName : $"{HarnessDisplayName} · {Project}";
    }
}

/// <summary>When a sink fires. Every condition is optional; all present conditions must hold.</summary>
public sealed class NotificationWhen
{
    /// <summary>Only when the event's tab is not the active one — herdr suppresses toasts for the active tab.</summary>
    public bool? NotActiveTab { get; init; }

    /// <summary>Only when OverShell's window is not the foreground window.</summary>
    public bool? WindowUnfocused { get; init; }

    /// <summary>Kinds this sink handles; empty means all.</summary>
    public string[] Kinds { get; init; } = [];

    /// <summary>Harness ids this sink handles; empty means all. <c>shell</c> matches tabs without a harness.</summary>
    public string[] Harnesses { get; init; } = [];

    /// <summary>Local-time quiet hours as <c>"22:00-07:00"</c>; the sink stays silent inside them.</summary>
    public string? QuietHours { get; init; }

    public bool Allows(NotificationEvent e, TimeOnly localNow)
    {
        if (NotActiveTab == true && e.TabIsActive)
        {
            return false;
        }

        if (WindowUnfocused == true && e.WindowIsFocused)
        {
            return false;
        }

        if (Kinds.Length > 0 && !Kinds.Any(k => k.Equals(e.Kind.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Harnesses.Length > 0)
        {
            var harness = e.Harness ?? "shell";
            if (!Harnesses.Any(h => h.Equals(harness, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return !InQuietHours(QuietHours, localNow);
    }

    internal static bool InQuietHours(string? spec, TimeOnly now)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var parts = spec.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeOnly.TryParse(parts[0], out var from) || !TimeOnly.TryParse(parts[1], out var to))
        {
            return false;
        }

        return from <= to ? now >= from && now < to : now >= from || now < to;
    }
}

/// <summary>One destination for notifications, from <c>settings.jsonc</c>.</summary>
public sealed class NotificationSinkConfig
{
    /// <summary><c>overlay</c>, <c>taskbar</c>, <c>sound</c>, <c>command</c>, <c>toast</c>.</summary>
    public required string Type { get; init; }

    public bool Enabled { get; init; } = true;

    public NotificationWhen When { get; init; } = new();

    /// <summary><c>command</c>: the executable. Resolved through PATH.</summary>
    public string? Exe { get; init; }

    /// <summary><c>command</c>: arguments, each templated with <c>{tab.id}</c>-style placeholders.</summary>
    public string[] Args { get; init; } = [];

    /// <summary><c>command</c>: write the event as JSON to the process's stdin.</summary>
    public bool StdinJson { get; init; } = true;

    /// <summary><c>command</c>: give up after this long.</summary>
    public int TimeoutMs { get; init; } = 10_000;

    /// <summary><c>sound</c>: a .wav path, or a system sound name (<c>asterisk</c>, <c>exclamation</c>, <c>hand</c>, <c>question</c>, <c>beep</c>).</summary>
    public string? Sound { get; init; }

    /// <summary><c>sound</c>: per-kind overrides.</summary>
    public Dictionary<string, string> Sounds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>overlay</c>: how long a toast stays, ms.</summary>
    public int DurationMs { get; init; } = 6000;
}

/// <summary>Expands <c>{name}</c> placeholders; unknown names are left as written so a typo is visible.</summary>
public static partial class ArgTemplate
{
    [GeneratedRegex(@"\{([a-zA-Z][a-zA-Z0-9_.]*)\}")]
    private static partial Regex Placeholder { get; }

    public static string Expand(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static string[] Expand(IEnumerable<string> templates, IReadOnlyDictionary<string, string> values) =>
        templates.Select(t => Expand(t, values)).ToArray();
}
