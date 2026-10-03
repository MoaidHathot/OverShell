using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using OverShell.App.Terminal.Hyperlinks;

namespace OverShell.App.Terminal.Search;

/// <summary>
/// One occurrence of what was searched for. <see cref="Rects"/> are physical screen pixels,
/// one per run of cells on a row, and empty when the occurrence is scrolled out of view -
/// the provider reports geometry for the viewport only, which is how a visible match is told
/// from a hidden one.
/// </summary>
internal sealed class FindMatch
{
    internal FindMatch(TextPatternRange range, IReadOnlyList<Rect> rects)
    {
        Range = range;
        Rects = rects;
    }

    /// <summary>The provider's range, trimmed to the needle. Worker thread only.</summary>
    internal TextPatternRange Range { get; }

    public IReadOnlyList<Rect> Rects { get; }

    public bool Visible => Rects.Count > 0;
}

/// <summary>
/// Every occurrence in the buffer in document order (scrollback first), plus where the
/// viewport sits among them: <see cref="AboveViewport"/> matches lie wholly above the
/// visible rows. <see cref="KeptIndex"/> is the position of the match the caller asked to
/// keep, -1 when it is gone.
/// </summary>
internal sealed record FindResult(string Needle, bool MatchCase, IReadOnlyList<FindMatch> Matches, bool Truncated, int AboveViewport, int KeptIndex)
{
    public int VisibleCount => Matches.Count(m => m.Visible);

    /// <summary>
    /// Where a fresh search starts: the lowest match on screen, else the nearest one above
    /// the view (the text the user just saw scroll past), else the first one below.
    /// </summary>
    public int InitialIndex()
    {
        if (Matches.Count == 0)
        {
            return -1;
        }

        var lastVisible = -1;
        for (var i = 0; i < Matches.Count; i++)
        {
            if (Matches[i].Visible)
            {
                lastVisible = i;
            }
        }

        if (lastVisible >= 0)
        {
            return lastVisible;
        }

        return AboveViewport > 0 ? AboveViewport - 1 : 0;
    }
}

/// <summary>
/// Find-in-buffer over the terminal's UI Automation text provider (DESIGN.md §12.14, spike 7).
/// <c>FindText</c> on the document range reaches the whole buffer, scrollback included;
/// <c>Select()</c> on a found range does what Windows Terminal's own search box does
/// (<c>Terminal::SelectNewRegion</c>): scrolls the match into view when it is hidden and
/// selects it, which the renderer paints. Always on a worker thread - a UIA client call on
/// the provider's own UI thread is the textbook deadlock.
/// </summary>
internal sealed class TerminalSearch
{
    /// <summary>Beyond this the count reads "500+"; each further occurrence costs a provider round trip.</summary>
    public const int MaxMatches = 500;

    private readonly ConcurrentDictionary<nint, AutomationElement> _elements = new();

    /// <param name="hwnd">The terminal's own HWND.</param>
    /// <param name="keep">A match from an earlier result whose position should be found again, if it still exists.</param>
    public Task<FindResult?> FindAsync(nint hwnd, string needle, bool matchCase, FindMatch? keep) =>
        Task.Run(() => Find(hwnd, needle, matchCase, keep));

    /// <summary>Scrolls the match into view if needed and selects it. False when the provider refused.</summary>
    public Task<bool> RevealAsync(FindMatch match) => Task.Run(() => Reveal(match));

    public void Forget(nint hwnd) => _elements.TryRemove(hwnd, out _);

    private FindResult? Find(nint hwnd, string needle, bool matchCase, FindMatch? keep)
    {
        if (needle.Length == 0)
        {
            return null;
        }

        try
        {
            var element = _elements.GetOrAdd(hwnd, static h => AutomationElement.FromHandle(h));
            if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern text)
            {
                return null;
            }

            var visible = text.GetVisibleRanges();
            var viewport = visible.Length > 0 ? visible[0] : null;

            var search = text.DocumentRange;
            var matches = new List<FindMatch>();
            var truncated = false;
            var above = 0;
            var keptIndex = -1;

            while (true)
            {
                if (matches.Count == MaxMatches)
                {
                    truncated = true;
                    break;
                }

                var candidate = TerminalTextProbe.TryFindText(search, needle, ignoreCase: !matchCase);
                if (candidate is null)
                {
                    break;
                }

                var found = TerminalTextProbe.TrimToNeedle(candidate, needle.Length);

                // The provider honours the case flag; comparing against the text it handed
                // back costs nothing extra and keeps a match-case search exact regardless.
                var accepted = !matchCase || found.StartsWith(needle, StringComparison.Ordinal);
                if (accepted)
                {
                    var rects = candidate.GetBoundingRectangles()
                        .Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0)
                        .ToArray();

                    if (viewport is not null && candidate.CompareEndpoints(TextPatternRangeEndpoint.End, viewport, TextPatternRangeEndpoint.Start) <= 0)
                    {
                        above++;
                    }

                    if (keep is not null && keptIndex < 0 &&
                        candidate.CompareEndpoints(TextPatternRangeEndpoint.Start, keep.Range, TextPatternRangeEndpoint.Start) == 0)
                    {
                        keptIndex = matches.Count;
                    }

                    matches.Add(new FindMatch(candidate, rects));
                }

                // Continue after this occurrence. A provider that hands back the same spot
                // again (a degenerate hit at the very end) would otherwise loop forever.
                var before = search.Clone();
                search.MoveEndpointByRange(TextPatternRangeEndpoint.Start, candidate, TextPatternRangeEndpoint.End);
                if (search.CompareEndpoints(TextPatternRangeEndpoint.Start, before, TextPatternRangeEndpoint.Start) <= 0)
                {
                    break;
                }
            }

            return new FindResult(needle, matchCase, matches, truncated, above, keptIndex);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            Diagnostics.TraceLog.Agents.Write($"find: provider refused '{needle}': {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            _elements.TryRemove(hwnd, out _);
            return null;
        }
    }

    private static bool Reveal(FindMatch match)
    {
        try
        {
            match.Range.Select();
            return true;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return false;
        }
    }
}
