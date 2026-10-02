using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OverShell.App.Chrome;

/// <summary>
/// The harness mark on a tab item, sidebar row or dashboard card (DESIGN.md §12.13): the
/// harness's vector icon drawn with <see cref="Fill"/>, or — for a rule set without one —
/// its text glyph. One control so the five places that show it stay identical, and so a
/// skin recolours it through the same brush as the text beside it.
/// </summary>
public sealed class HarnessIcon : Control
{
    public static readonly DependencyProperty TabProperty =
        DependencyProperty.Register(nameof(Tab), typeof(TerminalTab), typeof(HarnessIcon), new PropertyMetadata(null, OnTabChanged));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(HarnessIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(HarnessIcon), new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly Path _path;
    private readonly TextBlock _text;
    private TerminalTab? _subscribed;

    public HarnessIcon()
    {
        _path = new Path { Stretch = Stretch.Uniform, SnapsToDevicePixels = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Fill)) { Source = this });
        _path.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(Size)) { Source = this });
        _path.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(Size)) { Source = this });
        RenderOptions.SetEdgeMode(_path, EdgeMode.Unspecified);

        _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Symbol") };
        _text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Fill)) { Source = this });
        _text.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding(nameof(Size)) { Source = this });

        var grid = new Grid();
        grid.Children.Add(_path);
        grid.Children.Add(_text);
        AddVisualChild(grid);
        AddLogicalChild(grid);
        _content = grid;
        Unloaded += (_, _) => Subscribe(null);
        Loaded += (_, _) => { Subscribe(Tab); Update(); };
    }

    private readonly Grid _content;

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

    /// <summary>Icon edge and glyph font size, in DIPs.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _content;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        _content.Measure(constraint);
        return _content.DesiredSize;
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size arrangeBounds)
    {
        _content.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
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
        var geometry = tab?.IconGeometry;
        _path.Data = geometry;
        _path.Visibility = geometry is null ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = tab is { ShowsTextGlyph: true } ? tab.Glyph : string.Empty;
        _text.Visibility = _text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
