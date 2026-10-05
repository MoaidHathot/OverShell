using System.ComponentModel;
using OverShell.Core.Agents;
using OverShell.Core.Commands;
using OverShell.Core.Input;
using OverShell.Core.Notifications;
using OverShell.Core.Settings;

namespace OverShell.Core.Extensibility;

/// <summary>
/// A feature built on top of the shell rather than into it (DESIGN.md §12.16). The shell
/// is the engine - tabs, agents, commands, keys, settings, notifications; an extension
/// uses it through <see cref="IShell"/> and adds commands, keys and behaviour. The
/// built-in features of §12.16 onwards are extensions; a script host can load more.
/// </summary>
public interface IExtension : IDisposable
{
    /// <summary>Stable id, lower-case dotted (<c>herd.mode</c>): the trace prefix and the key under <c>settings.extensions</c>.</summary>
    string Id { get; }

    /// <summary>Called once, on the UI thread, after the window and its commands exist and before the session is restored.</summary>
    void Initialize(IShell shell);
}

/// <summary>What an extension sees of the running shell. Everything here is UI-thread unless noted.</summary>
public interface IShell
{
    IReadOnlyList<ITab> Tabs { get; }

    ITab? ActiveTab { get; }

    /// <summary>The tab a command aims at: the tear-off's when the chord came from one, else the active tab.</summary>
    ITab? TargetTab { get; }

    ITab? Find(string tabId);

    event Action<ITab>? TabOpened;

    event Action<ITab>? TabClosed;

    event Action<ITab?>? ActiveTabChanged;

    event Action<ITab, AgentTransition>? TabStateChanged;

    event Action<ITab, AgentAttention>? TabAttention;

    /// <summary>Every 500 ms on the UI thread - the shell's own heartbeat, so extensions need no timers.</summary>
    event Action<DateTimeOffset>? Heartbeat;

    /// <summary>settings.jsonc, agent rules or keybindings reloaded; <see cref="Settings"/> is the new object.</summary>
    event Action? SettingsChanged;

    CommandRegistry Commands { get; }

    IKeyBindings Keys { get; }

    AppSettings Settings { get; }

    /// <summary>
    /// This extension's section of settings.jsonc - <c>"extensions": { "&lt;id&gt;": { ... } }</c> -
    /// bound to <typeparamref name="T"/>; a fresh <typeparamref name="T"/> when absent or broken.
    /// </summary>
    T ExtensionSettings<T>(string extensionId) where T : class, new();

    IHostUi Ui { get; }

    ITab? OpenTab(TabRequest request);

    void Activate(ITab tab);

    void Close(ITab tab);

    /// <summary>The agents trace (<c>OVERSHELL_TRACE_AGENTS=1</c>).</summary>
    void Trace(string message);

    /// <summary>Runs on the UI thread, later.</summary>
    void Post(Action action);
}

/// <summary>One tab, as an extension sees it. Properties change on the UI thread with <see cref="INotifyPropertyChanged"/>.</summary>
public interface ITab : INotifyPropertyChanged
{
    string Id { get; }

    /// <summary>The user's label, else the harness, else the shell's title.</summary>
    string Label { get; }

    string Title { get; }

    /// <summary>Rule-set id of the harness (<c>opencode</c>), or null for a plain shell.</summary>
    string? Harness { get; }

    bool IsAgent { get; }

    AgentRuleSet Rules { get; }

    AgentState State { get; }

    bool Unread { get; }

    bool NeedsAttention { get; }

    /// <summary>When the current attention-worthy state began; null when none.</summary>
    DateTimeOffset? AttentionSince { get; }

    DateTimeOffset? LastActivity { get; }

    string? WorkingDirectory { get; }

    /// <summary>Repository or directory name.</summary>
    string Project { get; }

    string? Group { get; }

    bool Detached { get; }

    bool IsRunning { get; }

    bool IsActive { get; }

    /// <summary>The evidence behind the state, one line.</summary>
    string Explain { get; }

    string? Summary { get; }

    string? SessionId { get; }

    string? ResumeCommand { get; }

    /// <summary>The viewport as last read; <see cref="RequestScreen"/> refreshes it.</summary>
    IReadOnlyList<string> ScreenRows { get; }

    void RequestScreen();

    /// <summary>Types text into the tab as if from the keyboard.</summary>
    void SendText(string text);

