using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OverShell.App.Chrome;
using OverShell.App.Notifications;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Layout;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// Views and layouts (DESIGN.md §12.5): which chrome surrounds the terminal and what the
/// middle shows, plus live reload of every configuration file. The tab strip and the
/// Herd sidebar are single instances moved between hosts; the terminal never moves.
/// </summary>
public partial class MainWindow
{
    private const double CaptionWithTabs = 54;
    private const double CaptionSlim = 38;

    private readonly TabStrip _tabStrip = new();
    private readonly HerdSidebar _sidebar = new();
    private LayoutCatalog _layouts = null!;
    private GitStatusService? _gitStatus;
    private FileSystemWatcher? _configWatcher;
    private DispatcherTimer? _reloadTimer;
    private readonly HashSet<string> _pendingReloads = new(StringComparer.OrdinalIgnoreCase);
    private string _viewId = "terminal";
    private string _previousViewId = "herd";
    private double _captionHeight = CaptionWithTabs;
    private bool _statusVisible = true;

    /// <summary>Height of the caption bar: tall when it carries the tab strip, slim otherwise.</summary>
    public double CaptionHeight
    {
        get => _captionHeight;
        private set
        {
            if (Math.Abs(_captionHeight - value) < 0.5)
            {
                return;
            }

            _captionHeight = value;
            Raise();
        }
    }

    /// <summary>The current view id: terminal, herd, dashboard or zen.</summary>
    public string ViewId => _viewId;

    internal ChromeLayout CurrentLayout { get; private set; } = new() { Name = LayoutCatalog.DefaultName };

    internal HerdSidebar Sidebar => _sidebar;

    internal TabStrip Strip => _tabStrip;

    private void InitializeViews()
    {
        _layouts = LayoutCatalog.Load(AppPaths.LayoutsDir);
        foreach (var problem in _layouts.Problems)
        {
            _trace.Write($"layouts: {problem}");
        }

        _tabStrip.Tabs = Tabs;
        _tabStrip.TabSelected += tab => ActiveTab = tab;
        _tabStrip.TabCloseRequested += CloseTab;
        _tabStrip.TabMenuRequested += ShowTabMenu;
        _tabStrip.NewTabRequested += () => NewTab(_catalog.DefaultProfile);
        _tabStrip.ProfilesRequested += ShowProfilesMenu;

        _sidebar.TabSelected += tab => ActiveTab = tab;
        _sidebar.TabMenuRequested += ShowTabMenu;

        Dashboard.Tabs = Tabs;
        Dashboard.TabSelected += tab => ActiveTab = tab;
        Dashboard.OpenRequested += tab =>
        {
            ActiveTab = tab;
            ApplyView("terminal");
        };

        ViewSwitch.ViewRequested += ApplyView;

        if (_settings.Git.Dirty)
        {
            _gitStatus = CreateGitStatus();
        }

        RegisterViewCommands();
        ApplyView(AppSettings.ViewOrder.Contains(_settings.View, StringComparer.OrdinalIgnoreCase) ? _settings.View.ToLowerInvariant() : "terminal");
        WatchConfiguration();
    }

