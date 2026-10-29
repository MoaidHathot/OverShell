using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OverShell.App.Chrome;
using OverShell.Core.Tabs;

namespace OverShell.App;

public partial class MainWindow
{
    private readonly List<FloatEntry> _floats = [];
    private readonly Dictionary<string, PromptBar> _floatingPrompts = [];
    private bool _movingPresentation;
    private bool _floatFocusCheckPending;
    private TerminalTab? _reportedFocusedTab;
    internal ITabPresentations Presentations { get; private set; } = null!;
    internal IReadOnlyList<TearOffWindow> FloatingWindows => _floats.Select(e => e.Window).ToArray();
    internal int RoutedWindowCount => _shortcuts.AdditionalWindowCount;
    internal void TrackInputWindow(Window window)
    {
        _shortcuts.AddWindow(window);
        window.Deactivated += (_, _) => ScheduleFloatFocusCheck();
        window.Closed += (_, _) => ScheduleFloatFocusCheck();
    }

    private void ScheduleFloatFocusCheck()
    {
        if (_floatFocusCheckPending || !_settings.Floating.HideOnFocusLoss || _floats.Count == 0) return;
        _floatFocusCheckPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _floatFocusCheckPending = false;
            if (_movingPresentation || !_settings.Floating.HideOnFocusLoss) return;
            var active = ActiveTerminalWindow();
            // Focus can leave through an owned picker: the float was already inactive
            // while the picker had focus, so its own Deactivated event cannot tell us.
            foreach (var entry in _floats.Where(e => e.WasFocused && !ReferenceEquals(e.Window, active)).ToArray())
                ReleaseFloat(entry.Window.Tab, returnFocus: false);
        }, DispatcherPriority.Background);
    }

    private sealed class FloatEntry(TearOffWindow window, Panel home, Window homeWindow, NavigationPoint origin)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public TearOffWindow Window { get; } = window;
        public Panel Home { get; } = home;
        public Window HomeWindow { get; } = homeWindow;
        public NavigationPoint Origin { get; } = origin;
        public Action<TerminalTab>? Closing;
        public EventHandler? OwnerState;
        public System.ComponentModel.CancelEventHandler? HomeClosing;
        public bool HadFocus;
        public bool WasFocused;
    }

    private void InitializeFloating()
    {
        Presentations = new WindowPresentations(this);
        _commands.Register("tab.float.show", "Show tab floating", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab) ShowPresentationResult(Presentations.ShowFloating(tab.Id));
            return true;
        });
        _commands.Register("tab.float.toggle", "Toggle floating terminal", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab) ShowPresentationResult(Presentations.ToggleFloating(tab.Id));
            return true;
        });
        _commands.Register("tab.float.hide", "Hide floating terminal", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab) ShowPresentationResult(Presentations.HideFloating(tab.Id));
            return true;
        }, description: "Keep the session running and return to the interrupted work");
        _commands.Register("tab.present.normal", "Open tab in regular view", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab) ShowPresentationResult(Presentations.ShowNormally(tab.Id));
            return true;
        });
        _commands.Register("tab.float.attention", "Show waiting tab floating", "Tabs", () => JumpWaiting(1, false, false, "Nothing needs you", floating: true));
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseFloats(returnFocus: false); };
    }

    private void ShowPresentationResult(TabActionResult result)
    {
        if (!result.Success) ShowStatusMessage(result.Error!);
    }

    internal Window? ActiveTerminalWindow()
    {
        var active = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w.WindowState != WindowState.Minimized);
        while (active is not null)
        {
            if (ReferenceEquals(active, this) || _tearOffs.Any(w => ReferenceEquals(w, active)) || _floats.Any(e => ReferenceEquals(e.Window, active))) return active;
            active = active.Owner;
        }
        return null;
    }

    internal TerminalTab? FocusedTerminalTab => ActiveTerminalWindow() switch
    {
        TearOffWindow host when !host.Tab.Floating || host.IsFloating => host.Tab,
        Window host when ReferenceEquals(host, this) && !DashboardVisible && ActiveTab?.Floating != true => ActiveTab,
        _ => null,
    };

    private TearOffWindow? TerminalWindowFor(TerminalTab? tab) =>
        _floats.FirstOrDefault(e => e.Window.Tab == tab)?.Window ?? _tearOffs.FirstOrDefault(w => w.Tab == tab);

    private TearOffWindow? TerminalWindowForRoot(nint root) => root == 0 ? null
        : FloatingWindows.Concat(_tearOffs).FirstOrDefault(w => new WindowInteropHelper(w).Handle == root);

    private FloatEntry? ReleaseFloat(TerminalTab tab, bool returnFocus, bool closeWindow = true)
    {
        var entry = _floats.FirstOrDefault(e => e.Window.Tab == tab);
        if (entry is null) return null;
        entry.HadFocus = ReferenceEquals(ActiveTerminalWindow(), entry.Window);
        ClearLinkHover();
        var previousMoving = _movingPresentation;
        _movingPresentation = true;
        try
        {
            _floats.Remove(entry);
            entry.Window.ReattachRequested -= entry.Closing;
            entry.Window.Owner.StateChanged -= entry.OwnerState;
            entry.HomeWindow.Closing -= entry.HomeClosing;
            if (entry.Window.Owner != entry.HomeWindow) entry.Window.Owner.Closing -= entry.HomeClosing;
            var view = entry.Window.Release();
            // Owner-close handlers release before the original host closes; no session is disposed here.
            entry.Home.Children.Add(view);
            if (closeWindow) entry.Window.Close();
            if (entry.HomeWindow is TearOffWindow home) home.ApplyBodyOpacity(); else ApplyBodyOpacity();
            (Presentations as WindowPresentations)?.Raise(tab.Id, "regular");
        }
        finally { _movingPresentation = previousMoving; }
        if (returnFocus && entry.HadFocus) Navigation.Restore(entry.Origin);
        UpdateViewed();
        return entry;
    }

    private void CloseFloats(bool returnFocus)
    {
        foreach (var entry in _floats.ToArray()) ReleaseFloat(entry.Window.Tab, returnFocus);
    }

    private sealed class WindowPresentations(MainWindow window) : ITabPresentations
    {
        public event Action<PresentationChange>? Changed;
        internal void Raise(string id, string mode) => Changed?.Invoke(new(id, mode));
        public IReadOnlyList<FloatingPresentation> Floating => window._floats.Select(e =>
            new FloatingPresentation(e.Id, e.Window.Tab.Id, (e.Window.Owner as TearOffWindow)?.Tab.Id, ReferenceEquals(window.ActiveTerminalWindow(), e.Window))).ToArray();

        public TabActionResult ShowFloating(string tabId, string? ownerTabId = null, NavigationPoint? returnTo = null, bool activate = true)
        {
            if (!window._tabsById.TryGetValue(tabId, out var tab)) return TabActionResult.Fail("The tab has closed");
            if (window._floats.FirstOrDefault(e => e.Window.Tab == tab) is { } existing)
            {
                if (activate)
                {
                    if (existing.Window.WindowState == WindowState.Minimized) existing.Window.WindowState = WindowState.Normal;
                    existing.Window.Activate();
                    tab.Surface.Focus();
                }
                return TabActionResult.Ok;
            }
            Window owner = window.ActiveTerminalWindow() ?? window;
            if (owner is TearOffWindow { IsFloating: true } f) owner = f.Owner;
            if (ownerTabId is not null)
            {
                owner = window._tearOffs.FirstOrDefault(w => w.Tab.Id == ownerTabId)!;
                if (owner is null) return TabActionResult.Fail("The requested owning window has closed");
            }
            if (activate && owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            var previous = window._floats.FirstOrDefault(e => ReferenceEquals(e.Window.Owner, owner));
            if (!activate && previous is not null && ReferenceEquals(window.ActiveTerminalWindow(), previous.Window))
                return TabActionResult.Fail("A floating terminal has focus; activate the replacement or hide the current float first");
            var origin = previous?.Origin ?? returnTo ?? window.Navigation.Capture();
            if (previous is not null) window.ReleaseFloat(previous.Window.Tab, returnFocus: false);
            window.CloseTerminalPreview();
            window.ClearLinkHover();
            if (tab.View.Parent is not Panel home || Window.GetWindow(tab.View) is not { } homeWindow)
                return TabActionResult.Fail("The terminal has no live presentation host");
            if (!window._floatingPrompts.TryGetValue(tab.Id, out var prompt))
                window._floatingPrompts[tab.Id] = prompt = new PromptBar { Visibility = Visibility.Collapsed };
            var host = new TearOffWindow(owner, tab, floating: true, window.Search, prompt);
            host.ShowActivated = activate;
            window.ConfigureTerminalWindow(host);
            var entry = new FloatEntry(host, home, homeWindow, origin);
            entry.Closing = _ => window.ReleaseFloat(tab, returnFocus: true, closeWindow: false);
            entry.OwnerState = (_, _) => { if (owner.WindowState == WindowState.Minimized) window.ReleaseFloat(tab, returnFocus: false); };
            entry.HomeClosing = (_, _) => window.ReleaseFloat(tab, returnFocus: false);
            var previousMoving = window._movingPresentation;
            window._movingPresentation = true;
            try
            {
                window.FindControllerFor(tab)?.Forget(tab);
                home.Children.Remove(tab.View);
                host.Host();
                window._floats.Add(entry);
                host.ReattachRequested += entry.Closing;
                host.OpenNormallyRequested += t => ShowNormally(t.Id);
                host.TerminateRequested += window.CloseTab;
                host.Activated += (_, _) => { entry.WasFocused = true; window.UpdateViewed(); };
                host.Deactivated += (_, _) => window.UpdateViewed();
                owner.StateChanged += entry.OwnerState;
                homeWindow.Closing += entry.HomeClosing;
                if (owner != homeWindow) owner.Closing += entry.HomeClosing;
                window._shortcuts.AddWindow(host);
                host.Show();
                FloatingPlacement.Apply(host, owner, window._settings.Floating);
                if (activate)
                {
                    host.Activate();
                    _ = SetForegroundWindow(new WindowInteropHelper(host).Handle);
                    if (prompt.Visibility == Visibility.Visible) prompt.FocusInput(); else tab.Surface.Focus();
                }
            }
            catch (InvalidOperationException error)
            {
                if (window._floats.Contains(entry)) window.ReleaseFloat(tab, returnFocus: false);
                else
                {
                    host.Release();
                    if (tab.View.Parent is null) home.Children.Add(tab.View);
                    host.Close();
                }
                return TabActionResult.Fail($"Could not move the terminal: {error.Message}");
            }
            finally { window._movingPresentation = previousMoving; }
            Raise(tab.Id, "floating");
            window.UpdateViewed();
            return TabActionResult.Ok;
        }

        public TabActionResult ToggleFloating(string tabId)
        {
            var entry = window._floats.FirstOrDefault(e => e.Window.Tab.Id == tabId);
            return entry is not null && ReferenceEquals(window.ActiveTerminalWindow(), entry.Window)
                ? HideFloating(tabId) : ShowFloating(tabId);
        }

        public TabActionResult HideFloating(string tabId, bool returnFocus = true)
        {
            if (!window._tabsById.TryGetValue(tabId, out var tab)) return TabActionResult.Fail("The tab has closed");
            window.ReleaseFloat(tab, returnFocus);
            return TabActionResult.Ok;
        }

        public TabActionResult ShowNormally(string tabId)
        {
            if (!window._tabsById.TryGetValue(tabId, out var tab)) return TabActionResult.Fail("The tab has closed");
            window.ReleaseFloat(tab, returnFocus: false);
            window.ActiveTab = tab;
            if (!tab.Detached)
            {
                if (window.DashboardVisible) window.ShowViewById("terminal");
                window.BringToFront();
            }
            else if (window.TerminalWindowFor(tab) is { } home) _ = SetForegroundWindow(new WindowInteropHelper(home).Handle);
            tab.Surface.Focus();
            return TabActionResult.Ok;
        }
    }
}
