using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OverShell.App.Chrome;
using OverShell.Core.Tabs;

namespace OverShell.App;

public partial class MainWindow
{
    private readonly List<FloatEntry> _floats = [];
    private readonly Dictionary<string, PromptBar> _floatingPrompts = [];
    private readonly Dictionary<Window, string?> _inputWindowTargets = [];
    private bool _movingPresentation;
    private long _floatGeneration;
    private bool _floatFocusCheckPending;
    private TerminalTab? _reportedFocusedTab;
    internal ITabPresentations Presentations { get; private set; } = null!;
    internal IReadOnlyList<FloatingTerminalPanel> FloatingPanels => _floats.Select(e => e.Panel).ToArray();
    internal int RoutedWindowCount => _shortcuts.AdditionalWindowCount;

    internal void TrackInputWindow(Window window)
    {
        _inputWindowTargets[window] = TargetTab?.Id;
        _shortcuts.AddWindow(window);
        window.Activated += (_, _) => UpdateViewed();
        window.Deactivated += (_, _) => ScheduleFloatFocusCheck();
        window.Closed += (_, _) => { _inputWindowTargets.Remove(window); ScheduleFloatFocusCheck(); };
    }

    private sealed class FloatEntry(FloatingTerminalPanel panel, Panel home, Window homeWindow, NavigationPoint origin)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public FloatingTerminalPanel Panel { get; } = panel;
        public Window Owner => Panel.OwnerWindow;
        public Panel Home { get; } = home;
        public Window HomeWindow { get; } = homeWindow;
        public NavigationPoint Origin { get; } = origin;
        public EventHandler? OwnerState;
        public EventHandler? OwnerActivation;
        public System.ComponentModel.CancelEventHandler? Closing;
        public bool HadFocus;
        public bool Active;
        public bool WasFocused;
        public FindBarController? BackgroundFind;
        public bool BackgroundFindPaused;
    }

    private FloatEntry? FloatFor(TerminalTab? tab) => _floats.FirstOrDefault(e => e.Panel.Tab == tab);
    private FloatEntry? FloatForOwner(Window? owner) => _floats.FirstOrDefault(e => ReferenceEquals(e.Owner, owner));
    private FloatEntry? FocusedFloat => _floats.FirstOrDefault(IsFloatFocused);

    private bool IsFloatFocused(FloatEntry entry)
    {
        if (!ReferenceEquals(ActiveTerminalWindow(), entry.Owner)) return false;
        var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && !ReferenceEquals(w, entry.Owner));
        if (dialog is not null && _inputWindowTargets.TryGetValue(dialog, out var target)) return target == entry.Panel.Tab.Id;
        var nativeFocus = ShortcutRouter.FocusedWindow();
        if (nativeFocus != 0 && nativeFocus == entry.Panel.Tab.TerminalHwnd) return true;
        if (entry.Panel.BackgroundTab is { } background && background != entry.Panel.Tab && nativeFocus != 0 && nativeFocus == background.TerminalHwnd) return false;
        if (entry.Panel.IsInputWithin) return true;
        if (entry.Owner is TearOffWindow secondary && (secondary.PromptBar.IsKeyboardFocusWithin || secondary.Find.InputHasFocus)) return false;
        if (entry.Owner == this && (Prompt.IsKeyboardFocusWithin || Find.IsKeyboardFocusWithin)) return false;
        return entry.Active;
    }

    private void ScheduleFloatFocusCheck()
    {
        if (_floatFocusCheckPending || !_settings.Floating.HideOnFocusLoss || _floats.Count == 0) return;
        _floatFocusCheckPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _floatFocusCheckPending = false;
            if (_movingPresentation || !_settings.Floating.HideOnFocusLoss) return;
            foreach (var entry in _floats.Where(e => e.WasFocused && !IsFloatFocused(e)).ToArray())
                ReleaseFloat(entry.Panel.Tab, returnFocus: false);
        }, DispatcherPriority.Background);
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
        GotKeyboardFocus += (_, _) => UpdateViewed();
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
            if (ReferenceEquals(active, this) || _tearOffs.Any(w => ReferenceEquals(w, active))) return active;
            active = active.Owner;
        }
        return null;
    }

    internal TerminalTab? FocusedTerminalTab
    {
        get
        {
            var owner = ActiveTerminalWindow();
            if (owner is null) return null;
            if (FloatForOwner(owner) is { } entry && IsFloatFocused(entry)) return entry.Panel.Tab;
            return owner is TearOffWindow secondary ? secondary.Tab : !DashboardVisible ? ActiveTab : null;
        }
    }

    private TearOffWindow? TerminalWindowFor(TerminalTab? tab) => _tearOffs.FirstOrDefault(w => w.Tab == tab);
    private Window OwnerForTab(TerminalTab tab) => FloatFor(tab)?.Owner ?? TerminalWindowFor(tab) ?? (Window)this;

    private void ActivateFloat(FloatEntry entry)
    {
        entry.Active = entry.WasFocused = true;
        entry.Panel.Tab.FloatingFocusRequested = true;
        if (entry.Owner.WindowState == WindowState.Minimized) entry.Owner.WindowState = WindowState.Normal;
        entry.Owner.Activate();
        if (entry.Panel.PromptBar.IsVisible) entry.Panel.PromptBar.FocusInput(); else entry.Panel.Tab.Surface.Focus();
        UpdateViewed();
    }

    private FloatEntry? ReleaseFloat(TerminalTab tab, bool returnFocus)
    {
        var entry = FloatFor(tab);
        if (entry is null) return null;
        _floatGeneration++;
        entry.HadFocus = IsFloatFocused(entry);
        var focusedBackground = entry.Panel.BackgroundTab is { PresentationParked: true } background && ShortcutRouter.FocusedWindow() == background.TerminalHwnd ? background : null;
        ClearLinkHover();
        var previousMoving = _movingPresentation;
        _movingPresentation = true;
        try
        {
            _floats.Remove(entry);
            entry.Owner.StateChanged -= entry.OwnerState;
            entry.Owner.Activated -= entry.OwnerActivation;
            entry.Owner.Deactivated -= entry.OwnerActivation;
            entry.HomeWindow.Closing -= entry.Closing;
            if (entry.Owner != entry.HomeWindow) entry.Owner.Closing -= entry.Closing;
            var view = entry.Panel.Release();
            entry.Home.Children.Add(view);
            entry.BackgroundFind?.SetPaused(entry.BackgroundFindPaused);
            if (entry.HomeWindow is TearOffWindow home) home.ApplyBodyOpacity(); else ApplyBodyOpacity();
            (Presentations as WindowPresentations)?.Raise(tab.Id, "regular");
        }
        finally { _movingPresentation = previousMoving; }
        if (returnFocus && entry.HadFocus) Navigation.Restore(entry.Origin);
        else if (focusedBackground is not null && entry.Owner.IsActive && Tabs.Contains(focusedBackground)) focusedBackground.Surface.Focus();
        UpdateViewed();
        return entry;
    }

    // A background view must come home before its tab is closed, detached or borrowed elsewhere.
    private void ReleaseFloatsUsing(TerminalTab tab)
    {
        foreach (var entry in _floats.Where(e => e.Panel.BackgroundTab == tab && e.Panel.Tab != tab).ToArray())
            ReleaseFloat(entry.Panel.Tab, returnFocus: false);
    }

    private void CloseFloats(bool returnFocus)
    {
        foreach (var entry in _floats.ToArray()) ReleaseFloat(entry.Panel.Tab, returnFocus);
    }

    private sealed class WindowPresentations(MainWindow window) : ITabPresentations
    {
        public event Action<PresentationChange>? Changed;
        internal void Raise(string id, string mode) => Changed?.Invoke(new(id, mode));
        public IReadOnlyList<FloatingPresentation> Floating => window._floats.Select(e =>
            new FloatingPresentation(e.Id, e.Panel.Tab.Id, (e.Owner as TearOffWindow)?.Tab.Id, window.IsFloatFocused(e))).ToArray();

        public TabActionResult ShowFloating(string tabId, string? ownerTabId = null, NavigationPoint? returnTo = null, bool activate = true)
        {
            if (!window._tabsById.TryGetValue(tabId, out var tab)) return TabActionResult.Fail("The tab has closed");
            if (window.FloatFor(tab) is { } existing)
            {
                if (activate) window.ActivateFloat(existing);
                return TabActionResult.Ok;
            }
            var owner = ownerTabId is null ? window.ActiveTerminalWindow() ?? window : window._tearOffs.FirstOrDefault(w => w.Tab.Id == ownerTabId);
            if (owner is null) return TabActionResult.Fail("The requested owning window has closed");
            if (activate && owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            var previous = window.FloatForOwner(owner);
            if (!activate && previous is not null && window.IsFloatFocused(previous))
                return TabActionResult.Fail("A floating terminal has focus; activate the replacement or hide the current float first");
            var origin = previous?.Origin ?? returnTo ?? window.Navigation.Capture();
            var oldFocus = Keyboard.FocusedElement;
            var oldTab = window.FocusedTerminalTab;
            if (previous is not null) window.ReleaseFloat(previous.Panel.Tab, returnFocus: false);
            window.ReleaseFloatsUsing(tab);
            window.CloseTerminalPreview();
            window.ClearLinkHover();
            if (tab.View.Parent is not Panel home || Window.GetWindow(tab.View) is not { } homeWindow)
                return TabActionResult.Fail("The terminal has no live presentation host");
            var area = owner is TearOffWindow secondary ? secondary.TerminalArea : window.MainHost;
            var background = owner is TearOffWindow t ? t.Tab : window.DashboardVisible ? null : window.ActiveTab;
            if (!window._floatingPrompts.TryGetValue(tab.Id, out var prompt))
                window._floatingPrompts[tab.Id] = prompt = new PromptBar { Visibility = Visibility.Collapsed };
            var panel = new FloatingTerminalPanel(owner, area, tab, window.Search, prompt, window._settings.Floating);
            window.ConfigurePrompt(tab, prompt);
            var entry = new FloatEntry(panel, home, homeWindow, origin) { Active = activate, WasFocused = activate };
            entry.OwnerState = (_, _) => { if (owner.WindowState == WindowState.Minimized) window.ReleaseFloat(tab, returnFocus: false); };
            entry.OwnerActivation = (_, _) => window.UpdateViewed();
            entry.Closing = (_, _) => window.ReleaseFloat(tab, returnFocus: false);
            var previousMoving = window._movingPresentation;
            window._movingPresentation = true;
            try
            {
                window.FindControllerFor(tab)?.Forget(tab);
                home.Children.Remove(tab.View);
                window._floatGeneration++;
                window._floats.Add(entry);
                tab.FloatingFocusRequested = activate;
                panel.HideRequested += () => window.ReleaseFloat(tab, returnFocus: true);
                panel.OpenNormallyRequested += () => ShowNormally(tab.Id);
                panel.TerminateRequested += () => window.CloseTab(tab);
                panel.InputRequested += () => { tab.FloatingFocusRequested = true; entry.Active = entry.WasFocused = true; window.UpdateViewed(); };
                owner.StateChanged += entry.OwnerState;
                owner.Activated += entry.OwnerActivation;
                owner.Deactivated += entry.OwnerActivation;
                homeWindow.Closing += entry.Closing;
                if (owner != homeWindow) owner.Closing += entry.Closing;
                panel.Host(background);
                if (panel.HasParkedBackground && window.FindControllerFor(background) is { } backgroundFind)
                {
                    entry.BackgroundFind = backgroundFind;
                    entry.BackgroundFindPaused = backgroundFind.Paused;
                    backgroundFind.SetPaused(true);
                }
                if (activate) window.ActivateFloat(entry);
                else if (owner.IsActive)
                {
                    if (oldFocus is System.Windows.Controls.Primitives.TextBoxBase text && text.IsVisible) text.Focus();
                    else oldTab?.Surface.Focus();
                }
            }
            catch (InvalidOperationException error)
            {
                window.ReleaseFloat(tab, returnFocus: false);
                return TabActionResult.Fail($"Could not move the terminal: {error.Message}");
            }
            finally { window._movingPresentation = previousMoving; }
            Raise(tab.Id, "floating");
            window.UpdateViewed();
            return TabActionResult.Ok;
        }

        public TabActionResult ToggleFloating(string tabId) => window._tabsById.TryGetValue(tabId, out var tab) && window.FloatFor(tab) is { } entry && window.IsFloatFocused(entry)
            ? HideFloating(tabId) : ShowFloating(tabId);

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