    /// <summary>Pastes text (bracketed when the application asked for it) without a trailing Enter.</summary>
    void Paste(string text);

    /// <summary>
    /// Per-tab extension state, saved with the session and restored with the tab - a mute
    /// flag, a watch pattern. Keys are the extension's own; keep them prefixed by its id.
    /// </summary>
    IDictionary<string, string> Properties { get; }
}

/// <summary>Key bindings as an extension sees them.</summary>
public interface IKeyBindings
{
    /// <summary>The keys shown next to a command (menus, palette, hint bars); null when none are bound.</summary>
    string? HintFor(string commandId);

    /// <summary>
    /// Default bindings this extension brings - applied beneath the user's keybindings.jsonc,
    /// so a user entry for the same keys wins and <c>unbound</c> removes them. Call during
    /// <see cref="IExtension.Initialize"/>; the map is rebuilt when initialization ends.
    /// </summary>
    void AddDefaults(IEnumerable<Keybinding> bindings);

    KeybindingMap Map { get; }

    /// <summary>The chords of a key sequence pressed so far; empty when none is pending.</summary>
    IReadOnlyList<KeyChord> Pending { get; }

    /// <summary>The pending sequence changed: a leader chord went down, a sequence completed, was rejected, cancelled or timed out.</summary>
    event Action<IReadOnlyList<KeyChord>>? PendingChanged;

    /// <summary>The Ctrl key went up anywhere in our windows - what a hold-and-release switcher commits on.</summary>
    event Action? ControlReleased;

    /// <summary>
    /// A chord the extension wants to see before the key map does, while <paramref name="active"/>
    /// says so: return true to take it. For a switcher that owns Tab while it is open.
    /// </summary>
    void Intercept(Func<bool> active, Func<KeyChord, bool> handler);
}

/// <summary>A new tab.</summary>
/// <param name="Profile">Profile id or name; the default profile when null or unknown.</param>
/// <param name="WorkingDirectory">Starting directory; the profile's own when null.</param>
/// <param name="Label">A label to set.</param>
/// <param name="Group">A group to join.</param>
/// <param name="Command">A command typed once the shell shows its prompt.</param>
/// <param name="Activate">Whether the new tab becomes active.</param>
public sealed record TabRequest(
    string? Profile = null,
    string? WorkingDirectory = null,
    string? Label = null,
    string? Group = null,
    string? Command = null,
    bool Activate = true);

/// <summary>A row in a picker.</summary>
public sealed record PickItem(
    string Title,
    string? Detail = null,
    string? Hint = null,
    string? Glyph = null,
    string? Keywords = null,
    Func<string?>? Preview = null,
    object? Tag = null);

/// <summary>
/// The host's user interface, as far as an extension may drive it: the status line,
/// notifications, the three kinds of owned prompt windows, and a slot for a bar of its
/// own under the terminal. Anything beyond that an extension builds itself against
/// <see cref="Host"/>.
/// </summary>
public interface IHostUi
{
    /// <summary>The host's main window object - a WPF <c>Window</c> in the WPF host - for owning windows. Null in a headless host.</summary>
    object? Host { get; }

    /// <summary>The element the terminal body fills (a WPF element in the WPF host) - what an overlay centres on. Null in a headless host.</summary>
    object? TerminalArea { get; }

    /// <summary>A line in the status bar for a few seconds; a toast when the layout has no status bar.</summary>
    void Status(string message);

    /// <summary>A notification through the configured sinks, attributed to a tab when given.</summary>
    void Notify(NotificationKind kind, string title, string message, string? tabId = null);

    /// <summary>One-line text entry; null when dismissed.</summary>
    Task<string?> PromptAsync(string caption, string initial = "");

    /// <summary>A fuzzy-searchable list; the chosen item, or null when dismissed.</summary>
    Task<PickItem?> PickAsync(string glyph, string emptyText, IReadOnlyList<PickItem> items);

    /// <summary>A question with fixed answers; the index chosen, or -1 when dismissed.</summary>
    Task<int> AskAsync(string question, IReadOnlyList<string> answers);

    /// <summary>Shows a bar of the host's element type under the terminal (a WPF element in the WPF host); the same object again replaces nothing.</summary>
    void ShowBar(object bar);

    void HideBar(object bar);
}
