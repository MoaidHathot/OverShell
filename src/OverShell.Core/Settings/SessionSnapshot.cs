using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OverShell.Core.Settings;

/// <summary>Where a top-level window sat, in WPF device-independent units.</summary>
public sealed class SavedWindow
{
    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    /// <summary>True when the window was maximized; the other four are then its restore bounds.</summary>
    public bool Maximized { get; init; }
}

/// <summary>One tab as it is remembered between runs.</summary>
public sealed class SavedTab
{
    /// <summary>The tab's run-time id, the key into <c>session-screens.json</c>; a restored tab gets a new one.</summary>
    public string? Id { get; init; }

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

    /// <summary>True when the tab lived in a tear-off window of its own (§12.12); <see cref="Window"/> says where.</summary>
    public bool Detached { get; init; }

    /// <summary>The tear-off window's placement, when <see cref="Detached"/>.</summary>
    public SavedWindow? Window { get; init; }

    /// <summary>When the user closed this tab — only on entries under <see cref="SessionSnapshot.RecentlyClosed"/>.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Extension state the tab carried (<c>ITab.Properties</c>, §12.16): a mute, a watch pattern. Null when empty.</summary>
    public Dictionary<string, string>? Extra { get; init; }
}

/// <summary>How the run that wrote the file ended. Absent while running — and so, when read at the next start, absent means it never ended properly.</summary>
public enum SessionCloseReason
{
    /// <summary>The user closed the window.</summary>
    Closed,

    /// <summary>Windows signed the user out or restarted while the window was open.</summary>
    SessionEnding,
}

/// <summary>
/// What the window looks like, as last written: <c>%LOCALAPPDATA%\OverShell\session.json</c>.
/// Written on close and every couple of seconds while running (only when something
/// changed), read once at the next start. Tolerant on the way in — a corrupt or foreign
/// file yields an empty session, never an exception at startup.
/// </summary>
public sealed class SessionSnapshot
{
    /// <summary>Format version: 1 had no close reason, placement or history; 2 (P4) has all three.</summary>
    public const int CurrentVersion = 2;

    /// <summary>How many closed tabs are remembered for <c>tab.reopenClosed</c>.</summary>
    public const int RecentlyClosedLimit = 20;

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset SavedAt { get; init; }

    /// <summary>When the run that wrote the file started - with <see cref="SavedAt"/>, how long it lived (the crash-loop guard, §12.14).</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Null while the window is running; set by the save the window makes as it closes.
    /// A version-2 file without it was left behind by a run that was killed — a crash, the
    /// task manager, a power cut — and the next start says so (<see cref="Interrupted"/>).
    /// </summary>
    public SessionCloseReason? CloseReason { get; init; }

    public string View { get; init; } = "terminal";

    public int ActiveIndex { get; init; }

    /// <summary>The main window's placement.</summary>
    public SavedWindow? Window { get; init; }

    public List<SavedTab> Tabs { get; init; } = [];

    /// <summary>Per-view layout overrides chosen through <c>layout.*</c> during the session.</summary>
    public Dictionary<string, string> LayoutOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tabs the user closed during this run, newest last; carried across restarts so "reopen closed tab" survives one.</summary>
    public List<SavedTab> RecentlyClosed { get; init; } = [];

    /// <summary>True when the run that wrote this file did not get to say how it ended. Version-1 files never say, so they never count.</summary>
    [JsonIgnore]
    public bool Interrupted => Version >= 2 && CloseReason is null;

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

    /// <summary>
    /// The content worth comparing between two saves: everything but the timestamp and the
    /// close reason, which change on every write and on the last one respectively.
    /// </summary>
    public string ComparableJson()
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(this, Jsonc.SerializerOptions)!.AsObject();
        node.Remove("savedAt");
        node.Remove("startedAt");
        node.Remove("closeReason");
        return node.ToJsonString();
    }

    /// <summary>A one-line description for pickers: "4 tabs · OpenCode ×2, PowerShell ×2".</summary>
    public static string Describe(IReadOnlyList<SavedTab> tabs, Func<SavedTab, string> nameOf)
    {
        if (tabs.Count == 0)
        {
            return "no tabs";
        }

        var groups = tabs.GroupBy(nameOf, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
        return $"{tabs.Count} tab{(tabs.Count == 1 ? string.Empty : "s")} · {string.Join(", ", groups)}";
    }
}
