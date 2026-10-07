using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace OverShell.App.Chrome;

/// <summary>
/// A second top-level window holding one live tab (DESIGN.md §12.12). The tab's surface
/// is moved here from the main window and back — same HWND, same session, verified by
/// spike 2 (§12.7) — so nothing about the terminal restarts. The tab stays in the main
/// window's collection: detection, the sidebar, the dashboard and notifications carry
/// on. Closing this window re-attaches; it never ends the session.
/// <para>
/// Since §12.14 it wears the main window's chrome rather than the stock frame: the same
/// caption surface and backdrop, the tab's state dot, harness icon, label and detail in
/// the caption, the same caption buttons - so a tear-off and the main window look like
/// one program. The close button re-attaches (its tooltip says so).
/// </para>
/// </summary>
public sealed class TearOffWindow : Window
{
    private const double CaptionHeight = 36;

    private readonly Grid _host;
    private readonly Border _hostTint;
    private readonly Border _root;
    private readonly Border _caption;
    private readonly Button _maximize;
    private bool _reattachOnClose = true;

    public TearOffWindow(Window owner, TerminalTab tab)
    {
        Tab = tab;
        Title = $"{tab.Label} — OverShell";
        Width = Math.Max(600, owner.ActualWidth * 0.7);
        Height = Math.Max(400, owner.ActualHeight * 0.7);
        Left = owner.Left + 60;
        Top = owner.Top + 60;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;
        MinWidth = 400;
        MinHeight = 240;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // The main window's chrome: no system caption, our own buttons, a resize border.
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = default,
            GlassFrameThickness = new Thickness(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
        });

        // Nothing under a composed terminal from the first frame on (see WindowChromeInterop.SetClipsChildren).
        _host = new Grid { Background = Terminal.TerminalFactory.Composition.Enabled ? Brushes.Transparent : tab.Background };
        _hostTint = new Border { BorderBrush = tab.Background, BorderThickness = TerminalTab.ViewMargin, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _host.Children.Add(_hostTint);
        _maximize = CaptionButton("\uE922", "Maximize", (_, _) => ToggleMaximize());
        _caption = BuildCaption(tab);

        // The same find bar as the main window's, below the terminal (12.14).
        var findBar = new FindBar { Visibility = Visibility.Collapsed };
        Find = new FindBarController(findBar, this, (owner as MainWindow)?.Search ?? new Terminal.Search.TerminalSearch());

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_caption, 0);
        Grid.SetRow(_host, 1);
        Grid.SetRow(findBar, 2);
        layout.Children.Add(_caption);
        layout.Children.Add(_host);
        layout.Children.Add(findBar);

