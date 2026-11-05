using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.App.Terminal;

namespace OverShell.App.Chrome;

/// <summary>
/// A background at the terminal's original scale. Live mirrors preserve native glyphs,
/// colours and alpha; four clips leave a hole for the inset panel. A floated terminal
/// never mirrors itself behind itself. Opaque renderers use a native-sized styled drawing.
/// </summary>
internal sealed class FloatingBackdrop : FrameworkElement, IDisposable
{
    private readonly Window _owner;
    private readonly ITerminalPreview?[] _mirrors = new ITerminalPreview?[4];
    private TerminalTab? _source;
    private TerminalTab? _base;
    private StyledPreviewSource? _styled;
    private DrawingImage? _fallbackImage;
    private readonly DispatcherTimer _retry;
    private DispatcherOperation? _geometryUpdate;
    private Rect _hole;
    private Rect _destination;
    private bool _live;
    private bool _disposed;
    private Brush _tint = Brushes.Transparent;

    internal FloatingBackdrop(Window owner)
    {
        _owner = owner;
        _retry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _retry.Tick += (_, _) => UpdateGeometry();
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateGeometry();
        SizeChanged += (_, _) => InvalidateGeometry();
        _owner.DpiChanged += DpiChanged;
        _owner.StateChanged += StateChanged;
    }

    internal bool HasSource => _source is not null;
    internal string Backend => _source is null ? "tint" : _live ? "live" : "styled";
    internal Rect Destination => _destination;

    internal void SetSource(TerminalTab? background, TerminalTab floating)
    {
        _base = background;
        _source = background == floating ? null : background;
        if (_source is not null)
        {
            _source.View.SizeChanged += SourceSizeChanged;
            _source.Surface.ViewportChanged += SourceViewportChanged;
        }
        ApplyAppearance();
    }

    internal void ApplyAppearance()
    {
        if (_base is not null)
        {
            var brush = _base.Background.CloneCurrentValue();
            brush.Opacity = _base.Surface.BackgroundOpacity;
            _tint = brush;
        }
        else _tint = Brushes.Transparent;
        DrawingChanged();
        UpdateGeometry();
    }

    internal void SetHole(Rect hole)
    {
        _hole = hole;
        UpdateGeometry();
    }

    private void DpiChanged(object sender, DpiChangedEventArgs e) => InvalidateGeometry();
    private void StateChanged(object? sender, EventArgs e) => UpdateGeometry();
    private void SourceSizeChanged(object sender, SizeChangedEventArgs e) => InvalidateGeometry();
    private void SourceViewportChanged(object? sender, EventArgs e) => InvalidateGeometry();

    private void InvalidateGeometry()
    {
        if (_disposed || _geometryUpdate?.Status == DispatcherOperationStatus.Pending) return;
        _geometryUpdate = Dispatcher.BeginInvoke(() => { _geometryUpdate = null; UpdateGeometry(); }, DispatcherPriority.Loaded);
    }

