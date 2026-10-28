using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.App.Terminal;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core.Previews;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>A read-only, aspect-fit preview. Native visuals exist only while this consumer is visible.</summary>
public sealed class TerminalPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(nameof(Tab), typeof(TerminalTab), typeof(TerminalPreviewControl), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(nameof(Settings), typeof(DashboardSettings), typeof(TerminalPreviewControl), new PropertyMetadata(new DashboardSettings(), Changed));
    private static readonly DependencyPropertyKey BackendPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Backend), typeof(string), typeof(TerminalPreviewControl), new PropertyMetadata("starting"));
    public static readonly DependencyProperty BackendProperty = BackendPropertyKey.DependencyProperty;
    private readonly DispatcherTimer _retry;
    private TerminalTab? _source;
    private Window? _owner;
    private ScrollViewer? _scroll;
    private ITerminalPreview? _mirror;
    private StyledPreviewSource? _styled;
    private bool _placementDirty = true;
    private bool _listening;

    public TerminalPreviewControl()
    {
        Focusable = false;
        _retry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _retry.Tick += (_, _) => UpdatePlacement();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => InvalidatePlacement();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Release(); else InvalidatePlacement(); };
    }

    public TerminalTab? Tab { get => (TerminalTab?)GetValue(TabProperty); set => SetValue(TabProperty, value); }
    public DashboardSettings Settings { get => (DashboardSettings)GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public string Backend => (string)GetValue(BackendProperty);
    internal bool HasDemand { get; private set; }
    internal bool HasDrawing => _styled?.Image is not null;

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TerminalPreviewControl)d;
        control.Stop();
        if (control.IsLoaded) control.Start();
        control.InvalidateVisual();
    }

    private void Start()
    {
        if (_listening) return;
        _source = Tab;
        _owner = Window.GetWindow(this);
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) { _scroll = scroll; break; }
        if (_source is not null) _source.PropertyChanged += SourceChanged;
        if (_owner is not null)
        {
            _owner.SizeChanged += OwnerSizeChanged;
            _owner.StateChanged += OwnerStateChanged;
            _owner.DpiChanged += OwnerDpiChanged;
            _owner.IsVisibleChanged += OwnerVisibleChanged;
        }
        if (_scroll is not null) _scroll.ScrollChanged += ScrollChanged;
        LayoutUpdated += OnLayoutUpdated;
        _listening = true;
        InvalidatePlacement();
    }

    private void Stop()
    {
        Release();
        if (_source is not null) _source.PropertyChanged -= SourceChanged;
        if (_owner is not null)
        {
            _owner.SizeChanged -= OwnerSizeChanged;
            _owner.StateChanged -= OwnerStateChanged;
            _owner.DpiChanged -= OwnerDpiChanged;
            _owner.IsVisibleChanged -= OwnerVisibleChanged;
        }
        if (_scroll is not null) _scroll.ScrollChanged -= ScrollChanged;
        LayoutUpdated -= OnLayoutUpdated;
        _source = null;
        _owner = null;
        _scroll = null;
        _listening = false;
    }

    internal void InvalidatePlacement()
    {
        _placementDirty = true;
        if (IsLoaded) Dispatcher.BeginInvoke(() => { if (_placementDirty) UpdatePlacement(); }, DispatcherPriority.Loaded);
    }

    private void OwnerSizeChanged(object sender, SizeChangedEventArgs e) => InvalidatePlacement();
    private void OnLayoutUpdated(object? sender, EventArgs e) { if (_placementDirty) UpdatePlacement(); }
    private void OwnerStateChanged(object? sender, EventArgs e) { if (_owner?.WindowState == WindowState.Minimized) Release(); else InvalidatePlacement(); }
    private void OwnerDpiChanged(object sender, DpiChangedEventArgs e) => InvalidatePlacement();
    private void OwnerVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) { if (_owner?.IsVisible != true) Release(); else InvalidatePlacement(); }
    private void ScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidatePlacement();
    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTab.ScreenText) && Backend == "text" && HasDemand) InvalidateVisual();
    }

    private void UpdatePlacement()
    {
        _placementDirty = false;
        if (!_listening || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0 || _owner is not { IsVisible: true } owner || owner.WindowState == WindowState.Minimized || _source is not { } tab)
        {
            Release();
            return;
        }
        var rectangle = TransformToAncestor(owner).TransformBounds(new Rect(RenderSize));
        var clip = Rect.Intersect(rectangle, new Rect(0, 0, owner.ActualWidth, owner.ActualHeight));
        if (_scroll is { } scroll)
        {
            var presenter = WindowsTerminalSurface.FindDescendant<ScrollContentPresenter>(scroll);
            var viewport = presenter is not null ? presenter.TransformToAncestor(owner).TransformBounds(new Rect(presenter.RenderSize))
                : scroll.TransformToAncestor(owner).TransformBounds(new Rect(scroll.RenderSize));
            clip.Intersect(viewport);
        }
        if (clip.IsEmpty || clip.Width < 1 || clip.Height < 1) { Release(); return; }
        HasDemand = true;
        var mode = Settings.EffectiveMode;
        if (mode == "auto" && tab.Surface.Capabilities.HasFlag(SurfaceCapabilities.LivePreview))
        {
            _mirror ??= tab.Surface.CreatePreview(new WindowInteropHelper(owner).Handle);
            var scale = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            if (_mirror is not null && _mirror.Update(Rect.Transform(rectangle, scale), Rect.Transform(clip, scale), 6 * scale.M11))
            {
                ReleaseStyled();
                _retry.Stop();
                SetBackend("live");
                return;
            }
            _mirror?.Dispose();
            _mirror = null;
            _retry.Start(); // a new/recreated swap chain may become ready after the first frame
        }
        else _retry.Stop();

        if (mode != "text" && tab.Surface.Capabilities.HasFlag(SurfaceCapabilities.StyledPreview))
        {
            _styled ??= StyledPreviewSource.Acquire(tab, Settings.EffectiveInterval, DrawingChanged);
            SetBackend("styled");
        }
        else
        {
            ReleaseStyled();
            SetBackend("text");
            if (tab.ScreenRows.Count == 0) tab.RequestScreen();
        }
    }

    private void SetBackend(string value)
    {
        if (Backend == value) return;
        SetValue(BackendPropertyKey, value);
        InvalidateVisual();
    }
    private void DrawingChanged() => InvalidateVisual();
    private void ReleaseStyled() { _styled?.Release(DrawingChanged); _styled = null; }
    private void Release()
    {
        HasDemand = false;
        _retry.Stop();
        _mirror?.Dispose();
        _mirror = null;
        ReleaseStyled();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (Tab is not { } tab || ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(RenderSize);
        context.PushClip(new RectangleGeometry(bounds, 6, 6));
        context.DrawRectangle(tab.Background, null, bounds);
        if (_mirror is null && _styled?.Image is { } image)
        {
            var fit = PreviewFit.Calculate(image.Width, image.Height, ActualWidth, ActualHeight);
            if (fit.Scale > 0) context.DrawImage(image, new Rect(fit.Left, fit.Top, fit.Width, fit.Height));
        }
        else if (Backend == "text")
        {
            var text = new FormattedText(tab.ScreenText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface((FontFamily)FindResource("Font.Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, tab.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip) { LineHeight = 15 };
            context.DrawText(text, new Point(10, ActualHeight - text.Height - 8));
        }
        context.Pop();
    }
}
