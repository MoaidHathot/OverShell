using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// Most-recently-used tab switching (DESIGN.md §12.16), Windows Terminal's default
/// (<c>tabSwitcherMode: mru</c>): hold Ctrl, tap Tab through the tabs in the order you
/// last used them, release Ctrl to land - an Alt+Tab for tabs. A quick Ctrl+Tab bounces
/// to the previous tab. The overlay is a non-activating owned window over the terminal
/// listing the tabs with the selected one's last screen rows. <c>tabs.switcherMode:
/// "inOrder"</c> keeps the old adjacent cycling and leaves this extension idle.
/// </summary>
public sealed class MruSwitcherExtension : IExtension
{
    private IShell _shell = null!;
    private readonly List<ITab> _mru = [];
    private SwitcherOverlay? _overlay;
    private int _selected = -1;
    private bool _open;
    private MruSettings _settings = new();

    public string Id => "tabs.mru";

    public sealed class MruSettings
    {
        /// <summary><c>mru</c> (default): Ctrl+Tab walks the most recently used tabs; <c>inOrder</c>: adjacent cycling, this extension does nothing.</summary>
        public string SwitcherMode { get; init; } = "mru";

        public bool Mru => !string.Equals(SwitcherMode, "inOrder", StringComparison.OrdinalIgnoreCase);
    }

    public void Initialize(IShell shell)
    {
        _shell = shell;
        _settings = shell.ExtensionSettings<MruSettings>(Id);
        foreach (var tab in shell.Tabs)
        {
            Touch(tab);
        }

        if (shell.ActiveTab is { } active)
        {
            Touch(active);
        }

        shell.Commands.Register("tab.mruNext", "Switch tab: most recent next", "Tabs", () => Step(1), () => shell.Tabs.Count > 1,
            "Hold Ctrl and tap Tab to walk the tabs you used last; release to land");
        shell.Commands.Register("tab.mruPrevious", "Switch tab: most recent previous", "Tabs", () => Step(-1), () => shell.Tabs.Count > 1);

        // Ctrl+Tab becomes the MRU switcher (WT's default); Ctrl+PgUp/PgDn keep walking the strip.
        if (_settings.Mru)
        {
            shell.Keys.AddDefaults(
            [
                new Keybinding("ctrl+tab", "tab.mruNext"),
                new Keybinding("ctrl+shift+tab", "tab.mruPrevious"),
            ]);
        }

        shell.ActiveTabChanged += tab => { if (tab is not null && !_open) Touch(tab); };
        shell.TabClosed += tab => { _mru.Remove(tab); if (_open) Refresh(); };
        shell.Keys.ControlReleased += Commit;
        shell.Keys.Intercept(() => _open, OnChordWhileOpen);
        shell.SettingsChanged += () => _settings = shell.ExtensionSettings<MruSettings>(Id);
    }

    /// <summary>Most recent first.</summary>
    internal IReadOnlyList<ITab> Order => _mru;

    internal bool IsOpen => _open;

    internal int Selected => _selected;

    internal SwitcherOverlay? Overlay => _overlay;

    private void Touch(ITab tab)
    {
        _mru.Remove(tab);
        _mru.Insert(0, tab);
    }

    private void Step(int direction)
    {
        var tabs = _mru.Where(t => _shell.Tabs.Contains(t)).ToList();
        if (tabs.Count < 2)
        {
            return;
        }

        if (!_open)
        {
            _open = true;
            _selected = 0;
        }

        _selected = ((_selected + direction) % tabs.Count + tabs.Count) % tabs.Count;
        Refresh();
    }

    private bool OnChordWhileOpen(KeyChord chord)
    {
        if (chord.Key == "Tab" && chord.Modifiers.HasFlag(ChordModifiers.Control))
        {
            Step(chord.Modifiers.HasFlag(ChordModifiers.Shift) ? -1 : 1);
            return true;
        }

        if (chord.Key == "Escape")
        {
            Cancel();
            return true;
        }

        if (chord.Key is "Return" or "Space")
        {
            Commit();
            return true;
        }

        // Any other key lands on the selection and lets the key go on its way.
        Commit();
        return false;
    }

    /// <summary>Ctrl went up (or Enter): the selected tab becomes active.</summary>
    internal void Commit()
    {
        if (!_open)
        {
            return;
        }

        var tabs = _mru.Where(t => _shell.Tabs.Contains(t)).ToList();
        var target = _selected >= 0 && _selected < tabs.Count ? tabs[_selected] : null;
        CloseOverlay();
        if (target is not null)
        {
            Touch(target);
            _shell.Activate(target);
        }
    }

    internal void Cancel() => CloseOverlay();

