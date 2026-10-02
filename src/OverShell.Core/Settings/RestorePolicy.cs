namespace OverShell.Core.Settings;

/// <summary>
/// Whether a start should restore the last session or hold off (DESIGN.md §12.14). A
/// session file that brings the program down every time it is restored would otherwise be
/// restored forever; browsers break that loop the same way. Pure: the previous live file
/// and the archives go in, a decision comes out.
/// </summary>
public static class RestorePolicy
{
    /// <summary>A run that ended within this long of starting "died right after starting".</summary>
    public static readonly TimeSpan EarlyDeath = TimeSpan.FromSeconds(60);

    /// <summary>How many consecutive early, interrupted runs it takes to hold the restore.</summary>
    public const int Strikes = 2;

    /// <summary>
    /// True when the last <see cref="Strikes"/> runs all ended interrupted within
    /// <see cref="EarlyDeath"/> of starting: <paramref name="previous"/> is the live file
    /// the last run left, <paramref name="archives"/> the history, newest first - the
    /// interrupted one before it is the newest interrupted archive. A run that lived
    /// longer, closed cleanly, or predates the start stamp (a version-2 file from before
    /// this policy) breaks the chain.
    /// </summary>
    public static bool ShouldHold(SessionSnapshot? previous, IReadOnlyList<ArchivedSession> archives)
    {
        if (previous is null || !DiedEarly(previous.Interrupted, previous.StartedAt, previous.SavedAt))
        {
            return false;
        }

        var strikes = 1;
        foreach (var archive in archives)
        {
            if (strikes >= Strikes)
            {
                break;
            }

            if (!archive.Interrupted || !DiedEarly(true, archive.StartedAt, archive.SavedAt))
            {
                return false;
            }

            strikes++;
        }

        return strikes >= Strikes;
    }

    private static bool DiedEarly(bool interrupted, DateTimeOffset? startedAt, DateTimeOffset savedAt) =>
        interrupted && startedAt is { } started && savedAt - started < EarlyDeath;
}