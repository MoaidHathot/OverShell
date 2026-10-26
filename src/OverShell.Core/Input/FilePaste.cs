using OverShell.Core.Integrations;

namespace OverShell.Core.Input;

public enum FilePasteStyle { Windows, PowerShell, Posix }

/// <summary>File references are ordinary input, independent of file type or agent.</summary>
public static class FilePaste
{
    public static FilePasteStyle StyleFor(string? commandLine, bool agent, IEnumerable<string>? processes = null)
    {
        if (agent) return FilePasteStyle.Windows;
        // The process probe reports descendants nearest first: the deepest shell wins.
        foreach (var image in (processes ?? []).Reverse())
        {
            if (ShellStyle(ShellLaunch.ProgramName(image)) is { } style) return style;
        }

        return ShellStyle(ShellLaunch.ProgramFromCommandLine(commandLine ?? string.Empty)) ?? FilePasteStyle.Windows;
    }

    private static FilePasteStyle? ShellStyle(string name) => name switch
    {
        "pwsh" or "powershell" => FilePasteStyle.PowerShell,
        "bash" or "sh" or "zsh" or "fish" or "wsl" => FilePasteStyle.Posix,
        "cmd" => FilePasteStyle.Windows,
        _ => null,
    };

    public static string Format(IEnumerable<string> paths, FilePasteStyle style) =>
        string.Join(' ', paths.Select(path => Quote(path, style)));

    public static string Quote(string path, FilePasteStyle style)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(c => c < ' ' || c == '"'))
        {
            throw new ArgumentException("A file path contains characters that cannot be pasted into a terminal.", nameof(path));
        }

        return style switch
        {
            FilePasteStyle.PowerShell => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'",
            FilePasteStyle.Posix => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
            // Double trailing backslashes so a native Windows argv parser keeps the closing quote.
            _ => "\"" + path + new string('\\', path.Length - path.TrimEnd('\\').Length) + "\"",
        };
    }
}
