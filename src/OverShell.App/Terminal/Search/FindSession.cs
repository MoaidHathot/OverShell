using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.App.Overlay;

namespace OverShell.App.Terminal.Search;

/// <summary>
/// Find-in-buffer for one tab while its find bar is open (DESIGN.md §12.14). Owns the
/// search state - needle, matches, which one is current - and the overlay that tints the
/// other visible matches; the current match is the terminal's own selection, made through
/// UI Automation, which also scrolls it into view when it is hidden.
/// <para>
/// Matches are re-derived rather than tracked: a search costs a few milliseconds on a
/// worker thread, so output arriving, the viewport scrolling or the window moving simply
/// run it again, keeping the current match by its buffer position. Only an explicit step or
/// a new needle reveals a match; a refresh never moves the viewport under the user.
/// </para>
/// </summary>
internal sealed class FindSession : IDisposable
{
    /// <summary>Output is polled at this cadence; a scroll re-places highlights at once.</summary>
    private static readonly TimeSpan OutputPoll = TimeSpan.FromMilliseconds(200);

    private readonly Window _owner;
    private readonly TerminalSearch _search;
    private readonly DispatcherTimer _timer;
    private OverlayHost? _overlay;
    private FindResult? _result;
    private int _current = -1;
    private string _needle = string.Empty;
    private bool _matchCase;
    private long _seenOutput;
    private bool _busy;
    private bool _again;
    private Reveal _pending;
    private bool _suspended;
    private bool _disposed;

    private enum Reveal
    {
        None,
        Initial,
        Current,
    }

