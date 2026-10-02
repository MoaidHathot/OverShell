using System.Text.Json.Nodes;

namespace OverShell.Core.Settings;

/// <summary>The last rows a tab showed, for the ghost a restore after a crash paints (§12.14).</summary>
public sealed class SavedScreen
{
    public List<string> Rows { get; init; } = [];

    public DateTimeOffset At { get; init; }
}

/// <summary>
/// <c>session-screens.json</c> in the state root: the last few viewport rows of every open
/// tab, keyed by tab id, written every few seconds while running. Kept apart from
/// <c>session.json</c> so the two-second compare there stays small and a screen that keeps
/// changing (a build log) does not make the session file churn. Scrollback itself cannot
/// come back (ConPTY repaints the viewport only, §7.1); what was on screen when the lights
/// went out can.
/// </summary>
public sealed class SessionScreens
{
    /// <summary>How many rows from the bottom are kept per tab.</summary>
    public const int RowsKept = 30;

    public int Version { get; init; } = 1;

    public Dictionary<string, SavedScreen> Tabs { get; init; } = new(StringComparer.Ordinal);

    public static SessionScreens? Load(string path, out string? error)
    {
        error = null;
        var node = Jsonc.ReadFile(path, out var readError);
        if (readError is not null)
        {
            error = readError;
            return null;
        }

        return node is JsonObject ? Jsonc.To<SessionScreens>(node, out error) : null;
    }

    public bool Save(string path, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Jsonc.Serialize(this) + "\n", new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>The bottom <see cref="RowsKept"/> rows with trailing blank rows dropped - what is worth showing again.</summary>
    public static List<string> Trim(IReadOnlyList<string> rows)
    {
        var end = rows.Count;
        while (end > 0 && rows[end - 1].Trim().Length == 0)
        {
            end--;
        }

        var start = Math.Max(0, end - RowsKept);
        return rows.Skip(start).Take(end - start).Select(r => r.TrimEnd()).ToList();
    }

    /// <summary>
    /// The VT text a restored tab shows before its shell prints anything: the previous rows
    /// dimmed, a rule naming when they were saved, then a blank line for the prompt to land on.
    /// Control characters in the rows are dropped - they came from screen text and should
    /// be text; a stray escape would otherwise be interpreted.
    /// </summary>
    public static string Preamble(SavedScreen screen, string caption)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("\u001b[2m");
        foreach (var row in screen.Rows)
        {
            foreach (var ch in row)
            {
                if (!char.IsControl(ch))
                {
                    sb.Append(ch);
                }
            }

            sb.Append("\r\n");
        }

        sb.Append("\u001b[0m\u001b[2;3m\u2014 ").Append(caption).Append(" \u2014\u001b[0m\r\n\r\n");
        return sb.ToString();
    }
}
