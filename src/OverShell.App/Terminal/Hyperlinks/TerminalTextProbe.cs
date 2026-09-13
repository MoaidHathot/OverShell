using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Media;

namespace OverShell.App.Terminal.Hyperlinks;

/// <summary>
/// Reads the text under a screen point out of a native terminal HWND through UI
/// Automation — the one channel Microsoft's control exposes its buffer through
/// (<c>HwndTerminal</c> answers <c>WM_GETOBJECT</c> with a full <c>ITextProvider</c>) —
/// and, when a link is found there, asks the terminal where on screen it is.
/// <para>
/// Always runs on a worker thread. A UIA client call made on the provider's own UI
/// thread is the textbook deadlock, and every call here is a few milliseconds anyway.
/// </para>
/// <para>
/// What the provider guarantees (DESIGN.md §7.10): <c>TextUnit.Line</c> is one buffer
/// row; a row's text is exact, one entry per glyph rather than per cell; a CR LF follows
/// a row's text only when that row did <em>not</em> wrap into the next; and
/// <c>FindText</c> searches wrapped rows as continuous text, returning a range whose
/// bounding rectangles the terminal computes from its own cell layout. So the cell ↔ text
/// mapping is never reconstructed here — the terminal is asked.
/// </para>
/// </summary>
internal sealed class TerminalTextProbe
{
    /// <summary>Rows walked in each direction before giving up on a logical line. A URL longer than this is not a URL.</summary>
    private const int MaxRowsEachWay = 8;

    /// <summary>Occurrences of the same text examined within one logical line before giving up on placement.</summary>
    private const int MaxOccurrences = 8;

    private readonly ConcurrentDictionary<nint, AutomationElement> _elements = new();

    /// <param name="hwnd">The terminal's own HWND (class <c>HwndTerminalClass</c>).</param>
    /// <param name="screenPoint">Physical screen pixels, as carried by the window message.</param>
    /// <param name="columns">The grid width the surface reports; used only to size a cell. Text length stands in when unknown.</param>
    /// <param name="resolve">Pure text logic mapping (logical line, offset) to a link. Runs on the worker thread.</param>
    public Task<ProbeResult?> ProbeAsync(nint hwnd, Point screenPoint, int columns, Func<string, int, LinkMatch?> resolve) =>
        Task.Run(() => Probe(hwnd, screenPoint, columns, resolve));

    public void Forget(nint hwnd) => _elements.TryRemove(hwnd, out _);

    private ProbeResult? Probe(nint hwnd, Point screenPoint, int columns, Func<string, int, LinkMatch?> resolve)
    {
        try
        {
            var element = _elements.GetOrAdd(hwnd, static h => AutomationElement.FromHandle(h));

            if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern text)
            {
                return null;
            }

            var terminalBounds = element.Current.BoundingRectangle;

            var point = text.RangeFromPoint(screenPoint);
            var line = point.Clone();
            line.ExpandToEnclosingUnit(TextUnit.Line);

            var rects = line.GetBoundingRectangles();
            var rowBounds = rects.Length > 0 ? rects[0] : Rect.Empty;
            if (rowBounds.IsEmpty)
            {
                return null;
            }

            var (rowText, rowEndsLine) = ReadRow(line);

            // One row rectangle fixes the whole lattice: rows are cellHeight tall and the
            // row spans every column.
            var cellColumns = columns > 0 ? columns : rowText.Length;
            (Point Origin, Size Cell)? grid = cellColumns > 0 && rowBounds.Width > 0 && rowBounds.Height > 0
                ? (terminalBounds.TopLeft, new Size(rowBounds.Width / cellColumns, rowBounds.Height))
                : null;

            // A point below the last written row is snapped to that row by the provider;
            // the row rectangle no longer contains the pointer, which is how we tell.
            if (screenPoint.Y < rowBounds.Top || screenPoint.Y >= rowBounds.Bottom)
            {
                return new ProbeResult(null, null, grid);
            }

            // Text before the pointer within its row: exact, wide glyphs included.
            var prefix = line.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, point, TextPatternRangeEndpoint.Start);
            var column = prefix.GetText(-1).Length;

            // Walk to the logical line's ends, one buffer row at a time. A row whose text
            // carries no CR LF wrapped into the next one.
            var rows = new List<string> { rowText };
            var top = line;
            var above = 0;
            var truncatedAbove = false;
            while (true)
            {
                if (above == MaxRowsEachWay)
                {
                    truncatedAbove = true;
                    break;
                }

                var previous = top.Clone();
                if (previous.Move(TextUnit.Line, -1) == 0)
                {
                    break;
                }

                var (previousText, previousEndsLine) = ReadRow(previous);
                if (previousEndsLine)
                {
                    break;
                }

                rows.Insert(0, previousText);
                top = previous;
                above++;
            }