    public FindSession(TerminalTab tab, Window owner, TerminalSearch search)
    {
        Tab = tab;
        _owner = owner;
        _search = search;
        _seenOutput = tab.OutputVersion;

        tab.Surface.ViewportChanged += OnViewportChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = OutputPoll };
        _timer.Tick += (_, _) =>
        {
            var version = Tab.OutputVersion;
            if (version != _seenOutput)
            {
                _seenOutput = version;
                Refresh();
            }
        };
        _timer.Start();
    }

    public TerminalTab Tab { get; }

    /// <summary>Raised on the UI thread whenever the count or the current match changed.</summary>
    public event Action<FindSession>? Changed;

    public string Needle => _needle;

    public bool MatchCase => _matchCase;

    public int Count => _result?.Matches.Count ?? 0;

    public bool Truncated => _result?.Truncated == true;

    /// <summary>Zero-based position of the current match in document order; -1 without one.</summary>
    public int CurrentIndex => _current;

    /// <summary>The current match's screen geometry, empty when it is scrolled away.</summary>
    public IReadOnlyList<Rect> CurrentRects => _result is not null && _current >= 0 && _current < _result.Matches.Count
        ? _result.Matches[_current].Rects
        : [];

    public int VisibleCount => _result?.VisibleCount ?? 0;

    /// <summary>What the bar shows next to the box: "3 of 5", "500+ matches", "No matches", or nothing.</summary>
    public string Status
    {
        get
        {
            if (_needle.Length == 0 || _result is null)
            {
                return string.Empty;
            }

            if (_result.Matches.Count == 0)
            {
                return "No matches";
            }

            if (_result.Truncated)
            {
                return $"{_current + 1} of {TerminalSearch.MaxMatches}+";
            }

            return $"{_current + 1} of {_result.Matches.Count}";
        }
    }

    /// <summary>The overlay's state, for diagnostics.</summary>
    internal string DescribeOverlay() => _overlay?.Describe() ?? "none";

    /// <summary>A new needle (or case rule): searches and reveals the match nearest the view.</summary>
    public void SetQuery(string needle, bool matchCase)
    {
        if (_needle == needle && _matchCase == matchCase)
        {
            return;
        }

        _needle = needle;
        _matchCase = matchCase;

        if (needle.Length == 0)
        {
            _result = null;
            _current = -1;
            _overlay?.HideOverlay();
            Changed?.Invoke(this);
            return;
        }

        _pending = Reveal.Initial;
        Run();
    }

    /// <summary>
    /// Moves to the previous (<paramref name="direction"/> -1: up, older text) or the next
    /// (+1: down, newer) match, wrapping at either end, and reveals it.
    /// </summary>
    public void Step(int direction)
    {
        if (_result is null || _result.Matches.Count == 0)
        {
            return;
        }

        var count = _result.Matches.Count;
        _current = ((_current < 0 ? _result.InitialIndex() : _current) + direction + count) % count;
        _pending = Reveal.Current;
        Run();
    }

    /// <summary>Re-reads matches and geometry without moving the viewport.</summary>
    public void Refresh()
    {
        if (_needle.Length > 0)
        {
            Run();
        }
    }

    /// <summary>The window lost the foreground: highlights would float over whatever covers it.</summary>
    public void Suspend()
    {
        _suspended = true;
        _overlay?.HideOverlay();
    }

    public void Resume()
    {
        _suspended = false;
        Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        Tab.Surface.ViewportChanged -= OnViewportChanged;
        _search.Forget(Tab.TerminalHwnd);
        _overlay?.HideOverlay();
        _overlay?.Close();
        _overlay = null;
    }

    private void OnViewportChanged(object? sender, EventArgs e) => Refresh();

    private async void Run()
    {
        if (_busy)
        {
            _again = true;
            return;
        }

        _busy = true;
        try
        {
            do
            {
                _again = false;
                await SearchOnceAsync();
            }
            while (_again && !_disposed);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SearchOnceAsync()
    {
        var needle = _needle;
        var matchCase = _matchCase;
        var keep = _result is not null && _current >= 0 && _current < _result.Matches.Count ? _result.Matches[_current] : null;
        var reveal = _pending;
        _pending = Reveal.None;

        var hwnd = Tab.TerminalHwnd;
        if (hwnd == 0 || needle.Length == 0)
        {
            return;
        }

        var result = await _search.FindAsync(hwnd, needle, matchCase, keep);
        if (_disposed || result is null || needle != _needle || matchCase != _matchCase)
        {
            // Stale: a newer query is on its way, or the provider refused.
            if (reveal != Reveal.None)
            {
                _pending = reveal;
            }

            return;
        }

        var current = reveal == Reveal.Initial
            ? result.InitialIndex()
            : result.KeptIndex >= 0
                ? result.KeptIndex
                : result.Matches.Count == 0 ? -1 : Math.Clamp(_current, 0, result.Matches.Count - 1);

        if (reveal != Reveal.None && current >= 0)
        {
            var match = result.Matches[current];
            var wasVisible = match.Visible;
            await _search.RevealAsync(match);

            if (!wasVisible && !_disposed)
            {
                // The reveal scrolled; geometry has to be read again for the new viewport.
                var fresh = await _search.FindAsync(hwnd, needle, matchCase, match);
                if (fresh is not null && needle == _needle && matchCase == _matchCase)
                {
                    result = fresh;
                    current = fresh.KeptIndex >= 0 ? fresh.KeptIndex : Math.Clamp(current, 0, Math.Max(fresh.Matches.Count - 1, 0));
                }
            }
        }

        if (_disposed)
        {
            return;
        }

        _result = result;
        _current = result.Matches.Count == 0 ? -1 : current;
        Render();
        Changed?.Invoke(this);
    }

    private void Render()
    {
        if (_result is null || _result.Matches.Count == 0 || _suspended)
        {
            _overlay?.HideOverlay();
            return;
        }

        var fills = new List<Rect>();
        var outlines = new List<Rect>();
        for (var i = 0; i < _result.Matches.Count; i++)
        {
            var match = _result.Matches[i];
            if (!match.Visible)
            {
                continue;
            }

            if (i == _current)
            {
                outlines.AddRange(match.Rects);
            }
            else
            {
                fills.AddRange(match.Rects);
            }
        }

        // Accent-coloured, so the highlights follow the theme and any skin: a translucent
        // tint for the other matches, an outline around the one the selection marks.
        var accent = ((SolidColorBrush)_owner.FindResource("Accent.Base")).Color;
        var fill = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        var stroke = new SolidColorBrush(accent);
        fill.Freeze();
        stroke.Freeze();

        (_overlay ??= new OverlayHost(_owner)).ShowCells(fills, outlines, fill, stroke);
    }
}