    private void CloseOverlay()
    {
        _open = false;
        _selected = -1;
        _overlay?.HideOverlay();
    }

    private void Refresh()
    {
        if (_shell.Ui.Host is not Window owner || _shell.Ui.TerminalArea is not FrameworkElement area)
        {
            return;
        }

        _overlay ??= new SwitcherOverlay(owner, area);
        var tabs = _mru.Where(t => _shell.Tabs.Contains(t)).ToList();
        _overlay.ShowTabs(tabs, _selected);
    }

    public void Dispose()
    {
        CloseOverlay();
        _overlay?.Close();
        _overlay = null;
    }

    /// <summary>
    /// The switcher's window: owned, never activated (the terminal keeps the keyboard, which
    /// is what lets Ctrl's release be seen), centred over the terminal area; a list of tabs
    /// with the selected one's screen rows beside it.
    /// </summary>
    public sealed class SwitcherOverlay : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLongW(nint hwnd, int index, int value);

        private readonly FrameworkElement _area;
        private readonly StackPanel _rows;
        private readonly TextBlock _preview;
        private readonly DispatcherTimer _previewTimer;
        private ITab? _previewTab;

        public SwitcherOverlay(Window owner, FrameworkElement area)
        {
            Owner = owner;
            _area = area;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _rows = new StackPanel { MinWidth = 320 };
            _preview = new TextBlock { Width = 420, Height = 220, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 0, 0, 0) };
            _preview.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            _preview.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            _preview.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");

            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(_rows);
            body.Children.Add(_preview);

            var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10), Child = body };
            frame.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
            frame.SetResourceReference(Border.BorderBrushProperty, "Surface.Border");
            Content = frame;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                _ = SetWindowLongW(hwnd, GwlExStyle, GetWindowLongW(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
            };

            _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _previewTimer.Tick += (_, _) => UpdatePreview();
        }

        public IReadOnlyList<string> Labels { get; private set; } = [];

        public int SelectedIndex { get; private set; } = -1;

        public void ShowTabs(IReadOnlyList<ITab> tabs, int selected)
        {
            Labels = tabs.Select(t => t.Label).ToList();
            SelectedIndex = selected;
            _rows.Children.Clear();
            for (var i = 0; i < tabs.Count; i++)
            {
                _rows.Children.Add(Row(tabs[i], i == selected, i));
            }

            _previewTab = selected >= 0 && selected < tabs.Count ? tabs[selected] : null;
            _previewTab?.RequestScreen();
            UpdatePreview();
            Place();
            if (!IsVisible)
            {
                Show();
            }

            _previewTimer.Start();
        }

        public void HideOverlay()
        {
            _previewTimer.Stop();
            if (IsVisible)
            {
                Hide();
            }
        }

        private void UpdatePreview()
        {
            if (_previewTab is null)
            {
                _preview.Text = string.Empty;
                return;
            }

            var rows = _previewTab.ScreenRows;
            _preview.Text = string.Join('\n', rows.TakeLast(14));
        }

        private FrameworkElement Row(ITab tab, bool selected, int index)
        {
            var row = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 6, 12, 6), Margin = new Thickness(0, 1, 0, 1) };
            if (selected)
            {
                row.SetResourceReference(Border.BackgroundProperty, "Surface.Hover");
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Border.BackgroundProperty, tab.IsAgent ? "State." + tab.State : "Text.Disabled");
            Grid.SetColumn(dot, 0);

            var text = new StackPanel();
            var label = new TextBlock { Text = tab.Label, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            label.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Body");
            var detail = new TextBlock { Text = tab.IsAgent ? $"{tab.State.ToString().ToLowerInvariant()} \u00B7 {tab.Project}" : tab.Project };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");
            detail.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            detail.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");
            text.Children.Add(label);
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);

            var hint = new TextBlock { Text = index == 0 ? "current" : index == 1 ? "previous" : string.Empty, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            hint.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");
            Grid.SetColumn(hint, 2);

            grid.Children.Add(dot);
            grid.Children.Add(text);
            grid.Children.Add(hint);
            row.Child = grid;
            return row;
        }

        private void Place()
        {
            if (!_area.IsLoaded || PresentationSource.FromVisual(_area)?.CompositionTarget is not { } target)
            {
                return;
            }

            UpdateLayout();
            var centre = _area.PointToScreen(new Point(_area.ActualWidth / 2, _area.ActualHeight / 2));
            var dip = target.TransformFromDevice.Transform(centre);
            var width = ActualWidth > 0 ? ActualWidth : 760;
            var height = ActualHeight > 0 ? ActualHeight : 260;
            Left = dip.X - width / 2;
            Top = dip.Y - height / 2;
        }
    }
}
