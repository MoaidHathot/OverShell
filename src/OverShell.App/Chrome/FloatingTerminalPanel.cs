using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OverShell.App.Terminal.Search;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>
/// An in-window terminal presentation: no Window, Popup or additional top-level HWND.
/// Native terminals paint above WPF. The underlying live view is temporarily laid out
/// outside the owner's client area, with a styled preview in its place. It keeps its
/// grid and native input handle, including during a non-activating presentation.
/// </summary>
internal sealed class FloatingTerminalPanel : Grid
{
    private readonly Panel _area;
    private readonly Canvas _parking = new();
    private readonly Canvas _canvas = new();
    private readonly Grid _host;
    private readonly Border _frame;
    private readonly TerminalPreviewControl _preview;
    private readonly StackPanel _bars;
    private ParkedView? _parked;
    private FloatingSettings _settings;
    private Rect _bounds = Rect.Empty;
    private bool _customBounds;
    private bool _maximized;
    private bool _released;
    private Point? _dragStart;
    private Rect _dragBounds;
    private readonly double _originalOpacity;

    private sealed record ParkedView(TerminalTab Tab, Panel Parent, double Width, double Height);

    internal FloatingTerminalPanel(Window owner, Panel area, TerminalTab tab, TerminalSearch search, PromptBar prompt, FloatingSettings settings)
    {
        OwnerWindow = owner;
        _area = area;
        Tab = tab;
        _settings = settings;
        _originalOpacity = tab.Surface.BackgroundOpacity;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Focusable = false;
        Panel.SetZIndex(this, 100);

        // A WPF drawing can sit beneath the panel chrome; another native mirror cannot.
        _preview = new TerminalPreviewControl { Settings = new DashboardSettings { PreviewMode = "styled" }, IsHitTestVisible = false };
        Children.Add(_preview);
        Children.Add(_parking);
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(48, 0, 0, 0)) };
        backdrop.MouseLeftButtonDown += (_, e) => { HideRequested?.Invoke(); e.Handled = true; };
        Children.Add(backdrop);
        Children.Add(_canvas);

        _host = new Grid { Background = tab.Background };
        var find = new FindBar { Visibility = Visibility.Collapsed };
        Find = new FindBarController(find, owner, search);
        PromptBar = prompt;
        _bars = new StackPanel();
        _bars.Children.Add(find);
        _bars.Children.Add(prompt);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        CaptionSurface = BuildCaption();
        body.Children.Add(CaptionSurface);
        Grid.SetRow(_host, 1);
        body.Children.Add(_host);
        Grid.SetRow(_bars, 2);
        body.Children.Add(_bars);
        var resize = new Thumb { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = new FrameworkElementFactory(typeof(Border)) } };
        Grid.SetRow(resize, 1);
        resize.DragDelta += (_, e) => ResizeBy(e.HorizontalChange, e.VerticalChange);
        // The grip lives in the WPF frame margin rather than over the native terminal body.
        var chrome = new Grid();
        body.Margin = new Thickness(0, 0, 0, 14);
        chrome.Children.Add(body);
        Grid.SetRow(resize, 0);
        chrome.Children.Add(resize);
        _frame = new Border { Child = chrome, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        _frame.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        _frame.SetResourceReference(Border.BorderBrushProperty, "Surface.Border");
        _frame.PreviewMouseDown += (_, _) => InputRequested?.Invoke();
        _frame.GotKeyboardFocus += (_, _) => InputRequested?.Invoke();
        _canvas.Children.Add(_frame);
        SizeChanged += (_, _) => ArrangePanel();
    }

    internal Window OwnerWindow { get; }
    internal TerminalTab Tab { get; }
    internal TerminalTab? BackgroundTab { get; private set; }
    internal PromptBar PromptBar { get; }
    internal FindBarController Find { get; }
    internal FrameworkElement CaptionSurface { get; }
    internal Rect Bounds => _bounds;
    internal bool IsInputWithin => _frame.IsKeyboardFocusWithin || ShortcutRouter.FocusedWindow() == Tab.TerminalHwnd;
    internal bool HasBackgroundPreview => _preview.Tab is not null;
    internal bool HasParkedBackground => _parked is not null;
    internal event Action? HideRequested;
    internal event Action? OpenNormallyRequested;
    internal event Action? TerminateRequested;
    internal event Action? InputRequested;

    internal void Host(TerminalTab? background)
    {
        BackgroundTab = background;
        if (background is not null)
        {
            _preview.Tab = background;
            _preview.Margin = background.View.Margin;
            if (background != Tab && background.View.IsVisible && Window.GetWindow(background.View) == OwnerWindow && background.View.Parent is Panel parent)
            {
                var view = background.View;
                background.PresentationParked = true;
                _parked = new(background, parent, view.Width, view.Height);
                view.Width = Math.Max(1, view.ActualWidth);
                view.Height = Math.Max(1, view.ActualHeight);
                parent.Children.Remove(view);
                _parking.Children.Add(view);
                Canvas.SetLeft(view, Math.Max(OwnerWindow.ActualWidth, _area.ActualWidth) * 2 + 100);
            }
        }
        _host.Children.Add(Tab.View);
        Tab.SetFloating(true);
        // The panel has an opaque backing, unlike the top-level window's acrylic body.
        Tab.Surface.BackgroundOpacity = 1;
        _area.Children.Add(this);
        UpdateLayout();
        ArrangePanel();
    }

    internal FrameworkElement Release()
    {
        if (_released) return Tab.View;
        _released = true;
        Find.Dispose();
        _host.Children.Remove(Tab.View);
        _bars.Children.Remove(PromptBar);
        _preview.Tab = null;
        if (_parked is { } parked)
        {
            var view = parked.Tab.View;
            parked.Tab.PresentationParked = false;
            _parking.Children.Remove(view);
            Canvas.SetLeft(view, 0);
            view.Width = parked.Width;
            view.Height = parked.Height;
            parked.Parent.Children.Add(view);
            _parked = null;
        }
        Tab.SetFloating(false);
        Tab.Surface.BackgroundOpacity = _originalOpacity;
        _area.Children.Remove(this);
        return Tab.View;
    }

    internal void ApplySettings(FloatingSettings settings)
    {
        _settings = settings;
        _customBounds = false;
        ArrangePanel();
    }

    internal void ResizeBy(double x, double y)
    {
        if (_bounds.IsEmpty || !double.IsFinite(x) || !double.IsFinite(y)) return;
        _customBounds = true;
        _maximized = false;
        _bounds = FloatingPlacement.Constrain(new Rect(_bounds.X, _bounds.Y, Math.Max(1, _bounds.Width + x), Math.Max(1, _bounds.Height + y)), RenderSize);
        ArrangePanel();
    }

    private void ArrangePanel()
    {
        if (_released || ActualWidth <= 0 || ActualHeight <= 0) return;
        _bounds = _maximized ? new Rect(RenderSize) : _customBounds ? FloatingPlacement.Constrain(_bounds, RenderSize) : FloatingPlacement.Calculate(RenderSize, _settings);
        Canvas.SetLeft(_frame, _bounds.X);
        Canvas.SetTop(_frame, _bounds.Y);
        _frame.Width = _bounds.Width;
        _frame.Height = _bounds.Height;
        if (_parked is { } parked)
        {
            var view = parked.Tab.View;
            view.Width = Math.Max(1, ActualWidth - view.Margin.Left - view.Margin.Right);
            view.Height = Math.Max(1, ActualHeight - view.Margin.Top - view.Margin.Bottom);
            Canvas.SetLeft(view, Math.Max(OwnerWindow.ActualWidth, ActualWidth) * 2 + 100);
        }
    }

    private FrameworkElement BuildCaption()
    {
        var header = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button("\uE8A7", "Open in regular view", () => OpenNormallyRequested?.Invoke()));
        buttons.Children.Add(Button("\uE74D", "Terminate tab and session", () => TerminateRequested?.Invoke()));
        buttons.Children.Add(Button("\uE922", "Expand / restore inside this window", () => { _maximized = !_maximized; ArrangePanel(); }));
        buttons.Children.Add(Button("\uE8BB", "Hide floating terminal (session keeps running)", () => HideRequested?.Invoke()));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        var label = new TextBlock { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(TerminalTab.DisplayLabel)) { Source = Tab });
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
        header.Children.Add(label);
        var caption = new Border { Height = 36, Child = header };
        caption.SetResourceReference(Border.BackgroundProperty, "Surface.Chrome");
        caption.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject node && Ancestor<Button>(node) is not null) return;
            if (e.ClickCount == 2) { _maximized = !_maximized; ArrangePanel(); return; }
            _dragStart = e.GetPosition(this);
            _dragBounds = _bounds;
            caption.CaptureMouse();
            e.Handled = true;
        };
        caption.MouseMove += (_, e) =>
        {
            if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _dragBounds.IsEmpty) return;
            var delta = e.GetPosition(this) - start;
            _maximized = false;
            _customBounds = true;
            _bounds = FloatingPlacement.Constrain(new Rect(_dragBounds.X + delta.X, _dragBounds.Y + delta.Y, _dragBounds.Width, _dragBounds.Height), RenderSize);
            ArrangePanel();
        };
        caption.MouseLeftButtonUp += (_, _) => { _dragStart = null; caption.ReleaseMouseCapture(); };
        caption.LostMouseCapture += (_, _) => _dragStart = null;
        return caption;
    }

    private static Button Button(string glyph, string hint, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = hint, Width = 30, Height = 30, Focusable = false };
        button.SetResourceReference(StyleProperty, "ChromeIconButton");
        button.Click += (_, _) => action();
        return button;
    }

    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T found) return found;
        return null;
    }
}
