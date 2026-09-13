using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.App.Agents;
using OverShell.App.Chrome;
using OverShell.App.Diagnostics;
using OverShell.App.Notifications;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Commands;
using OverShell.Core.Input;
using OverShell.Core.Integrations;
using OverShell.Core.Notifications;
using OverShell.Core.Search;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// The herd-overseer side of the window (DESIGN.md §12): commands and keybindings, the
/// palette, the integration endpoint, agent detection plumbing and notifications.
/// </summary>
public partial class MainWindow
{
    private readonly CommandRegistry _commands = new();
    private readonly Dictionary<(Key Key, ModifierKeys Modifiers), string> _chords = [];
    private readonly Dictionary<string, TerminalTab> _tabsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly TraceLog _trace = TraceLog.Agents;
    private AppSettings _settings = null!;
    private AgentRules _rules = null!;
    private PersistedState _state = null!;
    private KeybindingMap _keybindings = null!;
    private AgentServices _agents = null!;
    private IntegrationEndpoint? _endpoint;
    private NotificationPipeline? _notifications;
    private PaletteWindow? _palette;
    private (int Working, int Blocked, int Done, int Error) _counts;

    /// <summary>For in-process diagnostics (<see cref="Diagnostics.HerdSelfTest"/>): the live pipeline, or null before Loaded.</summary>
    internal NotificationPipeline? Notifications => _notifications;

    internal CommandRegistry Commands => _commands;

    internal IntegrationEndpoint? Endpoint => _endpoint;

