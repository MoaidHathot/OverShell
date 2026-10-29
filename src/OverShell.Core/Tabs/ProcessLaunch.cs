using System.Text;

namespace OverShell.Core.Tabs;

/// <summary>A real root process, as distinct from a command typed into a continuing shell.</summary>
public sealed record ProcessLaunch(string Executable, IReadOnlyList<string>? Arguments = null)
{
    public string? Validate() => string.IsNullOrWhiteSpace(Executable) || Executable.Contains('\0') || Executable.Contains('"')
        ? "A process needs an executable path or name, without quotes or NUL characters"
        : Arguments?.Any(a => a is null || a.Contains('\0')) == true ? "Process arguments must be strings without NUL characters" : null;

    public string CommandLine => string.Join(' ', new[] { QuoteArgument(Executable) }.Concat((Arguments ?? []).Select(QuoteArgument)));

    /// <summary>Windows argv quoting: double backslashes only before a quote or the closing quote.</summary>
    public static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