    private void UpdateGeometry()
    {
        if (_disposed || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 || _hole.IsEmpty || _hole.Width <= 0 || _hole.Height <= 0) return;
        _live = false;
        if (_source is { } source && _owner.IsVisible && _owner.WindowState != WindowState.Minimized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var margin = source.View.Margin;
            if (GetWindowRect(source.TerminalHwnd, out var native))
                _destination = new Rect(margin.Left, margin.Top, Math.Max(1, native.Right - native.Left) / dpi.DpiScaleX, Math.Max(1, native.Bottom - native.Top) / dpi.DpiScaleY);
            if (!_destination.IsEmpty && source.Surface.Capabilities.HasFlag(SurfaceCapabilities.LivePreview))
            {
                var target = new WindowInteropHelper(_owner).Handle;
                var offset = TransformToAncestor(_owner).Transform(new Point());
                var toPixels = new Matrix(dpi.DpiScaleX, 0, 0, dpi.DpiScaleY, offset.X * dpi.DpiScaleX, offset.Y * dpi.DpiScaleY);
                Rect Pixels(Rect rect) => FloatingPlacement.SnapToPixels(Rect.Transform(rect, toPixels), new DpiScale(1, 1));
                var destination = Pixels(_destination);
                // Partition after snapping: independently rounding each clip outwards makes
                // adjacent mirrors overlap by a pixel and doubles the background tint there.
                var rectangles = Regions(Pixels(new Rect(RenderSize)), Pixels(_hole));
                _live = true;
                for (var i = 0; i < _mirrors.Length; i++)
                {
                    var clip = i < rectangles.Count ? Rect.Intersect(destination, rectangles[i]) : Rect.Empty;
                    if (clip.IsEmpty || clip.Width <= 0 || clip.Height <= 0) { _mirrors[i]?.Dispose(); _mirrors[i] = null; continue; }
                    _mirrors[i] ??= source.Surface.CreatePreview(target);
                    if (_mirrors[i]?.Update(destination, clip, 0) != true) _live = false;
                }
            }
            if (_live) { ReleaseStyled(); _retry.Stop(); }
            else
            {
                ReleaseMirrors();
                if (_styled is null)
                {
                    _styled = StyledPreviewSource.Acquire(source, 250, DrawingChanged);
                    DrawingChanged();
                }
                if (source.Surface.Capabilities.HasFlag(SurfaceCapabilities.LivePreview)) _retry.Start();
            }
        }
        else { ReleaseMirrors(); _retry.Stop(); }
        InvalidateVisual();
    }

    internal static IReadOnlyList<Rect> Regions(Rect area, Rect hole)
    {
        var cut = Rect.Intersect(area, hole);
        if (cut.IsEmpty) return [area];
        return new[]
        {
            new Rect(area.Left, area.Top, area.Width, cut.Top - area.Top),
            new Rect(area.Left, cut.Bottom, area.Width, area.Bottom - cut.Bottom),
            new Rect(area.Left, cut.Top, cut.Left - area.Left, cut.Height),
            new Rect(cut.Right, cut.Top, area.Right - cut.Right, cut.Height),
        }.Where(r => r.Width > 0 && r.Height > 0).ToArray();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var roundedHole = new RectangleGeometry(_hole, 10, 10);
        if (_live)
        {
            // Native clips leave a rectangular hole. Tint its tiny corners up to the
            // rounded frame, rather than exposing untinted desktop through those corners.
            drawing.DrawGeometry(_tint, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(_hole), roundedHole));
            return;
        }
        // One drawing/clip avoids antialiased seams between four translucent rectangles.
        drawing.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(RenderSize)), roundedHole));
        if (_fallbackImage is { } image && !_destination.IsEmpty)
        {
            // Image dimensions are source pixels; one source pixel remains one device pixel.
            var dpi = VisualTreeHelper.GetDpi(this);
            drawing.DrawImage(image, new Rect(_destination.Location, new Size(image.Width / dpi.DpiScaleX, image.Height / dpi.DpiScaleY)));
        }
        else drawing.DrawRectangle(_tint, null, new Rect(RenderSize));
        drawing.Pop();
    }

    private void DrawingChanged()
    {
        _fallbackImage = _styled?.Image is { } image && _source is { Background: SolidColorBrush background } source
            ? PreviewDrawing.WithDefaultBackgroundOpacity(image, background.Color, source.Surface.BackgroundOpacity) : _styled?.Image;
        InvalidateVisual();
    }
    private void ReleaseStyled() { _styled?.Release(DrawingChanged); _styled = null; _fallbackImage = null; }
    private void ReleaseMirrors()
    {
        for (var i = 0; i < _mirrors.Length; i++) { _mirrors[i]?.Dispose(); _mirrors[i] = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _geometryUpdate?.Abort();
        _geometryUpdate = null;
        _retry.Stop();
        ReleaseMirrors();
        ReleaseStyled();
        if (_source is not null)
        {
            _source.View.SizeChanged -= SourceSizeChanged;
            _source.Surface.ViewportChanged -= SourceViewportChanged;
        }
        _owner.DpiChanged -= DpiChanged;
        _owner.StateChanged -= StateChanged;
        _source = _base = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);
}
