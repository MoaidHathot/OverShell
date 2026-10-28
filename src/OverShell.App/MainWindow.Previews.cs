using System.Windows;
using System.Windows.Threading;
using OverShell.App.Chrome;

namespace OverShell.App;

public partial class MainWindow
{
    private TerminalPreviewWindow? _terminalPreview;
    private DispatcherTimer? _previewHoverTimer;
    private DispatcherTimer? _previewCloseTimer;
    private TerminalTab? _hoverPreviewTab;
    private FrameworkElement? _hoverPreviewAnchor;
    private bool _pointerOverCard;
    private bool _pointerOverPreview;
    internal TerminalPreviewWindow? TerminalPreview => _terminalPreview;

    private void InitializePreviewActions()
    {
        Dashboard.HoverRequested += ScheduleHoverPreview;
        Dashboard.HoverEnded += tab =>
        {
            if (!ReferenceEquals(_hoverPreviewTab, tab)) return;
            _pointerOverCard = false;
            SchedulePreviewClose();
        };
        Deactivated += (_, _) =>
        {
            if (_terminalPreview?.KeyboardPreview != true) CloseTerminalPreview();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseTerminalPreview(); };
        LocationChanged += (_, _) => { if (_terminalPreview?.KeyboardPreview != true) CloseTerminalPreview(); };
    }

    private void ScheduleHoverPreview(TerminalTab tab, FrameworkElement anchor)
    {
        if (!_settings.Dashboard.HoverPreview || !DashboardVisible || _terminalPreview?.KeyboardPreview == true || !IsActive) return;
        _previewCloseTimer?.Stop();
        _hoverPreviewTab = tab;
        _hoverPreviewAnchor = anchor;
        _pointerOverCard = true;
        _previewHoverTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _previewHoverTimer.Tick -= OnPreviewHover;
        _previewHoverTimer.Tick += OnPreviewHover;
        _previewHoverTimer.Interval = TimeSpan.FromMilliseconds(_settings.Dashboard.EffectiveHoverDelay);
        _previewHoverTimer.Stop();
        _previewHoverTimer.Start();
    }

    private void OnPreviewHover(object? sender, EventArgs e)
    {
        _previewHoverTimer?.Stop();
        if (_pointerOverCard && _hoverPreviewTab is { } tab && _hoverPreviewAnchor is { IsVisible: true } anchor && Tabs.Contains(tab) && DashboardVisible && IsActive)
            OpenTerminalPreview(tab, keyboard: false, anchor);
    }

    private void SchedulePreviewClose()
    {
        _previewHoverTimer?.Stop();
        if (_terminalPreview is null || _terminalPreview.KeyboardPreview) return;
        _previewCloseTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _previewCloseTimer.Tick -= OnPreviewClose;
        _previewCloseTimer.Tick += OnPreviewClose;
        _previewCloseTimer.Stop();
        _previewCloseTimer.Start();
    }

    private void OnPreviewClose(object? sender, EventArgs e)
    {
        _previewCloseTimer?.Stop();
        if (_terminalPreview?.KeyboardPreview != true && !_pointerOverPreview && !_pointerOverCard)
            CloseTerminalPreview();
    }

    internal void OpenTerminalPreview(TerminalTab? tab, bool keyboard, FrameworkElement? anchor = null)
    {
        if (tab is null || !Tabs.Contains(tab)) return;
        if (_terminalPreview is { } current && current.KeyboardPreview == keyboard)
        {
            _hoverPreviewTab = tab;
            _hoverPreviewAnchor = anchor;
            _pointerOverCard = anchor is not null;
            current.Tab = tab;
            current.Place(anchor);
            if (keyboard) current.Activate();
            return;
        }
        CloseTerminalPreview();
        var owner = keyboard ? CommandOwner() : this;
        var preview = new TerminalPreviewWindow(owner, tab, _settings.Dashboard, keyboard);
        _terminalPreview = preview;
        _hoverPreviewTab = tab;
        _hoverPreviewAnchor = anchor;
        _pointerOverCard = anchor is not null;
        preview.MouseEnter += (_, _) => { _pointerOverPreview = true; _previewCloseTimer?.Stop(); };
        preview.MouseLeave += (_, _) => { _pointerOverPreview = false; SchedulePreviewClose(); };
        preview.OpenRequested += selected => Dispatcher.BeginInvoke(() =>
        {
            if (!Tabs.Contains(selected)) return;
            _commands.TryExecute("nav.leave");
            _shell.SetCursor(null);
            ActiveTab = selected;
            if (!selected.Detached)
            {
                ApplyView("terminal");
                BringToFront();
            }
            selected.Surface.Focus();
        }, DispatcherPriority.Input);
        preview.NavigateRequested += direction =>
        {
            var index = Tabs.IndexOf(preview.Tab);
            if (index < 0 || Tabs.Count == 0) return;
            var next = Tabs[(index + direction + Tabs.Count) % Tabs.Count];
            preview.Tab = next;
            if (DashboardVisible)
            {
                _shell.SetCursor(next);
                Dashboard.EnsureVisible(next);
            }
        };
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_terminalPreview, preview)) _terminalPreview = null;
            _previewHoverTimer?.Stop();
            _previewCloseTimer?.Stop();
            _hoverPreviewTab = null;
            _hoverPreviewAnchor = null;
            _pointerOverCard = _pointerOverPreview = false;
            if (preview.ReturnFocus && owner.IsVisible)
            {
                owner.Activate();
                RefocusOwner(owner);
            }
        };
        preview.Show();
        preview.Place(anchor);
    }

    private void CloseTerminalPreview()
    {
        _previewHoverTimer?.Stop();
        _previewCloseTimer?.Stop();
        _hoverPreviewTab = null;
        _hoverPreviewAnchor = null;
        _pointerOverCard = _pointerOverPreview = false;
        var preview = _terminalPreview;
        _terminalPreview = null;
        preview?.Dismiss(returnFocus: false);
    }

    private void PreviewSettingsChanged()
    {
        if (_terminalPreview is { KeyboardPreview: false } && !_settings.Dashboard.HoverPreview) CloseTerminalPreview();
        else _terminalPreview?.ApplySettings(_settings.Dashboard);
    }
}
