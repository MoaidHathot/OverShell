using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OverShell.App.Chrome;

/// <summary>
/// The harness mark on a tab item, sidebar row, dashboard card or tear-off caption
/// (DESIGN.md 12.13, 12.15): the harness's vector icon drawn with <see cref="Fill"/>, or -
/// for a rule set without one - its text glyph. One control so every place that shows it
/// stays identical, and so a skin recolours it through the same brush as the text beside it.
/// <para>
/// Drawn in <see cref="OnRender"/> rather than through a <c>Path</c>, so the box is a whole
/// number of <em>device</em> pixels with its origin on a pixel: at 150 % an 11 DIP box is
/// 16.5 px, and every edge of the shape lands on a half pixel - the grey fuzz the first
/// vector icons had. A pixel-art geometry (a grid of at most 8 units, OpenCode's mark) gets
/// a whole number of pixels per unit, so its edges are exact; a 16-unit vector gets an
/// integer box and anti-aliasing. A <c>.Muted</c> companion geometry is drawn through the
/// same transform at reduced opacity - the second tone of a two-tone mark.
/// </para>
/// </summary>
public sealed class HarnessIcon : Control
{
    public static readonly DependencyProperty TabProperty =
        DependencyProperty.Register(nameof(Tab), typeof(TerminalTab), typeof(HarnessIcon), new PropertyMetadata(null, OnTabChanged));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(HarnessIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(HarnessIcon), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A geometry on a grid this small is pixel art: whole pixels per unit.</summary>
    private const double PixelArtGrid = 8;

    /// <summary>The second tone's opacity - the grey block of OpenCode's mark against its white frame.</summary>
    private const double MutedOpacity = 0.4;

    private readonly TextBlock _text;
    private TerminalTab? _subscribed;
    private Geometry? _geometry;
    private Geometry? _muted;

    public HarnessIcon()
    {
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Segoe UI Symbol") };
        _text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Fill)) { Source = this });
        _text.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding(nameof(Size)) { Source = this });
        AddVisualChild(_text);
        AddLogicalChild(_text);

        Unloaded += (_, _) => Subscribe(null);
        Loaded += (_, _) => { Subscribe(Tab); Update(); };
    }

    public TerminalTab? Tab
    {
        get => (TerminalTab?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Nominal icon height and glyph font size, in DIPs. The drawn box is this rounded to device pixels (pixel art: rounded down to whole units).</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>The box the icon is drawn in, in DIPs - whole device pixels at this element's DPI. For the self-test.</summary>
    public System.Windows.Size DrawnBox => Box(DpiScale());

    /// <summary>The box the icon would be drawn in at a given DPI scale, in DIPs. For the self-test.</summary>
    public System.Windows.Size BoxAt(double scale) => Box(scale);

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _text;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        _text.Measure(constraint);
        if (_geometry is null)
        {
            return _text.Visibility == Visibility.Visible ? _text.DesiredSize : default;
        }

        return Box(DpiScale());
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size arrangeBounds)
    {
        _text.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_geometry is null)
        {
            return;
        }

        var scale = DpiScale();
        var box = Box(scale);
        var bounds = _geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // Centre the box in the arranged size, on whole device pixels.
        var x = Math.Round((RenderSize.Width - box.Width) * scale / 2) / scale;
        var y = Math.Round((RenderSize.Height - box.Height) * scale / 2) / scale;
        var unit = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);

        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(unit, unit));
        dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
        dc.DrawGeometry(Fill, null, _geometry);
        if (_muted is not null)
        {
            dc.PushOpacity(MutedOpacity);
            dc.DrawGeometry(Fill, null, _muted);
            dc.Pop();
        }

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private double DpiScale()
    {
        var dpi = PresentationSource.FromVisual(this) is not null ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1.0;
        return dpi > 0 ? dpi : 1.0;
    }

    /// <summary>The icon's box in DIPs for a DPI scale: a whole number of device pixels, the geometry's aspect kept.</summary>
    private System.Windows.Size Box(double scale)
    {
        if (_geometry is null)
        {
            return default;
        }

        var bounds = _geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return default;
        }

        double heightPx;
        double widthPx;
        if (bounds.Width <= PixelArtGrid && bounds.Height <= PixelArtGrid)
        {
            // Pixel art: whole pixels per unit, never taller than asked for.
            var unit = Math.Max(1, Math.Floor(Size * scale / bounds.Height));
            heightPx = unit * bounds.Height;
            widthPx = unit * bounds.Width;
        }
        else
        {
            heightPx = Math.Max(1, Math.Round(Size * scale));
            widthPx = Math.Max(1, Math.Round(heightPx * bounds.Width / bounds.Height));
        }

        return new System.Windows.Size(widthPx / scale, heightPx / scale);
    }

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (HarnessIcon)d;
        icon.Subscribe(e.NewValue as TerminalTab);
        icon.Update();
    }

    private void Subscribe(TerminalTab? tab)
    {
        if (ReferenceEquals(_subscribed, tab))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnTabPropertyChanged;
        }

        _subscribed = tab;
        if (tab is not null)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
        }
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalTab.IconGeometry) or nameof(TerminalTab.Glyph) or nameof(TerminalTab.IsAgent) or null)
        {
            Update();
        }
    }

    private void Update()
    {
        var tab = Tab;
        _geometry = tab?.IconGeometry;
        _muted = tab?.IconMutedGeometry;
        _text.Text = tab is { ShowsTextGlyph: true } ? tab.Glyph : string.Empty;
        _text.Visibility = _text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InvalidateMeasure();
        InvalidateVisual();
    }
}
