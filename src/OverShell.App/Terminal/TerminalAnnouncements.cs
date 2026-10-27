using System.Text.RegularExpressions;

namespace OverShell.App.Terminal;

/// <summary>Incremental OSC title/directory announcements. A carried sequence is never replayed.</summary>
internal sealed partial class TerminalAnnouncements
{
    private string _carry = string.Empty;
    [GeneratedRegex("\u001b\\][02];([^\u0007\u001b]*)(?:\u0007|\u001b\\\\)")]
    private static partial Regex TitleRegex { get; }
    [GeneratedRegex("\u001b\\]9;9;\"?([^\"\u0007\u001b]*)\"?(?:\u0007|\u001b\\\\)")]
    private static partial Regex CwdRegex { get; }
    [GeneratedRegex("\u001b\\]7;file://[^/]*(/[^\u0007\u001b]*)(?:\u0007|\u001b\\\\)")]
    private static partial Regex Cwd7Regex { get; }

    public (string? Title, string? Directory) Observe(string chunk)
    {
        var text = _carry + chunk;
        var newFrom = _carry.Length;
        static Match? Last(Regex regex, string text, int newFrom) => regex.Matches(text).Cast<Match>().LastOrDefault(m => m.Index + m.Length > newFrom);
        var title = Last(TitleRegex, text, newFrom);
        var cwd = Last(CwdRegex, text, newFrom);
        var cwd7 = Last(Cwd7Regex, text, newFrom);
        var directory = cwd7 is not null && (cwd is null || cwd7.Index > cwd.Index)
            ? Uri.UnescapeDataString(cwd7.Groups[1].Value).TrimStart('/').Replace('/', '\\')
            : cwd?.Groups[1].Value;
        _carry = text.Length <= 8192 ? text : text[^8192..];
        return (title?.Groups[1].Value, directory);
    }
}
