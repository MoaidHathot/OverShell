using System.Text.RegularExpressions;
using System.Windows;

namespace OverShell.App.Terminal.Hyperlinks;

/// <summary>Pure text logic: is there a URL under this offset, and which span of the line carries it?</summary>
internal static partial class HyperlinkDetector
{
    // Windows Terminal's default URL pattern, loosened to accept the characters modern
    // URLs actually contain; trailing punctuation is trimmed afterwards.
    [GeneratedRegex(@"\b(?:https?|ftp|file)://[^\s<>""'`\x00-\x1f\u2018\u2019\u201c\u201d]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex { get; }

    private const string TrailingPunctuation = ".,;:!?'\")]}>";

    /// <summary>The URL printed as text covering <paramref name="offset"/>, if any.</summary>
    public static LinkMatch? MatchUrl(string line, int offset)
    {
        if (offset < 0 || offset >= line.Length)
        {
            return null;
        }

        foreach (Match m in UrlRegex.Matches(line))
        {
            if (offset < m.Index)
            {
                break;
            }

            var url = TrimTrailing(m.Value);
            if (offset < m.Index + url.Length)
            {
                return new LinkMatch(url, m.Index, url.Length);
            }
        }

        return null;
    }

    /// <summary>
    /// A match that reaches an end of the line where the walk was cut short may be a
    /// fragment of something longer; better no link than a wrong one.
    /// </summary>
    public static bool TouchesTruncatedEdge(TextHit hit, LinkMatch match) =>
        (hit.TruncatedAbove && match.Start == 0) ||
        (hit.TruncatedBelow && match.Start + match.Length >= hit.Line.Length);

    /// <summary>
    /// Fallback geometry when the terminal cannot be asked (see <c>TerminalTextProbe</c>):
    /// one rectangle per row the span crosses, assuming one cell per character. Exact for
    /// rows without wide or combining glyphs, which is nearly all of them.
    /// </summary>
    public static IReadOnlyList<Rect> ApproximateCellRects(TextHit hit, LinkMatch match)
    {
        if (hit.HitRowBounds.IsEmpty || hit.Rows.Count == 0 || match.Length <= 0)
        {
            return [];
        }

        var hitRowText = hit.Rows[hit.HitRow];
        if (hitRowText.Length == 0)
        {
            return [];
        }

        var cellWidth = hit.HitRowBounds.Width / hitRowText.Length;
        var cellHeight = hit.HitRowBounds.Height;
        var rects = new List<Rect>();

        var rowStart = 0;
        for (var row = 0; row < hit.Rows.Count; row++)
        {
            var rowLength = hit.Rows[row].Length;
            var from = Math.Max(match.Start, rowStart);
            var to = Math.Min(match.Start + match.Length, rowStart + rowLength);

            if (from < to)
            {
                var rect = new Rect(
                    hit.HitRowBounds.X + ((from - rowStart) * cellWidth),
                    hit.HitRowBounds.Y + ((row - hit.HitRow) * cellHeight),
                    (to - from) * cellWidth,
                    cellHeight);

                rect.Intersect(hit.TerminalBounds);
                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                {
                    rects.Add(rect);
                }
            }

            rowStart += rowLength;
        }

        return rects;
    }

    private static string TrimTrailing(string url)
    {
        var end = url.Length;
        while (end > 0 && TrailingPunctuation.Contains(url[end - 1]))
        {
            // Keep a closing bracket that balances one inside the URL: Wikipedia-style links.
            if (url[end - 1] == ')' && Count(url, '(', end) > Count(url, ')', end - 1))
            {
                break;
            }

            end--;
        }

        return url[..end];
    }

    private static int Count(string s, char c, int length)
    {
        var n = 0;
        for (var i = 0; i < length; i++)
        {
            if (s[i] == c)
            {
                n++;
            }
        }

        return n;
    }
}