            var bottom = line;
            var below = 0;
            var truncatedBelow = false;
            var endsLine = rowEndsLine;
            while (!endsLine)
            {
                if (below == MaxRowsEachWay)
                {
                    truncatedBelow = true;
                    break;
                }

                var next = bottom.Clone();
                if (next.Move(TextUnit.Line, 1) == 0)
                {
                    break;
                }

                var (nextText, nextEndsLine) = ReadRow(next);
                rows.Add(nextText);
                bottom = next;
                below++;
                endsLine = nextEndsLine;
            }

            var hitRow = above;

            var offset = column;
            for (var i = 0; i < hitRow; i++)
            {
                offset += rows[i].Length;
            }

            var logicalLine = string.Concat(rows);
            offset = Math.Clamp(offset, 0, Math.Max(logicalLine.Length - 1, 0));

            var hit = new TextHit(logicalLine, offset, rows, hitRow, rowBounds, terminalBounds, truncatedAbove, truncatedBelow);

            var match = resolve(logicalLine, offset);
            if (match is null || HyperlinkDetector.TouchesTruncatedEdge(hit, match))
            {
                return new ProbeResult(hit, null, grid);
            }

            var link = Locate(text, point, top, bottom, hit, match);
            return new ProbeResult(hit, link, grid);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            // The window went away or the provider refused; forget the cached element so the
            // next attempt starts clean.
            _elements.TryRemove(hwnd, out _);
            return null;
        }
    }

    /// <summary>
    /// Asks the terminal where the matched text sits: a search restricted to the logical
    /// line, then the bounding rectangles of what it found. When the same text occurs
    /// more than once on the line, the occurrence containing the pointer wins.
    /// </summary>
    private static LinkHit Locate(TextPattern text, TextPatternRange point, TextPatternRange top, TextPatternRange bottom, TextHit hit, LinkMatch match)
    {
        string? fontFamily = null;
        try
        {
            fontFamily = text.DocumentRange.GetAttributeValue(TextPattern.FontNameAttribute) as string;
        }
        catch (Exception e) when (e is InvalidOperationException or COMException)
        {
            // Purely advisory.
        }

        var needle = hit.Line.Substring(match.Start, match.Length);

        var search = top.Clone();
        search.MoveEndpointByRange(TextPatternRangeEndpoint.End, bottom, TextPatternRangeEndpoint.End);

        TextPatternRange? found = null;
        for (var i = 0; i < MaxOccurrences; i++)
        {
            var candidate = search.FindText(needle, false, false);
            if (candidate is null)
            {
                break;
            }

            TrimToNeedle(candidate, needle.Length);

            var startsBeforePointer = candidate.CompareEndpoints(TextPatternRangeEndpoint.Start, point, TextPatternRangeEndpoint.Start) <= 0;
            var endsAfterPointer = candidate.CompareEndpoints(TextPatternRangeEndpoint.End, point, TextPatternRangeEndpoint.Start) > 0;
            if (startsBeforePointer && endsAfterPointer)
            {
                found = candidate;
                break;
            }

            search.MoveEndpointByRange(TextPatternRangeEndpoint.Start, candidate, TextPatternRangeEndpoint.End);
        }

        if (found is null)
        {
            return new LinkHit(hit, match, HyperlinkDetector.ApproximateCellRects(hit, match), null, fontFamily);
        }

        var rects = found.GetBoundingRectangles()
            .Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0)
            .ToArray();

        return new LinkHit(hit, match, rects, ReadForeground(found), fontFamily);
    }

    /// <summary>
    /// The provider builds a found range from a half-open search hit and then increments
    /// the end once more as if it were inclusive (<c>UiaTextRangeBase::FindText</c>), so
    /// the range normally covers one glyph too many. Measured rather than assumed: the end
    /// is pulled back a glyph at a time while the range's text — minus the CR LF a row end
    /// carries — is longer than what was searched for. At a row end the extra "glyph" is
    /// that CR LF, and pulling back would drop the last real one.
    /// </summary>
    private static void TrimToNeedle(TextPatternRange range, int needleLength)
    {
        for (var i = 0; i < 2; i++)
        {
            var text = range.GetText(-1);
            if (text.EndsWith("\r\n", StringComparison.Ordinal))
            {
                text = text[..^2];
            }

            if (text.Length <= needleLength ||
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1) == 0)
            {
                break;
            }
        }
    }

    /// <summary>The rendered foreground of the range as a COLORREF, or null when the range is not uniformly coloured.</summary>
    private static Color? ReadForeground(TextPatternRange range)
    {
        try
        {
            var value = range.GetAttributeValue(TextPattern.ForegroundColorAttribute);
            if (value is int colorRef)
            {
                return Color.FromRgb((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or COMException)
        {
            // Mixed, unsupported, or gone; the caller has a default.
        }

        return null;
    }

    /// <summary>A row's text and whether the buffer marks it as the end of a logical line.</summary>
    private static (string Text, bool EndsLine) ReadRow(TextPatternRange row)
    {
        var text = row.GetText(-1);
        return text.EndsWith("\r\n", StringComparison.Ordinal)
            ? (text[..^2], true)
            : (text, false);
    }
}
