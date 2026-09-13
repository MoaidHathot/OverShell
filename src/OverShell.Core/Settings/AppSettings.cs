using OverShell.Core.Notifications;

namespace OverShell.Core.Settings;

/// <summary>Detector tuning.</summary>
public sealed class DetectionSettings
{
    /// <summary>Wait this long after output stops before reading the screen. Coalesces bursts.</summary>
    public int SnapshotDebounceMs { get; init; } = 300;

    /// <summary>Never read one tab's screen more often than this.</summary>
    public int SnapshotMinIntervalMs { get; init; } = 300;

    /// <summary>How often the foreground-process probe runs per tab with recent output.</summary>
    public int ProcessProbeIntervalMs { get; init; } = 2500;

    /// <summary>Treat every tab as an agent tab, even with no harness detected. Off: shells stay shells.</summary>
    public bool TreatUnknownAsAgent { get; init; }
}

public sealed class NotificationSettings
{
    /// <summary>Named sinks, so a user file can flip one setting on one sink and the merge keeps the rest.</summary>
    public Dictionary<string, NotificationSinkConfig> Sinks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TabSettings
{
    /// <summary>Two-line tab items (label + state/cwd) instead of one.</summary>
    public bool TwoLine { get; init; } = true;

    /// <summary>Show the harness glyph on agent tabs.</summary>
    public bool ShowHarnessGlyph { get; init; } = true;
}

/// <summary>
/// <c>settings.jsonc</c>. The embedded defaults are merged with the user's file, so the
/// user names only what changes; a broken user file falls back to defaults with the
/// problem recorded in <see cref="Problems"/> rather than an exception.
/// </summary>
public sealed class AppSettings
{
    /// <summary><c>terminal</c>, <c>herd</c>, <c>dashboard</c>, <c>zen</c>.</summary>
    public string View { get; init; } = "terminal";

    /// <summary>A layout preset name or a file under <c>layouts/</c>.</summary>
    public string Layout { get; init; } = "top";

    public DetectionSettings Detection { get; init; } = new();

    public NotificationSettings Notifications { get; init; } = new();

    public TabSettings Tabs { get; init; } = new();

    public List<string> Problems { get; } = [];

    public static AppSettings LoadDefaults() => Load(null);

    public static AppSettings Load(string? userFilePath)
    {
        var problems = new List<string>();

        var defaults = Jsonc.Parse(EmbeddedResources.Read("settings.jsonc"), out var defaultsError);
        if (defaultsError is not null)
        {
            problems.Add($"defaults: {defaultsError}");
        }

        var merged = defaults;
        if (userFilePath is not null)
        {
            var user = Jsonc.ReadFile(userFilePath, out var userError);
            if (userError is not null)
            {
                problems.Add($"{userFilePath}: {userError}");
            }
            else if (user is not null)
            {
                merged = Jsonc.Merge(defaults, user);
            }
        }

        var settings = Jsonc.To<AppSettings>(merged, out var shapeError);
        if (settings is null)
        {
            problems.Add($"settings: {shapeError ?? "empty"}");
            settings = Jsonc.To<AppSettings>(defaults, out _) ?? new AppSettings();
        }

        settings.Problems.AddRange(problems);
        return settings;
    }
}
