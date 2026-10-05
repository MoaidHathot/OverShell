using System.Windows;
using System.Windows.Threading;
using OverShell.Core.Agents;
using OverShell.Core.Commands;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;
using OverShell.Core.Notifications;
using OverShell.Core.Settings;
using OverShell.App.Chrome;
using OverShell.Core.Search;

namespace OverShell.App.Extensibility;

/// <summary>
/// The window as extensions see it (DESIGN.md §12.16): <see cref="IShell"/> over
/// <see cref="MainWindow"/>. Everything an extension may do goes through here, so what the
/// window exposes to features is one deliberate surface rather than its internals - and
/// so a script host can offer the same surface later.
/// </summary>
internal sealed class ShellHost : IShell, IKeyBindings, IHostUi
{
    private readonly MainWindow _window;
    private readonly List<Keybinding> _extensionDefaults = [];

    public ShellHost(MainWindow window)
    {
        _window = window;
    }

    // ----------------------------------------------------------------- IShell

    public IReadOnlyList<ITab> Tabs => _window.Tabs;

    public ITab? ActiveTab => _window.ActiveTab;

    public ITab? TargetTab => _window.TargetTab;

    public ITab? Find(string tabId) => _window.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.OrdinalIgnoreCase));

    public event Action<ITab>? TabOpened;

    public event Action<ITab>? TabClosed;

    public event Action<ITab?>? ActiveTabChanged;

    public event Action<ITab, AgentTransition>? TabStateChanged;

    public event Action<ITab, AgentAttention>? TabAttention;

    public event Action<DateTimeOffset>? Heartbeat;

    public event Action? SettingsChanged;

    public CommandRegistry Commands => _window.Commands;

    public IKeyBindings Keys => this;

    public AppSettings Settings => _window.CurrentSettings;

    public T ExtensionSettings<T>(string extensionId) where T : class, new()
    {
        var bound = Settings.ExtensionSettings<T>(extensionId, out var problem);
        if (problem is not null)
        {
            Trace($"settings: extensions.{extensionId}: {problem}; using defaults");
        }

        return bound;
    }

    public IHostUi Ui => this;

    public ITab? OpenTab(TabRequest request) => _window.OpenTabFor(request);

    public void Activate(ITab tab)
    {
        if (tab is TerminalTab t)
        {
            _window.ActiveTab = t;
        }
    }

    public void Close(ITab tab)
    {
        if (tab is TerminalTab t)
        {
            _window.CloseTab(t);
        }
    }

    public void Trace(string message) => _window.Trace.Write(message);

    public void Post(Action action) => _window.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    // ------------------------------------------------------- IKeyBindings

    public string? HintFor(string commandId) => _window.HintFor(commandId);

    public void AddDefaults(IEnumerable<Keybinding> bindings) => _extensionDefaults.AddRange(bindings);

    public KeybindingMap Map => _window.Keybindings;

    public IReadOnlyList<KeyChord> Pending => _window.PendingChords;

    public event Action<IReadOnlyList<KeyChord>>? PendingChanged;

    internal void RaisePendingChanged(IReadOnlyList<KeyChord> pending) => PendingChanged?.Invoke(pending);

    public event Action? ControlReleased;

    internal void RaiseControlReleased() => ControlReleased?.Invoke();

    private readonly List<(Func<bool> Active, Func<KeyChord, bool> Handler)> _interceptors = [];

    public void Intercept(Func<bool> active, Func<KeyChord, bool> handler) => _interceptors.Add((active, handler));

    /// <summary>Offers a chord to the active interceptors; true when one took it.</summary>
    internal bool TryIntercept(KeyChord chord)
    {
        foreach (var (active, handler) in _interceptors)
        {
            if (active() && handler(chord))
            {
                return true;
            }
        }

        return false;
    }
    /// <summary>What extensions declared, for the map to layer beneath the user's file.</summary>
    internal IReadOnlyList<Keybinding> ExtensionDefaults => _extensionDefaults;

    // ------------------------------------------------------------ IHostUi

    public object? Host => _window;

    public object? TerminalArea => _window.TerminalArea;

    public void Status(string message) => _window.ShowStatusMessage(message);

    public void Notify(NotificationKind kind, string title, string message, string? tabId = null)
    {
        var tab = tabId is null ? null : _window.Tabs.FirstOrDefault(t => t.Id == tabId);
        _window.Notifications?.Publish(new NotificationEvent(
            kind,
            tab?.Id ?? string.Empty,
            tab?.Label ?? title,
            tab?.Harness,
            tab?.IsAgent == true ? tab.Rules.DisplayName : null,
            tab?.Project,
            tab?.WorkingDirectory,
            message,
            title,
            DateTimeOffset.Now,
            tab?.IsActive ?? false,
            _window.IsActive));
    }

    public Task<string?> PromptAsync(string caption, string initial = "")
    {
        var completion = new TaskCompletionSource<string?>();
        var accepted = false;
        var palette = PaletteWindow.Prompt(_window, caption, initial, text =>
        {
            accepted = true;
            completion.TrySetResult(text);
        });
        palette.Closed += (_, _) =>
        {
            if (!accepted)
            {
                completion.TrySetResult(null);
            }

            _window.ActiveTab?.Surface.Focus();
        };
        return completion.Task;
    }

    public Task<PickItem?> PickAsync(string glyph, string emptyText, IReadOnlyList<PickItem> items)
    {
        var completion = new TaskCompletionSource<PickItem?>();
        var rows = items.Select(item => new PaletteItem
        {
            Title = item.Title,
            Detail = item.Detail,
            Hint = item.Hint,
            Glyph = item.Glyph,
            Keywords = item.Keywords,
            Preview = item.Preview,
            Invoke = () => completion.TrySetResult(item),
        }).ToList();

        var palette = PaletteWindow.Picker(_window, glyph, emptyText, query => Filter(rows, query));
        palette.Closed += (_, _) =>
        {
            completion.TrySetResult(null);
            _window.ActiveTab?.Surface.Focus();
        };
        return completion.Task;
    }

    public Task<int> AskAsync(string question, IReadOnlyList<string> answers)
    {
        var completion = new TaskCompletionSource<int>();
        var rows = answers.Select((answer, index) => new PaletteItem { Title = answer, Invoke = () => completion.TrySetResult(index) }).ToList();
        var palette = PaletteWindow.Ask(_window, "\uE9CE", question, rows);
        palette.Closed += (_, _) =>
        {
            completion.TrySetResult(-1);
            _window.ActiveTab?.Surface.Focus();
        };
        return completion.Task;
    }

    public void ShowBar(object bar)
    {
        if (bar is FrameworkElement element && !_window.Bars.Children.Contains(element))
        {
            _window.Bars.Children.Add(element);
        }
    }

    public void HideBar(object bar)
    {
        if (bar is FrameworkElement element)
        {
            _window.Bars.Children.Remove(element);
        }
    }

    private static IReadOnlyList<PaletteItem> Filter(IReadOnlyList<PaletteItem> rows, PaletteQuery query)
    {
        var text = query.Text.Trim();
        if (text.Length == 0)
        {
            return rows;
        }

        return rows
            .Select(row => (Row: row, Score: Core.Search.FuzzyMatcher.Score(text, row.Title, row.Detail, row.Keywords)))
            .Where(r => r.Score is not null)
            .OrderByDescending(r => r.Score!.Value)
            .Select(r => r.Row)
            .ToList();
    }

    // ------------------------------------------------------------- raising

    internal void RaiseTabOpened(TerminalTab tab) => TabOpened?.Invoke(tab);

    internal void RaiseTabClosed(TerminalTab tab) => TabClosed?.Invoke(tab);

    internal void RaiseActiveTabChanged(TerminalTab? tab) => ActiveTabChanged?.Invoke(tab);

    internal void RaiseStateChanged(TerminalTab tab, AgentTransition transition) => TabStateChanged?.Invoke(tab, transition);

    internal void RaiseAttention(TerminalTab tab, AgentAttention attention) => TabAttention?.Invoke(tab, attention);

    internal void RaiseHeartbeat(DateTimeOffset now) => Heartbeat?.Invoke(now);

    internal void RaiseSettingsChanged() => SettingsChanged?.Invoke();
}
