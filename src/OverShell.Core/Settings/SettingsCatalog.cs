using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverShell.Core.Input;

namespace OverShell.Core.Settings;

/// <summary>How a setting is chosen in the palette.</summary>
public enum SettingKind
{
    /// <summary>One of a fixed list of values.</summary>
    Choice,

    /// <summary>On or off.</summary>
    Toggle,

    /// <summary>A number: the presets listed, or one typed into the box.</summary>
    Number,

    /// <summary>Text: the presets listed, or what is typed when <see cref="SettingKnob.Validate"/> accepts it.</summary>
    Text,
}

/// <summary>One value a setting can take, in JSON form, with what to call it.</summary>
public sealed record SettingChoice(string Json, string Label, string? Detail = null);

/// <summary>
/// A setting the palette can change: where it lives in <c>settings.jsonc</c>, what to call it,
/// one line on what it does, and the values it takes. <see cref="Validate"/> judges typed text
/// for <see cref="SettingKind.Text"/> and returns the JSON to write, or null to refuse it.
/// </summary>
public sealed record SettingKnob(string Path, string Title, string Description, SettingKind Kind, IReadOnlyList<SettingChoice> Choices)
{
    /// <summary>
    /// The key path in the file. Split on dots by default; an extension's section is keyed by
    /// its whole id (<c>extensions["herd.mode"].hints</c>), so those name their segments.
    /// </summary>
    public string[] Segments { get; init; } = Path.Split('.');

    public double Min { get; init; } = double.NegativeInfinity;

    public double Max { get; init; } = double.PositiveInfinity;

    public Func<string, string?>? Validate { get; init; }

    /// <summary>Extra choices computed when the picker opens (the skin files on disk).</summary>
    public Func<IEnumerable<SettingChoice>>? MoreChoices { get; init; }

    /// <summary>What to show for a JSON null when null means something for this setting ("same as the body opacity"); "none" otherwise.</summary>
    public string? NullDisplay { get; init; }

    /// <summary>A value as the picker shows it for this knob: <see cref="NullDisplay"/> for null, else <see cref="Display(JsonNode?)"/>.</summary>
    public string DisplayValue(JsonNode? value) => value is null && NullDisplay is not null ? NullDisplay : Display(value);

    /// <summary>The same for a JSON literal.</summary>
    public string DisplayValue(string json) => DisplayValue(string.Equals(json, "null", StringComparison.Ordinal) ? null : JsonNode.Parse(json));
    /// <summary>
    /// The JSON to write for text typed into the box, or null when it is not a value for this
    /// setting: a number within range for <see cref="SettingKind.Number"/>, what <see cref="Validate"/>
    /// accepts for <see cref="SettingKind.Text"/>, nothing for the other kinds.
    /// </summary>
    public string? JsonForTyped(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        switch (Kind)
        {
            case SettingKind.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < Min || number > Max)
                {
                    return null;
                }

                return JsoncEdit.ToJson(number == Math.Floor(number) && Min >= 0 && Max > 1 ? (object)(long)number : number);
            case SettingKind.Text:
                return Validate?.Invoke(text);
            default:
                return null;
        }
    }

    /// <summary>A value as the picker shows it: the JSON without a string's quotes; "none" for null.</summary>
    public static string Display(JsonNode? value) => value switch
    {
        null => "none",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("0.0###", CultureInfo.InvariantCulture),
        _ => value.ToJsonString(),
    };

    /// <summary>The same for a JSON literal as it is written in the file.</summary>
    public static string Display(string json) => Display(string.Equals(json, "null", StringComparison.Ordinal) ? null : JsonNode.Parse(json));
}

/// <summary>
/// The settings the palette offers (<c>Change a setting</c>, and one command per entry). A
/// curated list rather than every key: these are the ones that get turned, each with a line
/// that says what it does and the values that make sense, so the palette can explain where a
/// file would only show a name. Anything else is a line in <c>settings.jsonc</c>, which
/// <c>Open settings.jsonc</c> writes from the commented defaults when it does not exist yet.
/// </summary>
public static class SettingsCatalog
{
    private static readonly SettingChoice On = new("true", "On");
    private static readonly SettingChoice Off = new("false", "Off");

