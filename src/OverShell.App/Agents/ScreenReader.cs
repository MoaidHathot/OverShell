using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace OverShell.App.Agents;

/// <summary>
/// Reads what a terminal currently shows — the viewport, not the scrollback — through the
/// same UI Automation text provider the hyperlink probe uses. Works for hidden tabs
/// (DESIGN.md §12.7, spike 1) and always on a worker thread: a UIA client call on the
/// provider's own UI thread deadlocks.
/// </summary>
internal sealed class ScreenReader
{
    private readonly ConcurrentDictionary<nint, AutomationElement> _elements = new();

    /// <summary>The viewport's rows, top to bottom, trailing blanks trimmed; null when the window is gone or refuses.</summary>
    public Task<IReadOnlyList<string>?> ReadRowsAsync(nint hwnd) => Task.Run(() => ReadRows(hwnd));

    public void Forget(nint hwnd) => _elements.TryRemove(hwnd, out _);

    private IReadOnlyList<string>? ReadRows(nint hwnd)
    {
        if (hwnd == 0)
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

            var ranges = text.GetVisibleRanges();
            if (ranges.Length == 0)
            {
                return [];
            }

            // The provider gives one range for the viewport; rows end in CR LF.
            var raw = ranges[0].GetText(-1);
            var rows = raw.Split("\r\n", StringSplitOptions.None);
            var list = new List<string>(rows.Length);
            foreach (var row in rows)
            {
                list.Add(row.TrimEnd());
            }

            // Trailing empty rows below the last written line carry no information.
            var end = list.Count;
            while (end > 0 && list[end - 1].Length == 0)
            {
                end--;
            }

            return list.GetRange(0, end);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            _elements.TryRemove(hwnd, out _);
            return null;
        }
    }
}
