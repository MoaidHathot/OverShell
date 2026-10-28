using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>A read-only enlarged preview. Hover windows never take keyboard focus.</summary>
internal sealed class TerminalPreviewWindow : Window
{
    private const int GwlExStyle = -20;
    private const long ToolWindow = 0x80;
    private const long NoActivate = 0x08000000;
    private readonly TerminalPreviewControl _preview;
    private readonly bool _keyboard;
    private bool _closing;
    private bool _dismissRequested;

    public TerminalPreviewWindow(Window owner, TerminalTab tab, DashboardSettings settings, bool keyboard)
    {
        Owner = owner;
        _keyboard = keyboard;
        ShowActivated = keyboard;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = keyboard ? ResizeMode.CanResize : ResizeMode.NoResize;
        Width = 900;
        Height = 600;
        MinWidth = 320;
        MinHeight = 220;
        Left = owner.Left + 24;
        Top = owner.Top + 48;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 36, GlassFrameThickness = default, CornerRadius = new CornerRadius(8),
            ResizeBorderThickness = new Thickness(keyboard ? 4 : 0), UseAeroCaptionButtons = false,
        });
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = Button("\uE8BB", "Close preview (Esc)", () => Dismiss(returnFocus: keyboard));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var open = Button("\uE8A7", "Open terminal (Enter)", Open);
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(open);
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(TerminalTab.Label)));
        header.Children.Add(title);
        root.Children.Add(header);
        _preview = new TerminalPreviewControl { Settings = settings };
        Grid.SetRow(_preview, 1);
        root.Children.Add(_preview);
        _preview.MouseLeftButtonDown += (_, e) => { if (!keyboard || e.ClickCount == 2) { e.Handled = true; Open(); } };
        var help = new TextBlock
        {
            Text = keyboard ? "Esc / Space close · Enter open · Arrow keys change tab" : "Read-only live preview · Click to open terminal",
            FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        help.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
        Grid.SetRow(help, 2);
        root.Children.Add(help);
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = root };
        border.SetResourceReference(Border.BorderBrushProperty, "Surface.Border");
        Content = border;
        Tab = tab;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtrW(handle, GwlExStyle).ToInt64() | ToolWindow | (keyboard ? 0 : NoActivate);
            _ = SetWindowLongPtrW(handle, GwlExStyle, (nint)style);
            if (!keyboard && PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(NoMouseActivation);
        };
        Loaded += (_, _) => { if (keyboard) { Activate(); Focus(); } };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (keyboard) Dismiss(returnFocus: false); };
    }

    public TerminalTab Tab
    {
        get => _preview.Tab!;
        set
        {
            _preview.Tab = value;
            DataContext = value;
            SetBinding(TitleProperty, new Binding(nameof(TerminalTab.Label)) { StringFormat = "{0} — Preview — OverShell" });
        }
    }

    internal TerminalPreviewControl Preview => _preview;
    public bool KeyboardPreview => _keyboard;
    public bool ReturnFocus { get; private set; }
    public event Action<TerminalTab>? OpenRequested;
    public event Action<int>? NavigateRequested;
    public void ApplySettings(DashboardSettings settings) => _preview.Settings = settings;

    private Button Button(string glyph, string hint, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = hint, Style = (Style)FindResource("ChromeIconButton"), Focusable = _keyboard };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action();
        return button;
    }

    private void Open()
    {
        var tab = Tab;
        Dismiss(returnFocus: false);
        OpenRequested?.Invoke(tab);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Space: e.Handled = true; Dismiss(returnFocus: true); break;
            case Key.Return: e.Handled = true; Open(); break;
            case Key.Left or Key.Up: e.Handled = true; NavigateRequested?.Invoke(-1); break;
            case Key.Right or Key.Down: e.Handled = true; NavigateRequested?.Invoke(1); break;
        }
    }

    public void Dismiss(bool returnFocus)
    {
        if (_closing) return;
        _dismissRequested = true;
        ReturnFocus = returnFocus;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        if (!_dismissRequested && _keyboard && IsActive) ReturnFocus = true;
        _preview.Tab = null;
        DataContext = null;
        BindingOperations.ClearBinding(this, TitleProperty);
        base.OnClosing(e);
    }

    public void Place(FrameworkElement? anchor = null)
    {
        if (Owner is not { } owner) return;
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var monitor = MonitorFromWindow(ownerHandle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(ownerHandle, out var ownerRect)) return;
        var work = info.Work.ToRect();
        var dpi = GetDpiForWindow(ownerHandle) / 96d;
        var width = Math.Min(900 * dpi, work.Width * 0.85);
        var height = Math.Min(600 * dpi, work.Height * 0.85);
        Rect? near = null;
        if (anchor is { IsVisible: true })
        {
            var start = anchor.PointToScreen(new Point());
            var end = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            near = new Rect(start, end);
        }
        var placed = PlaceWithin(work, ownerRect.ToRect(), near, new Size(width, height));
        _ = SetWindowPos(new WindowInteropHelper(this).Handle, 0, (int)placed.X, (int)placed.Y, (int)placed.Width, (int)placed.Height, 0x0004 | 0x0010);
        _preview.InvalidatePlacement();
    }

    internal static Rect PlaceWithin(Rect work, Rect owner, Rect? anchor, Size desired)
    {
        var width = Math.Min(desired.Width, work.Width);
        var height = Math.Min(desired.Height, work.Height);
        var x = owner.Left + (owner.Width - width) / 2;
        var y = owner.Top + (owner.Height - height) / 2;
        if (anchor is { } a)
        {
            x = a.Right + 12;
            if (x + width > work.Right) x = a.Left - width - 12;
            if (x < work.Left) x = work.Left + (work.Width - width) / 2;
            y = a.Top;
        }
        return new Rect(Math.Clamp(x, work.Left, work.Right - width), Math.Clamp(y, work.Top, work.Bottom - height), width, height);
    }

    private static nint NoMouseActivation(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (message != 0x0021) return 0;
        handled = true;
        return 3; // MA_NOACTIVATE: hover/click must not redirect keyboard input
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
