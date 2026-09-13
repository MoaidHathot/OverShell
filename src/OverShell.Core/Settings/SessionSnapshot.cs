using System.Text.Json.Nodes;

namespace OverShell.Core.Settings;

/// <summary>One tab as it is remembered between runs.</summary>
public sealed class SavedTab
{
    public string? ProfileId { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? Label { get; init; }

    public string? Group { get; init; }

    /// <summary>Harness rule-set id when an agent was running here; the restore may offer to resume it.</summary>
    public string? Harness { get; init; }

    /// <summary>True when the agent was still running at save time — the only case worth resuming unasked.</summary>
    public bool AgentRunning { get; init; }

    public string? SessionId { get; init; }

    /// <summary>The command that resumes the agent's session, with the id already filled in.</summary>
    public string? ResumeCommand { get; init; }
}

/// <summary>
/// What a window looked like when it closed: <c>%LOCALAPPDATA%\OverShell\session.json</c>.
/// Written on close and every half minute while running, read once at the next start.
/// Tolerant on the way in — a corrupt or foreign file yields an empty session, never an
/// exception at startup.
/// </summary>
public sealed class SessionSnapshot
{
    public int Version { get; init; } = 1;

    public DateTimeOffset SavedAt { get; init; }

    public string View { get; init; } = "terminal";

    public int ActiveIndex { get; init; }

    public List<SavedTab> Tabs { get; init; } = [];

    /// <summary>Per-view layout overrides chosen through <c>layout.*</c> during the session.</summary>
    public Dictionary<string, string> LayoutOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static SessionSnapshot? Load(string path, out string? error)
    {
        error = null;
        var node = Jsonc.ReadFile(path, out var readError);
        if (readError is not null)
        {
            error = readError;
            return null;
        }

        if (node is not JsonObject)
        {
            return null;
        }

        var snapshot = Jsonc.To<SessionSnapshot>(node, out var shapeError);
        if (snapshot is null)
        {
            error = shapeError ?? "not a session file";
        }

        return snapshot;
    }

    /// <summary>Writes atomically: a temp file renamed over the old one, so a crash mid-write cannot leave half a session.</summary>
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
}
