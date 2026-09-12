using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.Config;

namespace OverShell.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ProfileCatalog _catalog;
    private readonly DispatcherTimer _statusTimer;
    private readonly ShortcutRouter _shortcuts;
    private TerminalTab? _activeTab;

    public MainWindow()
    {
        _catalog = ProfileCatalog.Load();

        InitializeComponent();
        DataContext = this;

        _shortcuts = new ShortcutRouter(this);

        // Single terminal on launch — panes and extra tabs are opt-in.
        if (_catalog.DefaultProfile is { } profile)
        {
            AddTab(profile, activate: true);
        }

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        RegisterShortcuts();

        StateChanged += (_, _) => SyncMaximizeState();
        SourceInitialized += (_, _) =>
        {
            ApplyBackdrop();
            HookSettingChange();
        };

        // The frame-extension margins are physical pixels, so they have to be
        // recomputed whenever the window lands on a monitor with a different scale.
        DpiChanged += (_, _) => ApplyBackdrop();

        Loaded += (_, _) =>
        {
            SyncMaximizeState();
            ActiveTab?.View.Focus();
        };
    }

    public ObservableCollection<TerminalTab> Tabs { get; } = [];

    public ProfileCatalog Catalog => _catalog;

    public TerminalTab? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (ReferenceEquals(_activeTab, value))
            {
                return;
            }

            if (_activeTab is not null)
            {
                _activeTab.IsActive = false;
            }

            _activeTab = value;

            if (_activeTab is not null)
            {
                _activeTab.IsActive = true;
                Title = $"{_activeTab.Title} — OverShell";
            }

            Raise();
            UpdateStatus();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ------------------------------------------------------------ tab model

    private TerminalTab AddTab(TerminalProfile profile, bool activate)
    {
        var tab = new TerminalTab(profile, _catalog.SchemeFor(profile), Dispatcher);

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalTab.Title) && ReferenceEquals(tab, ActiveTab))
            {
                Title = $"{tab.Title} — OverShell";
            }
        };

        Tabs.Add(tab);
        TerminalHost.Children.Add(tab.View);

        if (activate)
        {
            ActiveTab = tab;
        }

        return tab;
    }

    private void CloseTab(TerminalTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // Dispose first: it suppresses further input, so the focus and key messages
        // generated while the HwndHost is unloaded can't reach a closed pseudoconsole.
        tab.Dispose();

        Tabs.RemoveAt(index);
        TerminalHost.Children.Remove(tab.View);

        if (Tabs.Count == 0)
        {
            Close();
            return;
        }

        if (ReferenceEquals(tab, ActiveTab))
        {
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    private void ActivateRelative(int delta)
    {
        if (ActiveTab is null || Tabs.Count < 2)
        {
            return;
        }

        var index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }

    // ------------------------------------------------------------- commands

    private void RegisterShortcuts()
    {
        var router = _shortcuts;

        router.Add(Key.T, ModifierKeys.Control, () => NewTab(_catalog.DefaultProfile));
        router.Add(Key.W, ModifierKeys.Control | ModifierKeys.Shift, () => { if (ActiveTab is { } t) CloseTab(t); });
        router.Add(Key.Tab, ModifierKeys.Control, () => ActivateRelative(1));
        router.Add(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => ActivateRelative(-1));
        router.Add(Key.PageDown, ModifierKeys.Control, () => ActivateRelative(1));
        router.Add(Key.PageUp, ModifierKeys.Control, () => ActivateRelative(-1));

        // Explicit, unambiguous clipboard chords.
        router.Add(Key.C, ModifierKeys.Control | ModifierKeys.Shift, () => { Copy(); return true; });
        router.Add(Key.V, ModifierKeys.Control | ModifierKeys.Shift, () => { Paste(); return true; });

        // Ctrl+C only copies when there is something selected; otherwise it must reach
        // the shell as an interrupt, which is what a terminal user expects.
        router.Add(Key.C, ModifierKeys.Control, Copy);
        router.Add(Key.V, ModifierKeys.Control, () => { Paste(); return true; });

        for (var i = 0; i < 9; i++)
        {
            var index = i;
            router.Add(Key.D1 + i, ModifierKeys.Alt, () =>
            {
                if (index < Tabs.Count)
                {
                    ActiveTab = Tabs[index];
                }
            });
        }

        // Classic console behaviour: right-click copies a selection, else pastes.
        router.RightClick = screenPoint =>
        {
            if (ActiveTab is null || !IsPointOverTerminal(screenPoint))
            {
                return false;
            }

            if (!Copy())
            {
                Paste();
            }

            return true;
        };
    }

    /// <summary>
    /// Hit-tests a screen point against the terminal area. Uses screen coordinates from
    /// the message itself rather than <see cref="Mouse.GetPosition"/>, whose state is
    /// stale while the native terminal owns the pointer.
    /// </summary>
    private bool IsPointOverTerminal(Point screenPoint)
    {
        if (!IsLoaded || TerminalHost.ActualWidth <= 0)
        {
            return false;
        }

        var topLeft = TerminalHost.PointToScreen(new Point(0, 0));
        var bottomRight = TerminalHost.PointToScreen(
            new Point(TerminalHost.ActualWidth, TerminalHost.ActualHeight));

        return screenPoint.X >= topLeft.X && screenPoint.X <= bottomRight.X &&
               screenPoint.Y >= topLeft.Y && screenPoint.Y <= bottomRight.Y;
    }

    /// <returns>True when a selection existed and was copied.</returns>
    private bool Copy()
    {
        var selection = ActiveTab?.SelectedText();
        if (string.IsNullOrEmpty(selection))
        {
            return false;
        }

        TrySetClipboard(selection);
        return true;
    }

    private void Paste()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText())
            {
                tab.SendText(Clipboard.GetText());
            }
        }
        catch (ExternalException)
        {
            // Another process had the clipboard locked; nothing useful to do.
        }
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Clipboard contention — swallow rather than crash the shell.
        }
    }

    private void NewTab(TerminalProfile? profile)
    {
        if (profile is not null)
        {
            AddTab(profile, activate: true);
        }
    }

    // -------------------------------------------------------- event handlers

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab(_catalog.DefaultProfile);

    private void Profiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("ShellContextMenu"),
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var itemStyle = (Style)FindResource("ShellMenuItem");

        foreach (var profile in _catalog.Profiles)
        {
            var item = new MenuItem
            {
                Header = profile.Name,
                Style = itemStyle,
                DataContext = new MenuAccent(TabAccent.For(profile.Id)),
                InputGestureText = profile.Id == _catalog.DefaultProfile?.Id ? "default" : string.Empty,
            };

            var captured = profile;
            item.Click += (_, _) => NewTab(captured);
            menu.Items.Add(item);
        }

        if (_catalog.Diagnostics.Count > 0)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
            menu.Items.Add(new MenuItem
            {
                Header = $"{_catalog.Diagnostics.Count} profile(s) unavailable",
                Style = itemStyle,
                IsEnabled = false,
            });
        }

        menu.IsOpen = true;
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            ActiveTab = tab;

            // Stop this bubbling to the title bar, which would start a window drag.
            e.Handled = true;
        }
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click closes, matching every other tabbed app.
        if (e.ChangedButton == MouseButton.Middle &&
            sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Restore under the cursor, the way every native title bar behaves.
            var cursor = e.GetPosition(this);
            var ratio = ActualWidth > 0 ? cursor.X / ActualWidth : 0.5;

            WindowState = WindowState.Normal;

            var screen = PointToScreen(cursor);
            Left = screen.X - (RestoreBounds.Width * ratio);
            Top = screen.Y - (e.GetPosition(this).Y / 2);
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _shortcuts.Dispose();

        // Two passes on purpose: WPF unloads every HwndHost during shutdown and each one
        // generates focus traffic, so all tabs must stop accepting input before any of
        // them starts closing pipes.
        foreach (var tab in Tabs)
        {
            tab.SuppressInput();
        }

        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        base.OnClosed(e);
    }

    // --------------------------------------------------------------- chrome

    /// <summary>
    /// Applies the system backdrop and swaps the chrome brushes to match. If the
    /// backdrop is unavailable — old build, transparency effects switched off, DWM
    /// refusing — we fall back to the fully opaque chrome rather than rendering a
    /// washed-out translucent tint over nothing.
    /// </summary>
    private void ApplyBackdrop()
    {
        var top = (double)FindResource("Metrics.TitleBarHeight");
        var bottom = (double)FindResource("Metrics.StatusBarHeight");

        var active = WindowChromeInterop.Apply(this, WindowChromeInterop.Resolve(), top, bottom);

        // With no backdrop the composition target is opaque, so the root has to paint
        // the base surface itself — otherwise the window renders flat black.
        WindowRoot.Background = active ? Brushes.Transparent : (Brush)FindResource("Surface.Base");

        TitleBarSurface.Background = (Brush)FindResource(
            active ? "Surface.ChromeTranslucent" : "Surface.Chrome");
        StatusBarSurface.Background = (Brush)FindResource(
            active ? "Surface.StatusTranslucent" : "Surface.Chrome");

        // ClearType cannot be applied over a transparent surface; asking for it anyway
        // produces colour fringing. Be explicit rather than relying on WPF's fallback.
        TextOptions.SetTextRenderingMode(
            this,
            active ? TextRenderingMode.Grayscale : TextRenderingMode.ClearType);
    }

    /// <summary>
    /// Toggling "Transparency effects" in Settings broadcasts WM_SETTINGCHANGE.
    /// Re-applying keeps the chrome correct without needing a restart.
    /// </summary>
    private void HookSettingChange()
    {
        const int WmSettingChange = 0x001A;
        const int WmDwmCompositionChanged = 0x031E;

        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg is WmSettingChange or WmDwmCompositionChanged)
            {
                Dispatcher.BeginInvoke(ApplyBackdrop, DispatcherPriority.Background);
            }

            return IntPtr.Zero;
        });
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void SyncMaximizeState()
    {
        var maximized = WindowState == WindowState.Maximized;

        BtnMaximize.Content = maximized ? "\uE923" : "\uE922";
        BtnMaximize.ToolTip = maximized ? "Restore" : "Maximize";

        // A maximized WindowChrome window overhangs the work area by its resize border.
        // Derive the compensation from the system metric rather than hard-coding it, so
        // it stays correct across DPI and theme changes.
        var resize = SystemParameters.WindowResizeBorderThickness;
        WindowRoot.Margin = maximized
            ? new Thickness(resize.Left, resize.Top, resize.Right, resize.Bottom)
            : default;

        WindowRoot.BorderThickness = maximized ? default : new Thickness(1);
    }

    private void UpdateStatus()
    {
        if (ActiveTab is not { } tab)
        {
            TxtGrid.Text = string.Empty;
            return;
        }

        var (columns, rows) = tab.Grid;
        var tabCount = Tabs.Count == 1 ? string.Empty : $"   ·   {Tabs.Count} tabs";
        TxtGrid.Text = $"{columns}\u00d7{rows}{tabCount}";

        // Only a tab that actually started can have died. The PTY starts on a background
        // thread, so a plain !IsRunning check would fire on every healthy new tab.
        if (tab.HasStarted && !tab.IsRunning)
        {
            tab.NotifyExited();
        }
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property ?? string.Empty));

    /// <summary>Binding shim so the menu item template can show a profile's accent dot.</summary>
    private sealed record MenuAccent(System.Windows.Media.Brush Accent);
}
