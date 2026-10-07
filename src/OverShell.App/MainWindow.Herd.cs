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
using OverShell.Core.Herd;
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
        TerminalTab.TabSettings = _settings.Tabs;

        // The terminal body's rendering mode is decided once, before the first tab (12.20): a
        // translucent body needs the composition path and a backdrop to show through. With
        // transparency effects off in Windows, or no backdrop asked for, it stays opaque.
        WindowChromeInterop.ConfiguredBackdrop = _settings.Window.Backdrop;
        var backdropWanted = WindowChromeInterop.Resolve() != BackdropKind.None && WindowChromeInterop.TransparencyEffectsEnabled();
        Terminal.TerminalFactory.Composition = Terminal.TerminalComposition.From(_settings.Window.TerminalOpacity, backdropWanted);
        if (_settings.Window.TerminalOpacity < 1.0)
        {
            _trace.Write(Terminal.TerminalFactory.Composition.Enabled
                ? $"window: terminal body composed at opacity {Terminal.TerminalFactory.Composition.Opacity:F2}"
                : "window: terminalOpacity below 1 but no backdrop (window.backdrop none, or transparency effects off) - the terminal body stays opaque");
        }
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
            Compatibility = _settings.Compatibility,
            EnvironmentFor = _endpoint is { } ep ? ep.EnvironmentFor : null,
            Commands = _endpoint?.Commands,
        };

        // overshell:// for this user, so a toast click finds its tab. HKCU only, rewritten only
        // when it points elsewhere (a moved build).
        if (_settings.Protocol.Register && Environment.ProcessPath is { } exe)
        {
            try
            {
                if (ProtocolRegistration.Register(exe))
                {
                    _trace.Write($"protocol: registered overshell:// -> {exe}");
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                _trace.Write($"protocol: registration failed: {e.Message}");
            }
        }
    }

    /// <summary>Needs the visual tree: the toast layer anchors to the middle of the window.</summary>
    private void InitializeNotifications()
    {
        _notifications = new NotificationPipeline(this, MainHost, FocusTabById, _settings.Notifications) { AnswerTab = AnswerTabById, Filter = e => _shell?.NotificationFilter?.Invoke(e) ?? true };
        _trace.Write($"notification sinks: {string.Join(", ", _notifications.ActiveSinks)}");
    }

    // ------------------------------------------------------------- commands

    private void RegisterCommands()
    {
        var c = _commands;

        c.Register("tab.new", "New tab", "Tabs", () => NewTab(_catalog.DefaultProfile), description: "Open the default profile");
        c.Register("tab.duplicate", "Duplicate tab", "Tabs", () => { if (TargetTab is { } t) DuplicateTab(t); }, () => TargetTab is not null, "Same profile, same directory, same group - next to this tab");
        c.Register("tab.close", "Close tab", "Tabs", () => { if (TargetTab is { } t) CloseTab(t); }, () => TargetTab is not null);
        c.Register("tab.next", "Next tab", "Tabs", () => ActivateRelative(1), () => Tabs.Count > 1);
        c.Register("tab.previous", "Previous tab", "Tabs", () => ActivateRelative(-1), () => Tabs.Count > 1);
        c.Register("tab.jumpToAttention", "Jump to the tab that needs you", "Tabs", JumpToAttention, description: "Blocked first, then finished-unseen; the one waiting longest (attention.order)");
        c.Register("tab.nextBlocked", "Next tab waiting for you", "Tabs", () => JumpWaiting(1, blockedOnly: true, doneOnly: false, "No tab is waiting for you"), () => Tabs.Any(t => t.State is AgentState.Blocked or AgentState.Error), "Blocked or errored; the one waiting longest first");
        c.Register("tab.previousBlocked", "Previous tab waiting for you", "Tabs", () => JumpWaiting(-1, blockedOnly: true, doneOnly: false, "No tab is waiting for you"), () => Tabs.Any(t => t.State is AgentState.Blocked or AgentState.Error));
        c.Register("tab.nextDone", "Next tab that finished unseen", "Tabs", () => JumpWaiting(1, blockedOnly: false, doneOnly: true, "Nothing finished unseen"), () => Tabs.Any(t => t.State == AgentState.Done && t.Unread));
        c.Register("tab.rename", "Rename tab", "Tabs", RenameActiveTab, () => TargetTab is not null);
        c.Register("tab.markAgent", "Treat this tab as an agent", "Agents", () => TargetTab?.MarkAsAgent(), () => TargetTab is { IsAgent: false });
        c.Register("tab.markShell", "Treat this tab as a shell", "Agents", () => TargetTab?.MarkAsShell(), () => TargetTab is { IsAgent: true });
        c.Register("tab.explain", "Explain this tab's state", "Agents", OpenExplain, () => TargetTab is not null, "Evidence trail: harness, authority, session, processes, transitions");

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
        c.Register("settings.open", "Open settings folder", "Settings", () => OpenFolder(AppPaths.ConfigRoot), description: $"{AppPaths.ConfigRoot} (from {AppPaths.Config.Variable})");
        RegisterSettingsAndHelpCommands(c);
        c.Register("settings.init", "Create settings.jsonc and keybindings.jsonc from the defaults", "Settings", InitSettingsFiles, () => !System.IO.File.Exists(AppPaths.SettingsFile) || !System.IO.File.Exists(AppPaths.KeybindingsFile), "Fully commented starter files; existing files are kept");

        c.Register("tab.resume", "Resume this tab's agent session", "Agents", () =>
        {
            if (TargetTab is { } t && !t.Resume())
            {
                ShowStatusMessage("No session to resume in this tab");
            }
        }, () => TargetTab is { ResumeCommand: not null }, "Types the harness's resume command (e.g. opencode --session <id>)");
        c.Register("session.save", "Save session now", "Settings", () => { SaveSession(force: true); ShowStatusMessage($"Session saved to {AppPaths.SessionFile}"); }, description: "Tabs, labels, groups and view; restored at the next start");
        c.Register("protocol.register", "Register overshell:// for this user", "Settings", () =>
        {
            var exe = Environment.ProcessPath ?? string.Empty;
            var changed = ProtocolRegistration.Register(exe);
            ShowStatusMessage(changed ? $"overshell:// now opens {exe}" : "overshell:// was already registered for this executable");
        }, description: "HKCU only; toast clicks then focus their tab");
    }

    internal PaletteWindow? Palette => _palette;

    /// <summary>The chord shown next to a command (menus, palette hints); null when none is bound.</summary>
    internal string? HintFor(string commandId) => _keybindings.FirstChordFor(commandId);

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
        if (TargetTab is not { } tab)
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

    // --------------------------------------------------------- integrations

    private void RunIntegration(string id, bool install)
    {
        try
        {
            var status = install ? IntegrationInstaller.Install(id) : IntegrationInstaller.Uninstall(id);
            if (status.Note is { } note && install && !status.Installed)
            {
                ShowStatusMessage($"{id}: not installed — {note}");
                return;
            }

            ShowStatusMessage(install
                ? $"{id}: installed {status.Path}{(status.HarnessFound ? string.Empty : " (harness not found on PATH)")}"
                : $"{id}: removed from {status.Path}");
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
            var state = s.Installed ? s.Current ? "installed" : "installed (outdated)" : "not installed";
            return $"{id}: {state}{(s.HarnessFound ? string.Empty : ", harness not on PATH")}{(s.Note is null ? string.Empty : $" ({s.Note})")}";
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
            WindowIsFocused: IsActive,
            CanAnswer: kind == NotificationKind.Blocked && tab.ReplyChannel is Core.Extensibility.ReplyChannel.Integration or Core.Extensibility.ReplyChannel.Keys);

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
    private void JumpToAttention() => JumpWaiting(direction: 1, blockedOnly: false, doneOnly: false, "Nothing needs you");

    /// <summary>
    /// Goes to a tab that waits for the user (§12.16): by default the one that has waited
    /// longest (<c>attention.order: age</c>), else the next in strip order. Blocked and error
    /// tabs come before unseen-done ones. Returns false when nothing waits.
    /// </summary>
    internal bool JumpWaiting(int direction, bool blockedOnly, bool doneOnly, string? nothingMessage = null)
    {
        var waiting = Tabs.Select((t, i) => new HerdOrdering.Waiting(t.Id, t.State, t.Unread, t.Agent.AttentionSince, i)).ToList();
        var currentIndex = ActiveTab is { } active ? Tabs.IndexOf(active) : -1;
        var target = HerdOrdering.NextWaiting(waiting, currentIndex, _settings.Attention.ByAge, direction, blockedOnly, doneOnly);
        if (target is null)
        {
            if (nothingMessage is not null)
            {
                ShowStatusMessage(nothingMessage);
            }

            return false;
        }

        ActiveTab = Tabs[target.Index];
        return true;
    }
    /// <summary>A toast's Allow / Deny (12.17): answers the tab through its reply channel; false when it has none.</summary>
    private bool AnswerTabById(string tabId, bool approve)
    {
        if (!_tabsById.TryGetValue(tabId, out var tab))
        {
            return false;
        }

        var taken = tab.Answer(approve);
        ShowStatusMessage(taken ? $"{(approve ? "Approved" : "Denied")} in {tab.Label}" : $"{tab.Label}: no one-key answer for this harness");
        return taken;
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
            var viewed = (ReferenceEquals(tab, ActiveTab) && IsActive) || IsViewedInTearOff(tab);
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

    /// <summary>
    /// Shows a line in the status bar's detail slot for a few seconds, then restores the
    /// working directory. When the layout has no status bar (Zen), the line goes to an
    /// in-window toast instead — a message nobody can see is not a message.
    /// </summary>
    internal void ShowStatusMessage(string message)
    {
        if (!_statusVisible)
        {
            _notifications?.Announce(message);
        }

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