    /// <summary>Loads configuration and starts the endpoint. Runs before any tab exists.</summary>
    private void InitializeHerd()
    {
        AppPaths.EnsureCreated();

        _settings = AppSettings.Load(System.IO.File.Exists(AppPaths.SettingsFile) ? AppPaths.SettingsFile : null);
        foreach (var problem in _settings.Problems)
        {
            _trace.Write($"settings: {problem}");
        }

        _state = PersistedState.Load(AppPaths.StateFile);

        _rules = AgentRules.Load(AppPaths.AgentsDir);
        foreach (var problem in _rules.Problems)
        {
            _trace.Write($"agent rules: {problem}");
        }

        try
        {
            var endpoint = new IntegrationEndpoint();
            endpoint.Start();
            endpoint.Trace += line => _trace.Write($"endpoint {line}");
            endpoint.ReportReceived += report => Dispatcher.BeginInvoke(() => OnReport(report), DispatcherPriority.Normal);
            endpoint.TabsProvider = () =>
            {
                try
                {
                    return Dispatcher.Invoke<IReadOnlyList<TabSummary>>(
                        () => Tabs.Select(t => t.Summarize()).ToList(),
                        DispatcherPriority.Normal,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    return [];
                }
            };
            _endpoint = endpoint;
            _trace.Write($"endpoint listening at {endpoint.BaseUrl}");
        }
        catch (Exception e) when (e is InvalidOperationException or System.Net.HttpListenerException)
        {
            // No endpoint means no integrations this run; the detector still works.
            _trace.Write($"endpoint failed to start: {e.Message}");
        }

        _agents = new AgentServices
        {
            Rules = _rules,
            Detection = _settings.Detection,
            EnvironmentFor = _endpoint is { } ep ? ep.EnvironmentFor : null,
        };
    }

    /// <summary>Needs the visual tree: the toast layer anchors to the middle of the window.</summary>
    private void InitializeNotifications()
    {
        _notifications = new NotificationPipeline(this, MainHost, FocusTabById, _settings.Notifications);
        _trace.Write($"notification sinks: {string.Join(", ", _notifications.ActiveSinks)}");
    }

    // ------------------------------------------------------------- commands

    private void RegisterCommands()
    {
        var c = _commands;

        c.Register("tab.new", "New tab", "Tabs", () => NewTab(_catalog.DefaultProfile), description: "Open the default profile");
        c.Register("tab.close", "Close tab", "Tabs", () => { if (ActiveTab is { } t) CloseTab(t); }, () => ActiveTab is not null);
        c.Register("tab.next", "Next tab", "Tabs", () => ActivateRelative(1), () => Tabs.Count > 1);
        c.Register("tab.previous", "Previous tab", "Tabs", () => ActivateRelative(-1), () => Tabs.Count > 1);
        c.Register("tab.jumpToAttention", "Jump to the tab that needs you", "Tabs", JumpToAttention, description: "Blocked first, then finished-unseen");
        c.Register("tab.rename", "Rename tab", "Tabs", RenameActiveTab, () => ActiveTab is not null);
        c.Register("tab.markAgent", "Treat this tab as an agent", "Agents", () => ActiveTab?.MarkAsAgent(), () => ActiveTab is { IsAgent: false });
        c.Register("tab.markShell", "Treat this tab as a shell", "Agents", () => ActiveTab?.MarkAsShell(), () => ActiveTab is { IsAgent: true });
        c.Register("tab.explain", "Explain this tab's state", "Agents", ExplainActiveTab, () => ActiveTab is not null);

        for (var i = 1; i <= 9; i++)
        {
            var index = i - 1;
            c.Register($"tab.switchTo.{i}", $"Switch to tab {i}", "Tabs", () => { if (index < Tabs.Count) ActiveTab = Tabs[index]; }, () => index < Tabs.Count);
        }

        c.Register("palette.commands", "Command palette", "Palette", () => OpenPalette(">"));
        c.Register("palette.tabs", "Switch tab…", "Palette", () => OpenPalette(string.Empty));

        c.Register("clipboard.copy", "Copy selection", "Clipboard", () => Copy());
        c.Register("clipboard.copyIfSelection", "Copy selection, else send to shell", "Clipboard", Copy);
        c.Register("clipboard.paste", "Paste", "Clipboard", Paste);

        foreach (var id in IntegrationInstaller.Ids)
        {
            var integration = id;
            c.Register($"integrations.install.{id}", $"Integrations: install {id}", "Integrations", () => RunIntegration(integration, install: true),
                description: id == "opencode" ? "Writes the OverShell plugin into OpenCode's plugins folder" : "Writes hooks into ~/.copilot/hooks");
            c.Register($"integrations.uninstall.{id}", $"Integrations: uninstall {id}", "Integrations", () => RunIntegration(integration, install: false));
        }

        c.Register("integrations.status", "Integrations: status", "Integrations", ShowIntegrationStatus, description: "Which harness integrations are installed, and the endpoint address");
        c.Register("settings.open", "Open settings folder", "Settings", () => OpenFolder(AppPaths.ConfigRoot), description: AppPaths.ConfigRoot);
    }

    private void LoadKeybindings()
    {
        _keybindings = KeybindingMap.Load(System.IO.File.Exists(AppPaths.KeybindingsFile) ? AppPaths.KeybindingsFile : null);
        foreach (var problem in _keybindings.Problems)
        {
            _trace.Write($"keybindings: {problem}");
        }

        // Resolve chord names to WPF keys once; Key has aliases (Return/Enter, Next/PageDown)
        // so comparing enum values rather than names is what makes both spellings work.
        _chords.Clear();
        foreach (var (chord, command) in _keybindings.Bindings)
        {
            if (Enum.TryParse<Key>(chord.Key, ignoreCase: true, out var key))
            {
                _chords[(key, ToModifiers(chord.Modifiers))] = command;
            }
            else
            {
                _trace.Write($"keybindings: '{chord}' names no WPF key");
            }
        }
    }

    private static ModifierKeys ToModifiers(ChordModifiers m)
    {
        var result = ModifierKeys.None;
        if (m.HasFlag(ChordModifiers.Control)) result |= ModifierKeys.Control;
        if (m.HasFlag(ChordModifiers.Shift)) result |= ModifierKeys.Shift;
        if (m.HasFlag(ChordModifiers.Alt)) result |= ModifierKeys.Alt;
        if (m.HasFlag(ChordModifiers.Win)) result |= ModifierKeys.Windows;
        return result;
    }

    /// <summary>The router saw a chord: run the bound command; swallow the key only when the command took it.</summary>
    private bool OnChord(Key key, ModifierKeys modifiers) =>
        _chords.TryGetValue((key, modifiers), out var command) && _commands.TryExecute(command);

    /// <summary>What the router would do with a chord — for in-process diagnostics, which inject no keys.</summary>
    internal bool DispatchChord(Key key, ModifierKeys modifiers) => OnChord(key, modifiers);

    internal PaletteWindow? Palette => _palette;

    private string? HintFor(string commandId) => _keybindings.FirstChordFor(commandId);

    // -------------------------------------------------------------- palette

    private void OpenPalette(string initial)
    {
        if (_palette is { IsVisible: true })
        {
            _palette.Close();
            return;
        }

        // The switcher previews screens: read every tab once now, then once a second while open.
        foreach (var tab in Tabs)
        {
            tab.ScreenWatched = true;
            tab.RequestScreen();
        }

        _palette = PaletteWindow.Show(this, initial, BuildPaletteItems);
        _palette.Closed += (_, _) =>
        {
            _palette = null;
            var dashboard = _settings.ViewFor(_viewId).Content == ViewContent.Dashboard;
            foreach (var tab in Tabs)
            {
                tab.ScreenWatched = dashboard;
            }

            if (!dashboard)
            {
                ActiveTab?.Surface.Focus();
            }
        };
    }

    private IReadOnlyList<PaletteItem> BuildPaletteItems(PaletteQuery query)
    {
        var scored = new List<(int Score, int Order, PaletteItem Item)>();
        var order = 0;

        if (query.CommandsMode)
        {
            foreach (var command in _commands.All.OrderBy(c => c.Category).ThenBy(c => c.Title))
            {
                if (!command.IsEnabled)
                {
                    continue;
                }

                var score = FuzzyMatcher.Score(query.Text, command.Title, command.Category, command.Id);
                if (score is null)
                {
                    continue;
                }

                var captured = command;
                scored.Add((score.Value, order++, new PaletteItem
                {
                    Title = command.Title,
                    Detail = command.Description ?? command.Category,
                    Hint = HintFor(command.Id),
                    Glyph = "\u203A",
                    Invoke = () => _commands.TryExecute(captured.Id),
                }));
            }
        }
        else
        {
            for (var i = 0; i < Tabs.Count; i++)
            {
                var tab = Tabs[i];
                if (query.StateFilter is { } state && !tab.StateText.StartsWith(state, StringComparison.OrdinalIgnoreCase) && !tab.State.ToString().StartsWith(state, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (query.ProjectFilter is { } project && !tab.Project.Contains(project, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score = FuzzyMatcher.Score(query.Text, tab.Label, tab.Title, tab.Project, tab.WorkingDirectory, tab.Harness, tab.StateText);
                if (score is null)
                {
                    continue;
                }

                // Attention first when the query is empty; otherwise the match decides.
                var boost = query.Text.Length == 0 ? (tab.State is AgentState.Blocked or AgentState.Error ? 200 : tab.State == AgentState.Done && tab.Unread ? 100 : 0) : 0;
                var captured = tab;
                scored.Add((score.Value + boost, order++, new PaletteItem
                {
                    Title = tab.Label,
                    Detail = string.IsNullOrEmpty(tab.Detail) ? tab.WorkingDirectory : $"{tab.Detail}  ·  {tab.WorkingDirectory}",
                    Hint = i < 9 ? $"Alt+{i + 1}" : null,
                    Dot = tab.StateBrush,
                    Preview = () => captured.ScreenText,
                    Invoke = () => ActiveTab = captured,
                }));
            }
        }

        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Select(s => s.Item).ToList();
    }

    private void RenameActiveTab()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        _palette?.Close();
        _palette = PaletteWindow.Prompt(this, "Tab name — empty restores the automatic label", tab.UserLabel ?? string.Empty, text =>
        {
            tab.UserLabel = text;
            if (!_state.SetLabel(tab.Profile.Id, tab.WorkingDirectory, text))
            {
                _trace.Write("state: could not save labels");
            }
        });
        _palette.Closed += (_, _) =>
        {
            _palette = null;
            ActiveTab?.Surface.Focus();
        };
    }

    private void ExplainActiveTab()
    {
        if (ActiveTab is { } tab)
        {
            ShowStatusMessage($"{tab.Label}: {tab.State} — {tab.Agent.Explain} ({tab.Agent.Authority}{(tab.Harness is null ? string.Empty : ", " + tab.Harness)})");
        }
    }

    // --------------------------------------------------------- integrations

    private void RunIntegration(string id, bool install)
    {
        try
        {
            var status = install ? IntegrationInstaller.Install(id) : IntegrationInstaller.Uninstall(id);
            ShowStatusMessage(install
                ? $"{id}: installed {status.Path}{(status.HarnessFound ? string.Empty : " (harness not found on PATH)")}"
                : $"{id}: removed {status.Path}");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowStatusMessage($"{id}: {e.Message}");
        }
    }

    private void ShowIntegrationStatus()
    {
        var parts = IntegrationInstaller.Ids.Select(id =>
        {
            var s = IntegrationInstaller.Status(id);
            return $"{id}: {(s.Installed ? s.Current ? "installed" : "installed (outdated)" : "not installed")}{(s.HarnessFound ? string.Empty : ", harness not on PATH")}";
        });
        ShowStatusMessage($"{string.Join("  ·  ", parts)}  ·  endpoint {(_endpoint is { } ep ? ep.BaseUrl : "off")}");
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        {
            // Explorer refused; nothing to add.
        }
    }

    // ---------------------------------------------------------------- agents

    private void OnReport(IntegrationReport report)
    {
        if (_tabsById.TryGetValue(report.TabId, out var tab))
        {
            tab.ApplyReport(report);
        }
        else
        {
            _trace.Write($"report for unknown tab '{report.TabId}' from {report.Source} dropped");
        }
    }

    private void OnAttention(TerminalTab tab, AgentAttention attention)
    {
        if (_notifications is null)
        {
            return;
        }

        var kind = NotificationEvent.KindFor(attention.State);
        var message = attention.Message ?? kind switch
        {
            NotificationKind.Blocked => "Needs your input",
            NotificationKind.Error => "Reported an error",
            NotificationKind.Exited => "Process exited",
            _ => "Finished",
        };

        var e = new NotificationEvent(
            kind,
            tab.Id,
            tab.Label,
            tab.Harness,
            tab.IsAgent ? tab.Agent.Rules.DisplayName : null,
            tab.Project,
            tab.WorkingDirectory,
            message,
            attention.Message is null ? null : attention.Reason,
            attention.At,
            TabIsActive: ReferenceEquals(tab, ActiveTab),
            WindowIsFocused: IsActive);

        _notifications.Publish(e);
        RefreshAttention();
    }

    /// <summary>Recomputes the status-bar counts and the taskbar badge. Cheap; called from the heartbeat.</summary>
    private void RefreshAttention()
    {
        var counts = (Working: 0, Blocked: 0, Done: 0, Error: 0);
        foreach (var tab in Tabs)
        {
            switch (tab.State)
            {
                case AgentState.Working: counts.Working++; break;
                case AgentState.Blocked: counts.Blocked++; break;
                case AgentState.Error: counts.Error++; break;
                case AgentState.Done when tab.Unread: counts.Done++; break;
            }
        }

        if (counts == _counts)
        {
            return;
        }

        _counts = counts;

        var parts = new List<string>(3);
        if (counts.Working > 0) parts.Add($"\u25CF {counts.Working} working");
        if (counts.Blocked > 0) parts.Add($"\u25B2 {counts.Blocked} need{(counts.Blocked == 1 ? "s" : string.Empty)} you");
        if (counts.Error > 0) parts.Add($"\u2716 {counts.Error} error{(counts.Error == 1 ? string.Empty : "s")}");
        if (counts.Done > 0) parts.Add($"\u2713 {counts.Done} done");
        TxtAttention.Text = string.Join("   ", parts);
        TxtAttention.Foreground = (Brush)FindResource(counts.Blocked > 0 || counts.Error > 0 ? "State.Blocked" : counts.Done > 0 ? "State.Done" : "Text.Disabled");

        _notifications?.UpdateBadge(counts.Blocked + counts.Error + counts.Done, counts.Blocked > 0, counts.Error > 0);
    }

    /// <summary>Blocked or errored first (after the current tab, wrapping), then finished-unseen.</summary>
    private void JumpToAttention()
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        var start = ActiveTab is { } active ? Tabs.IndexOf(active) : -1;
        TerminalTab? Find(Func<TerminalTab, bool> predicate)
        {
            for (var step = 1; step <= Tabs.Count; step++)
            {
                var candidate = Tabs[(start + step) % Tabs.Count];
                if (predicate(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        var target = Find(t => t.State is AgentState.Blocked or AgentState.Error) ?? Find(t => t.State == AgentState.Done && t.Unread);
        if (target is not null)
        {
            ActiveTab = target;
        }
        else
        {
            ShowStatusMessage("Nothing needs you");
        }
    }

    private void FocusTabById(string tabId)
    {
        if (_tabsById.TryGetValue(tabId, out var tab))
        {
            ActiveTab = tab;
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }
    }

    /// <summary>Whether the user can see the active tab: it is showing and the window has the foreground.</summary>
    private void UpdateViewed()
    {
        foreach (var tab in Tabs)
        {
            var viewed = ReferenceEquals(tab, ActiveTab) && IsActive;
            tab.SetViewed(viewed);
            if (viewed)
            {
                _notifications?.Viewed(tab.Id);
            }
        }

        RefreshAttention();
    }

    // ---------------------------------------------------------------- status

    private DispatcherTimer? _statusMessageTimer;

    /// <summary>Shows a line in the status bar's detail slot for a few seconds, then restores the working directory.</summary>
    private void ShowStatusMessage(string message)
    {
        TxtMessage.Text = message;

        _statusMessageTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
        _statusMessageTimer.Stop();
        _statusMessageTimer.Tick -= OnStatusMessageExpired;
        _statusMessageTimer.Tick += OnStatusMessageExpired;
        _statusMessageTimer.Start();
        UpdateDetailSlot();
        _trace.Write($"status: {message}");
    }

    private void OnStatusMessageExpired(object? sender, EventArgs e)
    {
        _statusMessageTimer?.Stop();
        UpdateDetailSlot();
    }

    /// <summary>The status bar's middle slot shows one thing: a hovered link, else a message, else the working directory.</summary>
    private void UpdateDetailSlot()
    {
        var link = _hoverLink is not null;
        var message = _statusMessageTimer?.IsEnabled == true;
        LinkHintPanel.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
        TxtMessage.Visibility = !link && message ? Visibility.Visible : Visibility.Collapsed;
        TxtDetail.Visibility = !link && !message ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Attention_Click(object sender, MouseButtonEventArgs e)
    {
        JumpToAttention();
        e.Handled = true;
    }
}
