using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace OverShell.App.Chrome;

/// <summary>One change-driven snapshot/drawing per tab, shared by its card and enlarged preview.</summary>
internal sealed class StyledPreviewSource
{
    private static readonly ConditionalWeakTable<TerminalTab, StyledPreviewSource> Sources = new();
    private static readonly SemaphoreSlim DrawingSlots = new(2);
    private readonly TerminalTab _tab;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<Action> _consumers = [];
    private CancellationTokenSource? _drawing;
    private long _version = -1;
    private long _viewportVersion;
    private long _capturedViewport = -1;
    private int _generation;
    private bool _busy;

    private StyledPreviewSource(TerminalTab tab)
    {
        _tab = tab;
        _timer = new DispatcherTimer(DispatcherPriority.Background, tab.View.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Capture();
    }

    public DrawingImage? Image { get; private set; }
    internal static int ActiveSources { get; private set; }
    internal static int PendingDrawings { get; private set; }
    internal static long CompletedDrawings { get; private set; }

    public static StyledPreviewSource Acquire(TerminalTab tab, int intervalMs, Action changed)
    {
        var source = Sources.GetValue(tab, static t => new(t));
        if (source._consumers.Add(changed) && source._consumers.Count == 1)
        {
            ActiveSources++;
            tab.Surface.ViewportChanged += source.ViewportChanged;
            tab.View.SizeChanged += source.SizeChanged;
        }
        source._timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        source._timer.Start();
        source.Capture();
        return source;
    }

    public void Release(Action changed)
    {
        if (!_consumers.Remove(changed) || _consumers.Count != 0) return;
        ActiveSources--;
        _timer.Stop();
        _tab.Surface.ViewportChanged -= ViewportChanged;
        _tab.View.SizeChanged -= SizeChanged;
        ++_generation;
        _drawing?.Cancel();
        Image = null;
        _version = -1;
    }

    private void ViewportChanged(object? sender, EventArgs e) => _viewportVersion++;
    private void SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => _viewportVersion++;

    private async void Capture()
    {
        if (_busy || _consumers.Count == 0 || (_version == _tab.OutputVersion && _capturedViewport == _viewportVersion)) return;
        _busy = true;
        var generation = _generation;
        var version = _tab.OutputVersion;
        var viewport = _viewportVersion;
        var cancellation = new CancellationTokenSource();
        _drawing = cancellation;
        try
        {
            var frame = _tab.Surface.CapturePreview();
            if (frame is null) return;
            var face = string.IsNullOrWhiteSpace(_tab.Profile.FontFace) ? "Cascadia Mono, Consolas" : _tab.Profile.FontFace;
            var points = _tab.Profile.FontSize ?? 12;
            var pixels = Math.Clamp(double.IsFinite(points) ? points : 12, 1, 100) * 96 / 72 * VisualTreeHelper.GetDpi(_tab.View).PixelsPerDip;
            PendingDrawings++;
            DrawingImage image;
            try
            {
                image = await Task.Run(async () =>
                {
                    await DrawingSlots.WaitAsync(cancellation.Token);
                    try { return PreviewDrawing.Build(frame, face, pixels, cancellation.Token); }
                    finally { DrawingSlots.Release(); }
                }, cancellation.Token);
            }
            finally { PendingDrawings--; }
            if (generation != _generation || _consumers.Count == 0 || cancellation.IsCancellationRequested) return;
            Image = image;
            _version = version;
            _capturedViewport = viewport;
            CompletedDrawings++;
            foreach (var consumer in _consumers.ToArray()) consumer();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Diagnostics.TraceLog.Agents.Write($"preview [{_tab.Id}]: {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_drawing, cancellation)) _drawing = null;
            cancellation.Dispose();
            _busy = false;
        }
    }
}