    private static readonly Regex HexColour = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<SettingKnob> Knobs { get; } =
    [
        new("floating.widthRatio", "Floating terminal width", "Fraction of the owning window's width, clamped to its monitor. Applies live.", SettingKind.Number,
            [new("0.6", "60%"), new("0.8", "80%"), new("0.95", "95%")]) { Min = 0.25, Max = 1 },
        new("floating.heightRatio", "Floating terminal height", "Fraction of the owning window's height, clamped to its monitor. Applies live.", SettingKind.Number,
            [new("0.5", "50%"), new("0.75", "75%"), new("0.95", "95%")]) { Min = 0.25, Max = 1 },
        new("floating.position", "Floating terminal position", "Placement relative to its owner; terminals can also be moved and resized interactively.", SettingKind.Choice,
            [new("\"center\"", "center"), new("\"topRight\"", "top right"), new("\"bottomRight\"", "bottom right")]),
        new("floating.hideOnFocusLoss", "Hide floats on focus loss", "Dismiss a float when focus leaves it and its owned pickers; the session keeps running.", SettingKind.Toggle, [On, Off]),
        new("attention.presentation", "Attention jump presentation", "Switch tabs or interact in a floating terminal; Return goes back to the interrupted work.", SettingKind.Choice,
            [new("\"tab\"", "tab"), new("\"floating\"", "floating")]),
        new("notifications.openMode", "Open notifications as", "How a notification click presents its tab. Open floating is also an explicit toast action.", SettingKind.Choice,
            [new("\"tab\"", "tab"), new("\"floating\"", "floating"), new("\"preview\"", "read-only preview")]),
        new("dashboard.previewMode", "Dashboard previews", "Live rendered previews where supported, styled cell snapshots, or the legacy text extract. Applies live.", SettingKind.Choice,
        [new("\"auto\"", "auto", "live renderer with styled fallback (default)"), new("\"styled\"", "styled", "colours and formatting from terminal cells"), new("\"text\"", "text", "legacy last rows")]),
        new("dashboard.snapshotIntervalMs", "Preview snapshot interval", "Minimum interval between changed styled snapshots; live mirrors share renderer frames.", SettingKind.Number,
            [new("250", "250 ms"), new("500", "500 ms"), new("1000", "1 second", "default"), new("2000", "2 seconds")]) { Min = 250, Max = 10000 },
        new("dashboard.hoverPreview", "Enlarge previews on hover", "Hover over a dashboard card for a larger read-only terminal preview. Space on a keyboard-selected card also opens it.", SettingKind.Toggle, [On, Off]),
        new("dashboard.hoverDelayMs", "Preview hover delay", "How long the pointer rests on a dashboard card before its enlarged preview appears.", SettingKind.Number,
            [new("300", "300 ms"), new("500", "500 ms", "default"), new("800", "800 ms")]) { Min = 150, Max = 3000 },
        new("profiles.source", "Profile source", "Windows Terminal configuration to import. settingsPath and OVERSHELL_WT_SETTINGS take precedence.", SettingKind.Choice,
        [
            new("\"auto\"", "auto", "Stable, then Preview, then unpackaged (default)"),
            new("\"stable\"", "stable", "Windows Terminal"),
            new("\"preview\"", "preview", "Windows Terminal Preview"),
            new("\"unpackaged\"", "unpackaged", "Unpackaged Windows Terminal"),
        ]),
        new("profiles.settingsPath", "Profile settings file", "An explicit Windows Terminal settings file; environment variables expand and relative paths use OverShell's configuration directory.", SettingKind.Text,
            [new("null", "use the selected source")]) { NullDisplay = "use the selected source", Validate = JsoncEdit.ToJson },
        new("profiles.defaultProfile", "Default profile", "Profile name or ID for new tabs; choose one from the loaded source or follow Windows Terminal.", SettingKind.Text,
            [new("null", "follow Windows Terminal")]) { NullDisplay = "follow Windows Terminal", Validate = JsoncEdit.ToJson },
        new("window.terminalOpacity", "Terminal body opacity",
            "How much the backdrop shows through the terminal's default background; 1 is opaque. Crossing 1 takes a restart.",
            SettingKind.Number,
            [
                new("1.0", "1.0", "opaque"),
                new("0.95", "0.95"),
                new("0.9", "0.9"),
                new("0.85", "0.85", "default"),
                new("0.8", "0.8"),
                new("0.75", "0.75"),
                new("0.7", "0.7"),
                new("0.6", "0.6"),
                new("0.5", "0.5"),
            ]) { Min = 0.0, Max = 1.0 },
        new("window.terminalOpacityUnfocused", "Terminal body opacity when the window is not active",
            "The body while another window is active. Same as the body opacity: an inactive window looks exactly like an active one, acrylic and all, like Windows Terminal; 1 makes it the theme's own background, opaque, so the inactive window shows as such.",
            SettingKind.Number,
            [
                new("null", "same as the body opacity", "stays translucent (default)"),
                new("1.0", "1.0", "opaque, the theme's background"),
                new("0.95", "0.95"),
                new("0.9", "0.9"),
                new("0.7", "0.7"),
            ]) { Min = 0.0, Max = 1.0, NullDisplay = "same as the body opacity" },
        new("window.backdrop", "Window backdrop",
            "The Windows 11 material behind the window - and behind a translucent terminal body.",
            SettingKind.Choice,
            [
                new("\"acrylic\"", "acrylic", "what is behind the window, blurred (default)"),
                new("\"mica\"", "mica", "the wallpaper, tinted; nothing behind the window shows"),
                new("\"micaalt\"", "micaalt", "the wallpaper, tinted - the alternate, darker material"),
                new("\"none\"", "none", "a solid window; the terminal body stays opaque"),
            ]),
        new("theme", "Theme",
            "The chrome palette: follow the Windows app mode, or dark, or light. Live.",
            SettingKind.Choice,
            [
                new("\"system\"", "system", "follow Windows (default)"),
                new("\"dark\"", "dark"),
                new("\"light\"", "light"),
            ]),
        new("accent", "Accent colour",
            "The accent of the chrome: the Windows accent, the theme's own blue, or a colour of yours (#RRGGBB). Live.",
            SettingKind.Text,
            [
                new("\"system\"", "system", "the Windows accent colour (default)"),
                new("\"palette\"", "palette", "the theme's own blue"),
            ]) { Validate = text => HexColour.IsMatch(text) ? JsoncEdit.ToJson(text.ToUpperInvariant()) : null },
        new("skin", "Skin",
            "A ResourceDictionary under skins\\ overriding the theme's brushes, fonts and metrics; colours apply live, the rest at the next start.",
            SettingKind.Choice,
            [new("null", "none", "the theme as it is (default)")])
        {
            MoreChoices = () => Directory.Exists(AppPaths.SkinsDir)
                ? Directory.EnumerateFiles(AppPaths.SkinsDir, "*.xaml").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => new SettingChoice(JsoncEdit.ToJson(n), n, "skins\\" + n + ".xaml"))
                : [],
        },
        new("view", "View at start",
            "Which view the window opens in.",
            SettingKind.Choice,
            [
                new("\"terminal\"", "terminal", "the tab strip on top, the live tab below (default)"),
                new("\"herd\"", "herd", "the tab list beside the terminal"),
                new("\"dashboard\"", "dashboard", "one card per tab"),
                new("\"zen\"", "zen", "the terminal alone"),
            ]),
        new("tabs.twoLine", "Two-line tabs", "A second line under each tab's label with its state and project.", SettingKind.Toggle, [On, Off]),
        new("tabs.aliasDisplay", "Tab alias display", "Show the runtime alias beside the label, as the primary name, or only in discovery and tooltips.", SettingKind.Choice,
            [new("\"badge\"", "badge"), new("\"primary\"", "primary"), new("\"hidden\"", "hidden")]),
        new("tabs.labelMode", "Tab labels", "Automatic tab names. Manual names take priority; duplicate names are qualified. Applies live.", SettingKind.Choice,
        [
            new("\"session\"", "session", "Session/task title first (default)"),
            new("\"project\"", "project", "Project, followed by the session/task title"),
            new("\"harness\"", "harness", "The agent or harness name"),
        ]),
        new("tabs.showHarnessGlyph", "Harness icons on tabs", "The OpenCode / Copilot / Claude / Codex icon on agent tabs.", SettingKind.Toggle, [On, Off]),
        new("attention.order", "Order of waiting tabs",
            "Which waiting tab a jump goes to.",
            SettingKind.Choice,
            [
                new("\"age\"", "age", "the one that has waited longest (default)"),
                new("\"strip\"", "strip", "the next one in tab order"),
            ]),
        new("session.restore", "Restore the session", "Reopen the last run's tabs, view and windows at start - after a crash too.", SettingKind.Toggle, [On, Off]),
        new("session.resumeAgents", "Resume agents", "Bring a restored tab's agent back, by session id when one is known.", SettingKind.Toggle, [On, Off]),
        new("session.resumeWithoutId", "Resume without a session id", "No id known: use the tool's own 'most recent session' form (opencode --continue).", SettingKind.Toggle, [On, Off]),
        new("session.restoreWindows", "Restore window placement", "Main window and tear-offs back where they were.", SettingKind.Toggle, [On, Off]),
        new("session.showPreviousScreen", "Previous screen above the prompt",
            "After a restart, what each tab showed last time, dimmed, above the new prompt.",
            SettingKind.Choice,
            [
                new("\"interrupted\"", "interrupted", "only after a crash or sign-out (default)"),
                new("\"always\"", "always"),
                new("\"never\"", "never"),
            ]),
        new("session.confirmCloseWithAgents", "Ask before closing with agents at work", "Closing the window while agents are working or waiting asks first.", SettingKind.Toggle, [On, Off]),
        new("session.restartWithWindows", "Restart with Windows", "Ask Windows to start OverShell again after a restart or sign-out.", SettingKind.Toggle, [On, Off]),
        new("detection.injectShellIntegration", "Shell integration on the command line", "A plain PowerShell profile announces its directory from the first prompt, with no profile edit.", SettingKind.Toggle, [On, Off]),
        new("detection.cwdFromProcess", "Directory from the shell process", "Follow cd in shells that do not announce their directory (cmd, bash, wrapped shells).", SettingKind.Toggle, [On, Off]),
        new("detection.treatUnknownAsAgent", "Treat every tab as an agent", "Watch every tab for agent states even when no harness is recognised.", SettingKind.Toggle, [On, Off]),
        new("compatibility.reloadEnvironmentVariables", "Environment from the registry", "Every new tab's environment as a fresh logon would have it, so a tool installed after OverShell is on PATH.", SettingKind.Toggle, [On, Off]),
        new("git.branch", "Git branch on tabs", "The branch name from .git/HEAD; no process spawned.", SettingKind.Toggle, [On, Off]),
        new("git.dirty", "Git dirty marker", "A '*' when the repository has changes - runs git status per repository.", SettingKind.Toggle, [On, Off]),
        new("endpoint.control", "Control API", "Read screens, send text, reply and open tabs over the loopback endpoint; off keeps it to the integrations' reports.", SettingKind.Toggle, [On, Off]),
        new("protocol.register", "Register overshell://", "Register the URL scheme for this user at start, so toast clicks find their tab.", SettingKind.Toggle, [On, Off]),
        new("keys.sequenceTimeoutMs", "Key sequence timeout",
            "How long a key sequence waits for its next chord, in milliseconds.",
            SettingKind.Number,
            [new("1500", "1500"), new("3000", "3000", "default"), new("5000", "5000"), new("10000", "10000")]) { Min = 200, Max = 60000 },
        new("extensions.herd.mode.hints", "Herd mode hint bar", "The bar listing the keys while a herd-mode sequence is pending.", SettingKind.Toggle, [On, Off]) { Segments = ["extensions", "herd.mode", "hints"] },
        new("extensions.herd.mode.leader", "Herd mode leader key",
            "The chord that starts a herd-mode sequence.",
            SettingKind.Text,
            [new("\"ctrl+shift+k\"", "ctrl+shift+k", "default")]) { Validate = Chord, Segments = ["extensions", "herd.mode", "leader"] },
        new("extensions.tabs.mru.switcherMode", "Ctrl+Tab order",
            "What Ctrl+Tab walks. Takes effect at the next start (the keys are bound then).",
            SettingKind.Choice,
            [
                new("\"inOrder\"", "inOrder", "the strip, left to right; Ctrl+Shift+Tab goes left; wraps (default)"),
                new("\"mru\"", "mru", "most recently used first, with the overlay: hold Ctrl, tap Tab, release to land"),
            ]) { Segments = ["extensions", "tabs.mru", "switcherMode"] },
        new("extensions.summon.keys", "Summon key",
            "The global hotkey that brings OverShell to the front from anywhere.",
            SettingKind.Text,
            [new("\"win+backtick\"", "win+backtick", "default"), new("\"ctrl+alt+backtick\"", "ctrl+alt+backtick"), new("\"ctrl+shift+f12\"", "ctrl+shift+f12")]) { Validate = Chord },
        new("extensions.summon.toggle", "Summon toggles", "Pressing the summon key while OverShell is in front minimizes it.", SettingKind.Toggle, [On, Off]),
        new("extensions.summon.to", "Summon lands on",
            "Which tab the window shows when summoned.",
            SettingKind.Choice,
            [
                new("\"attention\"", "attention", "the tab that has waited longest (default)"),
                new("\"current\"", "current", "whatever was active"),
            ]),
        new("notifications.sinks.overlay.enabled", "In-window toasts", "A toast over the terminal when a background tab needs you.", SettingKind.Toggle, [On, Off]),
        new("notifications.sinks.taskbar.enabled", "Taskbar badge", "The number of waiting tabs on the taskbar icon, and a flash when the window is not in front.", SettingKind.Toggle, [On, Off]),
        new("notifications.sinks.sound.enabled", "Sounds", "System sounds while you are away from the window.", SettingKind.Toggle, [On, Off]),
        new("notifications.sinks.toast.enabled", "Windows toasts", "Action Center toasts; a click focuses the tab. Off by default in favour of Palantir.", SettingKind.Toggle, [On, Off]),
        new("notifications.sinks.palantir.enabled", "Palantir toasts", "Windows toasts through Palantir, when it is installed.", SettingKind.Toggle, [On, Off]),
    ];

    /// <summary>The knob at a path, if the catalog has it.</summary>
    public static SettingKnob? Find(string path) =>
        Knobs.FirstOrDefault(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase));

    private static string? Chord(string text) => KeyChord.TryParse(text, out _) ? JsoncEdit.ToJson(text.ToLowerInvariant()) : null;
}
