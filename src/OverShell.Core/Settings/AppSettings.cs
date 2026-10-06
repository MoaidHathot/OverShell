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

    /// <summary>
    /// Learn a tab's working directory from the shell process itself (its PEB) when the shell
    /// does not announce it with OSC 7 / 9;9 - every couple of seconds after output, every
    /// ten seconds otherwise. A shell that does announce it is believed instead.
    /// </summary>
    public bool CwdFromProcess { get; init; } = true;

    /// <summary>
    /// Put the shell integration into a plain PowerShell launch (`-NoExit -Command`, the way
    /// VS Code does) so the shell announces its directory exactly, with no profile edit. A
    /// profile that runs a command or a file of its own is left alone (§12.15).
    /// </summary>
    public bool InjectShellIntegration { get; init; } = true;
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

    /// <summary>For a restored tab whose agent was running, bring the agent back: its session by id when one is known.</summary>
    public bool ResumeAgents { get; init; } = true;

    /// <summary>
    /// When no session id is known (no integration installed), resume the harness's most
    /// recent session instead (<c>opencode --continue</c>). The tool decides what "most
    /// recent" means, usually per directory.
    /// </summary>
    public bool ResumeWithoutId { get; init; } = true;

    /// <summary>Put the main window and the tear-off windows back where they were.</summary>
    public bool RestoreWindows { get; init; } = true;

    /// <summary>
    /// Paint what each tab showed when the previous run ended, dimmed, above the new shell's
    /// prompt: <c>interrupted</c> (default - after a crash or sign-out, when you did not choose
    /// to close), <c>always</c>, or <c>never</c>.
    /// </summary>
    public string ShowPreviousScreen { get; init; } = "interrupted";
    /// <summary>
    /// Closing the window while agents are working or waiting asks first (they resume at the
    /// next start, but a mid-turn kill is rarely what Alt+F4 meant). Never on sign-out.
    /// </summary>
    public bool ConfirmCloseWithAgents { get; init; } = true;
    /// <summary>
    /// Ask Windows to start OverShell again after a restart or sign-out, when the Windows
    /// setting "Automatically save my restartable apps and restart them when I sign back in"
    /// is on. Never after a crash. Off by default: starting by itself is a choice.
    /// </summary>
    public bool RestartWithWindows { get; init; }
}

/// <summary>How the tabs that need you are walked (§12.16).</summary>
public sealed class AttentionSettings
{
    /// <summary><c>age</c>: the tab that has waited longest first (default); <c>strip</c>: the next one in tab order.</summary>
    public string Order { get; init; } = "age";

    public bool ByAge => !string.Equals(Order, "strip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Key handling (§12.16).</summary>
public sealed class KeySettings{
    /// <summary>How long a key sequence waits for its next chord before it is dropped.</summary>
    public int SequenceTimeoutMs { get; init; } = 3000;

    /// <summary>Show the hint bar listing what the next chord of a pending sequence can be.</summary>
    public bool Hints { get; init; } = true;
}

/// <summary>Behaviours matched to Windows Terminal's, under its names (§12.15).</summary>
public sealed class CompatibilitySettings{
    /// <summary>
    /// Build every tab's environment from the registry, as a fresh logon would, instead of
    /// inheriting OverShell's own - so a tool installed after OverShell (or after whatever
    /// launched it) is on PATH in the next tab. Off: tabs inherit the process environment,
    /// which is what you want when you start OverShell from a shell with variables of its own.
    /// </summary>
    public bool ReloadEnvironmentVariables { get; init; } = true;
}

/// <summary>The <c>overshell://</c> URL protocol.</summary>
public sealed class ProtocolSettings
{
    /// <summary>Register <c>overshell://</c> for this user at start (HKCU, no elevation), so toast clicks find their tab.</summary>
    public bool Register { get; init; } = true;
}

/// <summary>The loopback endpoint integrations and the control API come through (12.18).</summary>
public sealed class EndpointSettings
{
    /// <summary>
    /// Serve the control routes (read a screen, send text, reply, open a tab, wait) and write
    /// <c>endpoint.json</c> for <c>OverShell mcp</c>. Off, the endpoint only takes the
    /// integrations' reports and the plugins' command polls.
    /// </summary>
    public bool Control { get; init; } = true;
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

    /// <summary><c>system</c> (follow the Windows app theme, the default), <c>dark</c> or <c>light</c>.</summary>
    public string Theme { get; init; } = "system";

    /// <summary><c>system</c> (the Windows accent colour, the default), <c>palette</c> (the theme's own blue) or a <c>#RRGGBB</c> colour.</summary>
    public string? Accent { get; init; } = "system";
    public DetectionSettings Detection { get; init; } = new();

    public NotificationSettings Notifications { get; init; } = new();

    public TabSettings Tabs { get; init; } = new();

    public GitSettings Git { get; init; } = new();

    public SessionSettings Session { get; init; } = new();

    public ProtocolSettings Protocol { get; init; } = new();

    public EndpointSettings Endpoint { get; init; } = new();

    public CompatibilitySettings Compatibility { get; init; } = new();

    public KeySettings Keys { get; init; } = new();

    public AttentionSettings Attention { get; init; } = new();

    /// <summary>
    /// Per-extension settings (§12.16): <c>"extensions": { "&lt;id&gt;": { "enabled": true, ... } }</c>.
    /// Kept as JSON so an extension binds its own shape with <see cref="ExtensionSettings{T}"/>.
    /// </summary>
    public Dictionary<string, System.Text.Json.Nodes.JsonNode?> Extensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Problems { get; } = [];

    /// <summary>Whether an extension is turned on: <c>extensions.&lt;id&gt;.enabled</c>, default true.</summary>
    public bool ExtensionEnabled(string id) =>
        !(Extensions.GetValueOrDefault(id) is System.Text.Json.Nodes.JsonObject o
          && o["enabled"] is System.Text.Json.Nodes.JsonValue v
          && v.TryGetValue<bool>(out var enabled)
          && !enabled);

    /// <summary>An extension's section bound to <typeparamref name="T"/>; a fresh instance when absent or malformed.</summary>
    public T ExtensionSettings<T>(string id, out string? problem) where T : class, new()
    {
        problem = null;
        var node = Extensions.GetValueOrDefault(id);
        if (node is null)
        {
            return new T();
        }

        var bound = Jsonc.To<T>(node, out var error);
        if (bound is null)
        {
            problem = error;
            return new T();
        }

        return bound;
    }
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
