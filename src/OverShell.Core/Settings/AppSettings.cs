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

/// <summary>What fills the middle of the window.</summary>
public enum ViewContent
{
    /// <summary>The active tab's live terminal.</summary>
    Terminal,

    /// <summary>One card per tab, bodies from screen text.</summary>
    Dashboard,
}

/// <summary>A view is a layout plus what the middle shows (DESIGN.md §12.5). Four ship; users may retune them.</summary>
public sealed class ViewDefinition
{
    /// <summary>A layout preset name or a file under <c>layouts\</c>.</summary>
    public string Layout { get; init; } = "top";

    public ViewContent Content { get; init; } = ViewContent.Terminal;

    public string? Title { get; init; }
}

/// <summary>Git decoration per tab.</summary>
public sealed class GitSettings
{
    /// <summary>Read <c>.git/HEAD</c> for the branch name. Cheap; on by default.</summary>
    public bool Branch { get; init; } = true;

    /// <summary>Run <c>git status --porcelain</c> per repository for the dirty marker. Off by default: it spawns a process.</summary>
    public bool Dirty { get; init; }

    /// <summary>How often one repository is asked for its status, when <see cref="Dirty"/> is on.</summary>
    public int StatusIntervalMs { get; init; } = 10_000;
}

/// <summary>What comes back after a restart.</summary>
public sealed class SessionSettings
{
    /// <summary>Reopen the tabs (profile, directory, label, group) and the view that were open when the window closed.</summary>
    public bool Restore { get; init; } = true;

    /// <summary>For a restored tab whose agent left a session id, type its resume command once the shell is ready.</summary>
    public bool ResumeAgents { get; init; } = true;
}

/// <summary>The <c>overshell://</c> URL protocol.</summary>
public sealed class ProtocolSettings
{
    /// <summary>Register <c>overshell://</c> for this user at start (HKCU, no elevation), so toast clicks find their tab.</summary>
    public bool Register { get; init; } = true;
}

/// <summary>
/// <c>settings.jsonc</c>. The embedded defaults are merged with the user's file, so the
/// user names only what changes; a broken user file falls back to defaults with the
/// problem recorded in <see cref="Problems"/> rather than an exception.
/// </summary>
public sealed class AppSettings
{
    public static readonly string[] ViewOrder = ["terminal", "herd", "dashboard", "zen"];

    /// <summary>The view shown at start: <c>terminal</c>, <c>herd</c>, <c>dashboard</c>, <c>zen</c>.</summary>
    public string View { get; init; } = "terminal";

    /// <summary>The four views and their layouts; a user file may retune any of them.</summary>
    public Dictionary<string, ViewDefinition> Views { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A skin under <c>skins\&lt;name&gt;.xaml</c>: a ResourceDictionary overriding theme keys. Null for none.</summary>
    public string? Skin { get; init; }

    public DetectionSettings Detection { get; init; } = new();

    public NotificationSettings Notifications { get; init; } = new();

    public TabSettings Tabs { get; init; } = new();

    public GitSettings Git { get; init; } = new();

    public SessionSettings Session { get; init; } = new();

    public ProtocolSettings Protocol { get; init; } = new();

    public List<string> Problems { get; } = [];

    /// <summary>The view's definition, else a terminal view on the default layout.</summary>
    public ViewDefinition ViewFor(string id) => Views.GetValueOrDefault(id) ?? new ViewDefinition();

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
