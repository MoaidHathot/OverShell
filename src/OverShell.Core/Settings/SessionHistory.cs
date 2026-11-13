using System.Globalization;

namespace OverShell.Core.Settings;

/// <summary>One archived session file, as listed for the history picker.</summary>
public sealed record ArchivedSession(string Path, DateTimeOffset SavedAt, DateTimeOffset? StartedAt, bool Interrupted, SessionCloseReason? CloseReason, IReadOnlyList<SavedTab> Tabs);

/// <summary>
/// Earlier sessions, kept under <c>sessions\</c> in the state root the way a browser keeps
/// "recently closed windows" (DESIGN.md §12.13): a copy of the live file is archived when
/// the window closes cleanly, and at start when the live file turns out to be interrupted —
/// so a session lost to a crash is never overwritten by the run that follows it before the
/// user had a chance to bring it back. The newest <see cref="Keep"/> are kept.
/// </summary>
public static class SessionHistory
{
    public const int Keep = 10;

    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    public static string Directory(string stateRoot) => Path.Combine(stateRoot, "sessions");

    /// <summary>
    /// Archives <paramref name="snapshot"/> unless it is empty or its tabs are the same as the
    /// newest archive's and no crash-history boundary would be lost. Repeated clean
    /// sessions collapse; distinct interrupted runs and clean closes after a crash must
    /// remain distinguishable for the restore-loop policy.
    /// Returns the archive path, or null when nothing was written.
    /// </summary>
    public static string? Archive(string stateRoot, SessionSnapshot snapshot, out string? error)
    {
        error = null;
        var directory = Directory(stateRoot);
        try
        {
            var newest = List(stateRoot).FirstOrDefault();
            // Closing the final tab is a clean run boundary as well. Keep that
            // marker after an interrupted run, even though it has no tabs to reopen.
            if (snapshot.Tabs.Count == 0 && newest?.Interrupted != true) return null;
            System.IO.Directory.CreateDirectory(directory);
            if (newest is not null && newest.Interrupted == snapshot.Interrupted &&
                (!snapshot.Interrupted || newest.StartedAt == snapshot.StartedAt) &&
                TabsJson(newest.Tabs) == TabsJson(snapshot.Tabs))
            {
                return null;
            }

            var stamp = snapshot.SavedAt.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var name = $"{stamp}{(snapshot.Interrupted ? "-interrupted" : string.Empty)}.json";
            var path = Path.Combine(directory, name);
            for (var i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(directory, $"{stamp}{(snapshot.Interrupted ? "-interrupted" : string.Empty)}-{i}.json");
            }

            // The archive says how the run ended even when the live file could not: a copy
            // of an interrupted file keeps its missing close reason, which is the point.
            if (!snapshot.Save(path, out error))
            {
                return null;
            }

            Prune(directory);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Every archive that still parses, newest first.</summary>
    public static IReadOnlyList<ArchivedSession> List(string stateRoot)
    {
        var directory = Directory(stateRoot);
        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<ArchivedSession>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            if (SessionSnapshot.Load(file, out _) is { } snapshot)
            {
                result.Add(new ArchivedSession(file, snapshot.SavedAt, snapshot.StartedAt, snapshot.Interrupted, snapshot.CloseReason, snapshot.Tabs));
            }
        }

        return result.OrderByDescending(s => s.SavedAt).ToList();
    }

    /// <summary>The tabs as identity: what they run and where, not where their windows sat or when they closed.</summary>
    private static string TabsJson(IReadOnlyList<SavedTab> tabs) =>
        System.Text.Json.JsonSerializer.Serialize(tabs.Select(t => new { t.ProfileId, t.WorkingDirectory, t.Label, t.SessionTitle, t.TerminalTitle, t.Group, t.Harness, t.AgentRunning, t.SessionId, t.ResumeCommand, t.Detached }), Jsonc.SerializerOptions);

    private static void Prune(string directory)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Keep);
        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Someone has it open; next time.
            }
        }
    }
}