        _root = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = layout,
        };
        _root.SetResourceReference(Border.BorderBrushProperty, "Surface.Border");
        Content = _root;

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TerminalTab.Label) or nameof(TerminalTab.Title))
            {
                Title = $"{tab.Label} — OverShell";
            }
        };

        Activated += (_, _) => Tab.Surface.Focus();
        StateChanged += (_, _) => SyncMaximizeState();
        SourceInitialized += (_, _) => ApplyBackdrop();
        DpiChanged += (_, _) => ApplyBackdrop();
    }

    public TerminalTab Tab { get; }

    /// <summary>This window's find bar, over its one tab.</summary>
    internal FindBarController Find { get; }

    /// <summary>Raised when the window closes with the surface still inside it; the main window takes it back.</summary>
    public event Action<TerminalTab>? ReattachRequested;

    /// <summary>Puts the tab's view into this window. The main window has already removed it from its host.</summary>
    public void Host()
    {
        _host.Children.Add(Tab.View);
        Tab.Detached = true;
    }

    /// <summary>Takes the view out again for the main window; closing afterwards does not re-attach twice.</summary>
    public FrameworkElement Release()
    {
        _reattachOnClose = false;
        Find.Forget(Tab);
        _host.Children.Remove(Tab.View);
        Tab.Detached = false;
        return Tab.View;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_reattachOnClose && !e.Cancel)
        {
            ReattachRequested?.Invoke(Tab);
        }
    }

    /// <summary>The terminal's own HWND, for the router and diagnostics.</summary>
    public nint TerminalHwnd => MainWindow.FindTerminalHwnd(Tab.View);

    /// <summary>The host's colour follows the tab's scheme so a resize never flashes another colour.</summary>
    public Brush HostBackground => _host.Background;

    /// <summary>The caption surface, for the self-test's render.</summary>
    internal FrameworkElement CaptionSurface => _caption;

    // ------------------------------------------------------------------ chrome

    private Border BuildCaption(TerminalTab tab)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // State dot, like the tab item's.
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetBinding(Border.BackgroundProperty, new Binding(nameof(TerminalTab.StateBrush)) { Source = tab });
        Grid.SetColumn(dot, 0);

        var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var icon = new HarnessIcon { Tab = tab, Size = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(HarnessIcon.FillProperty, "Text.Secondary");
        icon.SetBinding(VisibilityProperty, new Binding(nameof(TerminalTab.ShowsHarnessIcon)) { Source = tab, Converter = new BooleanToVisibilityConverter() });
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(TerminalTab.Label)) { Source = tab });
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
        label.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Body");
        var detail = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(TerminalTab.Detail)) { Source = tab });
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");
        detail.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
        detail.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");
        text.Children.Add(icon);
        text.Children.Add(label);
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(CaptionButton("\uE921", "Minimize", (_, _) => WindowState = WindowState.Minimized));
        buttons.Children.Add(_maximize);
        var close = CaptionButton("\uE8BB", "Back to the main window  (Ctrl+Shift+A)", (_, _) => Close());
        close.SetResourceReference(StyleProperty, "CloseCaptionButton");
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 2);

        grid.Children.Add(dot);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        var caption = new Border { Height = CaptionHeight, Child = grid };
        caption.SetResourceReference(Border.BackgroundProperty, "Surface.Chrome");
        caption.MouseLeftButtonDown += Caption_MouseLeftButtonDown;
        return caption;
    }

    private static Button CaptionButton(string glyph, string tooltip, RoutedEventHandler click)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip };
        button.SetResourceReference(StyleProperty, "CaptionButton");
        button.Click += click;
        return button;
    }

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

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
            // Restore under the cursor, as the main window does.
            var cursor = e.GetPosition(this);
            var ratio = ActualWidth > 0 ? cursor.X / ActualWidth : 0.5;
            WindowState = WindowState.Normal;
            var screen = PointToScreen(cursor);
            Left = screen.X - (RestoreBounds.Width * ratio);
            Top = screen.Y - (e.GetPosition(this).Y / 2);
        }

        DragMove();
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node) ?? System.Windows.LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void SyncMaximizeState()
    {
        var maximized = WindowState == WindowState.Maximized;
        _maximize.Content = maximized ? "\uE923" : "\uE922";
        _maximize.ToolTip = maximized ? "Restore" : "Maximize";

        // A maximized WindowChrome window overhangs the work area by its resize border.
        var resize = SystemParameters.WindowResizeBorderThickness;
        _root.Margin = maximized ? new Thickness(resize.Left, resize.Top, resize.Right, resize.Bottom) : default;
        _root.BorderThickness = maximized ? default : new Thickness(1);
    }

    /// <summary>Re-applies the backdrop after a theme change (the main window calls it for every tear-off).</summary>
    public void ReapplyBackdrop() => ApplyBackdrop();

    /// <summary>The same backdrop as the main window over the caption band; opaque fallbacks when there is none.</summary>
    private void ApplyBackdrop()
    {
        // A composed terminal body (12.20) needs the backdrop behind the whole window and nothing painted under it.
        var composed = Terminal.TerminalFactory.Composition.Enabled;
        var active = WindowChromeInterop.Apply(this, WindowChromeInterop.Resolve(), CaptionHeight, 0, wholeClient: composed);
        _root.Background = active ? Brushes.Transparent : (Brush)FindResource("Surface.Base");
        // Nothing under a composed terminal (WPF cannot repaint under the child HWND); the margin ring carries the tint.
        _host.Background = composed && active ? Brushes.Transparent : Tab.Background;
        _hostTint.Opacity = Terminal.TerminalFactory.Composition.Opacity;
        _hostTint.Visibility = composed && active ? Visibility.Visible : Visibility.Collapsed;
        _caption.SetResourceReference(Border.BackgroundProperty, active ? "Surface.ChromeTranslucent" : "Surface.Chrome");
        TextOptions.SetTextRenderingMode(this, active ? TextRenderingMode.Grayscale : TextRenderingMode.ClearType);
    }
}
