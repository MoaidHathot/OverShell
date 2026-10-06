using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverShell.Core.Herd;

/// <summary>One thing that happened to one tab: when, what kind, which tab, and the particulars.</summary>
public sealed record HerdLogEntry(DateTimeOffset At, string Kind, string Tab, string? Label, string? Harness, string? From, string? To, string? Detail)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["at"] = At.ToString("o"), ["kind"] = Kind, ["tab"] = Tab };
        if (Label is not null) o["label"] = Label;
        if (Harness is not null) o["harness"] = Harness;
        if (From is not null) o["from"] = From;
        if (To is not null) o["to"] = To;
        if (Detail is not null) o["detail"] = Detail;
        return o;
    }

    public static HerdLogEntry? FromJson(JsonObject? o)
    {
        if (o is null)
        {
            return null;
        }

        var at = Str(o, "at");
        var kind = Str(o, "kind");
        var tab = Str(o, "tab");
        if (at is null || kind is null || tab is null || !DateTimeOffset.TryParse(at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
        {
            return null;
        }

        return new HerdLogEntry(when, kind, tab, Str(o, "label"), Str(o, "harness"), Str(o, "from"), Str(o, "to"), Str(o, "detail"));
    }

    /// <summary>The entry as one line for a picker or a report: time, label, what.</summary>
    public string Describe()
    {
        var who = Label ?? Tab;
        var what = Kind switch
        {
            "state" => $"{From} -> {To}",
            "attention" when To == "Done" => "finished while you were away",
            "attention" => $"needs you: {To}",
            "opened" => "opened",
            "closed" => "closed",
            "run" => "OverShell started",
            _ => Kind,
        };
        return $"{At.ToLocalTime():HH:mm:ss}  {who}  {what}";
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : null;
}

/// <summary>
/// The herd log (DESIGN.md §12.19): what happened to every tab while the window ran, one
/// JSON line per event, one file per run under <c>state\logs\</c>. Written as it happens
/// and flushed per line, so a crash loses nothing; read back by the <c>herd.log</c> picker
/// and by anyone with a text editor. Low volume - a state change is a few a minute at most -
/// so the simplest thing works: a lock and a flush.
/// </summary>
public sealed class HerdLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public HerdLog(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        Path = path;
    }

    public string Path { get; }

    /// <summary>The file a run started at <paramref name="startedAt"/> writes: sortable, one per second at most.</summary>
    public static string FileName(DateTimeOffset startedAt) => $"{startedAt.ToLocalTime():yyyyMMdd-HHmmss}.jsonl";

    public void Append(HerdLogEntry entry)
    {
        lock (_gate)
        {
            try
            {
                _writer.WriteLine(entry.ToJson().ToJsonString());
                _writer.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // A full disk or a closed log is not a reason to stop the shell.
            }
        }
    }

    /// <summary>Every readable entry in <paramref name="path"/>, oldest first; a bad line is skipped, not fatal. Reads the live file too (the writer keeps it open).</summary>
    public static IReadOnlyList<HerdLogEntry> Read(string path)
    {
        var entries = new List<HerdLogEntry>();
        if (!File.Exists(path))
        {
            return entries;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                if (HerdLogEntry.FromJson(JsonNode.Parse(line) as JsonObject) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A line cut short by a crash; the rest is still good.
            }
        }

        return entries;
    }

    /// <summary>The run files in <paramref name="directory"/>, newest first by name (the stamp sorts).</summary>
    public static IReadOnlyList<string> Files(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.jsonl").OrderByDescending(System.IO.Path.GetFileName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Keeps the newest <paramref name="keep"/> run files, deletes the rest.</summary>
    public static int Prune(string directory, int keep)
    {
        var removed = 0;
        foreach (var file in Files(directory).Skip(Math.Max(0, keep)))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Someone has it open; next time.
            }
        }

        return removed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