    private GitStatusService CreateGitStatus()
    {
        var service = new GitStatusService(Dispatcher, _settings.Git.StatusIntervalMs);
        service.Updated += root =>
        {
            var dirty = service.Query(root, DateTimeOffset.MinValue);
            foreach (var tab in Tabs)
            {
                if (string.Equals(tab.GitRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    tab.ApplyDirty(dirty);
                }
            }
        };
        return service;
    }

    private void RegisterViewCommands()
    {
        foreach (var id in AppSettings.ViewOrder)
        {
            var view = id;
            var title = _settings.ViewFor(id).Title ?? char.ToUpperInvariant(id[0]) + id[1..];
            _commands.Register($"view.{id}", $"View: {title}", "View", () => ApplyView(view), description: $"Layout '{_settings.ViewFor(id).Layout}'");
        }

        _commands.Register("view.toggle", "View: toggle last two", "View", () => ApplyView(_previousViewId), description: "Switch between the current view and the one before it");
        _commands.Register("settings.reload", "Reload configuration", "Settings", () => ReloadConfiguration(all: true), description: "settings.jsonc, keybindings.jsonc, agents\\, layouts\\");

        // Layouts at runtime: pick any preset (or user layout) for the current view. The choice
        // lives for this session; settings.jsonc is where it becomes permanent.
        foreach (var layout in _layouts.All.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = layout.Name;
            _commands.Register($"layout.{name}", $"Layout: {name}", "Layout", () => OverrideLayout(name), description: layout.Description);
        }
    }

    private readonly Dictionary<string, string> _layoutOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Uses <paramref name="layoutName"/> for the current view until settings say otherwise.</summary>
    private void OverrideLayout(string layoutName)
    {
        _layoutOverrides[_viewId] = layoutName;
        ApplyView(_viewId);
        ShowStatusMessage($"Layout '{layoutName}' for the {_viewId} view (this session)");
    }

    /// <summary>For in-process diagnostics: applies a layout by name without touching the view.</summary>
    internal bool ApplyLayoutByName(string name)
    {
        if (_layouts.Find(name) is not { } layout)
        {
            return false;
        }

        ApplyLayout(layout);
        return true;
    }

    /// <summary>The tab's context menu (strip, list, rail, sidebar): the tab commands, aimed at this tab.</summary>
    private void ShowTabMenu(TerminalTab tab, FrameworkElement anchor)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("ShellContextMenu"),
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        var itemStyle = (Style)FindResource("ShellMenuItem");
        var accent = new MenuAccent(tab.StateBrush);

        void Add(string header, string? gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, Style = itemStyle, DataContext = accent, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        // Commands act on the active tab, so the menu first makes this one active.
        Add("Rename…", HintFor("tab.rename"), () => { ActiveTab = tab; _commands.TryExecute("tab.rename"); });
        Add(tab.IsAgent ? "Treat as a shell" : "Treat as an agent", null, () => { if (tab.IsAgent) tab.MarkAsShell(); else tab.MarkAsAgent(); });
        Add("Explain state", null, () => ShowStatusMessage($"{tab.Label}: {tab.State} — {tab.Agent.Explain} ({tab.Agent.Authority}{(tab.Harness is null ? string.Empty : ", " + tab.Harness)})"));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        Add("Close tab", HintFor("tab.close"), () => CloseTab(tab));

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ views

    private void ApplyView(string id)
    {
        var definition = _settings.ViewFor(id);
        var layout = _layouts.Resolve(_layoutOverrides.GetValueOrDefault(id) ?? definition.Layout);
        var changed = !string.Equals(id, _viewId, StringComparison.OrdinalIgnoreCase);

        if (changed)
        {
            _previousViewId = _viewId;
            _viewId = id.ToLowerInvariant();
        }

        ApplyLayout(layout);
        ShowContent(definition.Content);
        ViewSwitch.Current = _viewId;
        Raise(nameof(ViewId));
        _trace.Write($"view {_viewId}: layout={layout.Name} content={definition.Content}");

        if (definition.Content == ViewContent.Terminal)
        {
            Dispatcher.BeginInvoke(() => ActiveTab?.Surface.Focus(), DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// Places the tab strip and sidebar for a layout. Idempotent: applying the current
    /// layout again only re-asserts positions, which is what a reload wants.
    /// </summary>
    private void ApplyLayout(ChromeLayout layout)
    {
        CurrentLayout = layout;

        // Every host lets go of both movable pieces first; then each is placed once.
        Detach(_tabStrip);
        Detach(_sidebar);
        LeftPanel.Children.Clear();
        RightPanel.Children.Clear();

        // Tabs.
        _tabStrip.Mode = layout.Tabs.EffectiveStyle;
        _tabStrip.Visibility = layout.Tabs.Placement == TabsPlacement.Hidden ? Visibility.Collapsed : Visibility.Visible;
        _tabStrip.Width = double.NaN;
        switch (layout.Tabs.Placement)
        {
            case TabsPlacement.Top:
                CaptionTabsHost.Content = _tabStrip;
                break;
            case TabsPlacement.Bottom:
                BottomTabsHost.Content = _tabStrip;
                break;
            case TabsPlacement.Left:
            case TabsPlacement.Right:
                _tabStrip.Width = layout.Tabs.EffectiveStyle == TabsStyle.Rail ? 48 : Math.Max(120, layout.Tabs.Width);
                (layout.Tabs.Placement == TabsPlacement.Left ? LeftPanel : RightPanel).Children.Add(WithEdge(_tabStrip, layout.Tabs.Placement == TabsPlacement.Left));
                break;
        }

        BottomBar.Visibility = layout.Tabs.Placement == TabsPlacement.Bottom ? Visibility.Visible : Visibility.Collapsed;
        CaptionHeight = layout.TabsInCaption ? CaptionWithTabs : CaptionSlim;
        TxtCaptionTitle.Visibility = layout.TabsInCaption ? Visibility.Collapsed : Visibility.Visible;
        TxtCaptionTitle.Text = ActiveTab is { } active ? active.Title : "OverShell";

        // Sidebar.
        if (layout.Sidebar.Placement != SidePlacement.Hidden)
        {
            _sidebar.Width = Math.Max(200, layout.Sidebar.Width);
            (layout.Sidebar.Placement == SidePlacement.Left ? LeftPanel : RightPanel).Children.Add(WithEdge(_sidebar, layout.Sidebar.Placement == SidePlacement.Left));
            _sidebar.Refresh(Tabs);
        }

        LeftPanel.Visibility = LeftPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RightPanel.Visibility = RightPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Status bar.
        _statusVisible = layout.Status.Visible;
        StatusBarSurface.Visibility = _statusVisible ? Visibility.Visible : Visibility.Collapsed;

        // The backdrop bands follow the caption and status heights.
        if (IsLoaded)
        {
            ApplyBackdrop();
        }
    }

    /// <summary>Wraps a side panel with a hairline towards the terminal.</summary>
    private static Border WithEdge(FrameworkElement panel, bool onLeft) => new()
    {
        Child = panel,
        BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("Surface.Border"),
        BorderThickness = onLeft ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0),
    };

    private void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case ContentControl host:
                host.Content = null;
                break;
            case Border edge:
                edge.Child = null;
                if (edge.Parent is Panel edgeHost)
                {
                    edgeHost.Children.Remove(edge);
                }

                break;
            case Panel panel:
                panel.Children.Remove(element);
                break;
        }
    }

    private void ShowContent(ViewContent content)
    {
        var dashboard = content == ViewContent.Dashboard;

        // Hiding the terminal host hides the native windows with it; UIA still reads them
        // (§12.7, spike 1), which is what the cards are built from.
        TerminalHost.Visibility = dashboard ? Visibility.Collapsed : Visibility.Visible;
        Dashboard.Visibility = dashboard ? Visibility.Visible : Visibility.Collapsed;

        foreach (var tab in Tabs)
        {
            tab.ScreenWatched = dashboard || _palette is not null;
            if (dashboard)
            {
                tab.RequestScreen();
            }
        }

        if (dashboard)
        {
            // Keyboard focus leaves the terminal; the window takes it so chords still route.
            Focus();
        }
    }

    /// <summary>Heartbeat step for the views: sidebar order, activity texts, git decoration.</summary>
    private void RefreshViews(DateTimeOffset now)
    {
        foreach (var tab in Tabs)
        {
            tab.RefreshGit(now, _settings.Git.Branch, _gitStatus);
        }

        if (CurrentLayout.Sidebar.Placement != SidePlacement.Hidden)
        {
            _sidebar.Refresh(Tabs);
        }

        var urgent = _counts.Blocked > 0 || _counts.Error > 0;
        ViewSwitch.SetAttention(_counts.Blocked + _counts.Error + _counts.Done, urgent);

        if (!CurrentLayout.TabsInCaption && ActiveTab is { } active && TxtCaptionTitle.Text != active.Title)
        {
            TxtCaptionTitle.Text = active.Title;
        }
    }

    // ------------------------------------------------------------- hot reload

    /// <summary>
    /// Watches the configuration root for <c>*.jsonc</c> changes and reloads what changed,
    /// debounced: editors write a file several times in a row.
    /// </summary>
    private void WatchConfiguration()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigRoot);
            _configWatcher = new FileSystemWatcher(AppPaths.ConfigRoot, "*.jsonc")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            FileSystemEventHandler onChange = (_, e) => Dispatcher.BeginInvoke(() => QueueReload(e.FullPath));
            _configWatcher.Changed += onChange;
            _configWatcher.Created += onChange;
            _configWatcher.Deleted += onChange;
            _configWatcher.Renamed += (_, e) => Dispatcher.BeginInvoke(() => QueueReload(e.FullPath));
            _configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _trace.Write($"config watcher failed: {e.Message}");
        }
    }

    private void QueueReload(string path)
    {
        _pendingReloads.Add(path);
        _reloadTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadTimer.Tick -= OnReloadTimer;
        _reloadTimer.Tick += OnReloadTimer;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void OnReloadTimer(object? sender, EventArgs e)
    {
        _reloadTimer?.Stop();
        var paths = _pendingReloads.ToArray();
        _pendingReloads.Clear();

        var settings = paths.Any(p => string.Equals(p, AppPaths.SettingsFile, StringComparison.OrdinalIgnoreCase));
        var keys = paths.Any(p => string.Equals(p, AppPaths.KeybindingsFile, StringComparison.OrdinalIgnoreCase));
        var agents = paths.Any(p => p.StartsWith(AppPaths.AgentsDir, StringComparison.OrdinalIgnoreCase));
        var layouts = paths.Any(p => p.StartsWith(AppPaths.LayoutsDir, StringComparison.OrdinalIgnoreCase));

        ReloadConfiguration(all: false, settings, keys, agents, layouts);
    }

    /// <summary>Reloads configuration files and re-applies them to the running window.</summary>
    internal void ReloadConfiguration(bool all, bool settings = false, bool keybindings = false, bool agents = false, bool layouts = false)
    {
        var parts = new List<string>();

        if (all || keybindings)
        {
            LoadKeybindings();
            parts.Add($"keybindings ({_keybindings.Bindings.Count} chords{(_keybindings.Problems.Count > 0 ? $", {_keybindings.Problems.Count} problem(s)" : string.Empty)})");
        }

        if (all || agents)
        {
            _rules = AgentRules.Load(AppPaths.AgentsDir);
            _agents.Rules = _rules;
            foreach (var tab in Tabs)
            {
                tab.RulesReloaded();
            }

            parts.Add($"agents ({_rules.All.Count} rule sets{(_rules.Problems.Count > 0 ? $", {_rules.Problems.Count} problem(s)" : string.Empty)})");
        }

        if (all || layouts)
        {
            _layouts = LayoutCatalog.Load(AppPaths.LayoutsDir);
            parts.Add($"layouts ({_layouts.All.Count})");
        }

        if (all || settings)
        {
            _settings = AppSettings.Load(File.Exists(AppPaths.SettingsFile) ? AppPaths.SettingsFile : null);
            _agents.Detection = _settings.Detection;

            _notifications?.Dispose();
            _notifications = new NotificationPipeline(this, MainHost, FocusTabById, _settings.Notifications);
            _counts = default;
            RefreshAttention();

            if (_settings.Git.Dirty && _gitStatus is null)
            {
                _gitStatus = CreateGitStatus();
            }
            else if (!_settings.Git.Dirty)
            {
                _gitStatus = null;
            }


            parts.Add($"settings{(_settings.Problems.Count > 0 ? $" ({_settings.Problems.Count} problem(s))" : string.Empty)}");
        }

        if (all || settings || layouts)
        {
            ApplyView(_viewId);
        }

        foreach (var problem in _settings.Problems.Concat(_keybindings.Problems).Concat(_rules.Problems).Concat(_layouts.Problems))
        {
            _trace.Write($"reload: {problem}");
        }

        if (parts.Count > 0)
        {
            ShowStatusMessage($"Reloaded {string.Join(", ", parts)}");
        }
    }
}
