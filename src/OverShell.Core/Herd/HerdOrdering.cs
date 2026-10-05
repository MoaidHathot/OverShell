using OverShell.Core.Agents;

namespace OverShell.Core.Herd;

/// <summary>What the sidebar needs to know about one tab to place it.</summary>
public sealed record HerdEntry(
    string TabId,
    string Project,
    AgentState State,
    bool Unread,
    bool IsAgent,
    DateTimeOffset? LastActivity,
    int Index);

/// <summary>One project's tabs, with the worst state among them as the rollup.</summary>
public sealed record HerdGroup(string Project, AgentState Rollup, int NeedingAttention, IReadOnlyList<HerdEntry> Tabs);

/// <summary>
/// The Herd sidebar's order (DESIGN.md §12.5): tabs grouped by project, each group sorted
/// attention first then most recent; groups by their worst state, then recency, then
/// name. Pure so the ordering can be tested without a window and re-run on every
/// heartbeat cheaply.
/// </summary>
public static class HerdOrdering
{
    /// <summary>Lower ranks first. Blocked and Error are the same urgency; an unseen Done is next.</summary>
    public static int AttentionRank(AgentState state, bool unread) => state switch
    {
        AgentState.Blocked or AgentState.Error => 0,
        AgentState.Done when unread => 1,
        AgentState.Working => 2,
        AgentState.Idle or AgentState.Done => 3,
        AgentState.Exited => 5,
        _ => 4,
    };

    public static bool NeedsAttention(AgentState state, bool unread) => AttentionRank(state, unread) <= 1;

    /// <summary>A tab waiting for the user, with how long it has waited, for <see cref="NextWaiting"/>.</summary>
    public sealed record Waiting(string TabId, AgentState State, bool Unread, DateTimeOffset? Since, int Index);

    /// <summary>
    /// The tab to jump to among those waiting (§12.16): the one that has waited longest when
    /// <paramref name="byAge"/>, else the next after <paramref name="currentIndex"/> in strip
    /// order, wrapping. Blocked and error tabs come before unseen-done ones in both modes;
    /// <paramref name="direction"/> -1 walks the other way (the newest, or the previous).
    /// Returns null when nothing waits; the current tab is skipped unless it is the only one.
    /// </summary>
    public static Waiting? NextWaiting(IReadOnlyList<Waiting> waiting, int currentIndex, bool byAge, int direction = 1, bool blockedOnly = false, bool doneOnly = false)
    {
        var candidates = waiting
            .Where(w => NeedsAttention(w.State, w.Unread))
            .Where(w => !blockedOnly || w.State is AgentState.Blocked or AgentState.Error)
            .Where(w => !doneOnly || w.State == AgentState.Done)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var others = candidates.Where(w => w.Index != currentIndex).ToList();
        if (others.Count == 0)
        {
            return candidates[0];
        }

        if (byAge)
        {
            var ordered = others
                .OrderBy(w => AttentionRank(w.State, w.Unread))
                .ThenBy(w => w.Since ?? DateTimeOffset.MaxValue)
                .ThenBy(w => w.Index)
                .ToList();
            return direction >= 0 ? ordered[0] : ordered[^1];
        }

        var count = waiting.Count == 0 ? 1 : waiting.Max(w => w.Index) + 1;
        for (var rank = 0; rank <= 1; rank++)
        {
            for (var step = 1; step <= count; step++)
            {
                var index = ((currentIndex + step * direction) % count + count) % count;
                var hit = others.FirstOrDefault(w => w.Index == index && AttentionRank(w.State, w.Unread) == rank);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return others[0];
    }
    public static IReadOnlyList<HerdGroup> Group(IEnumerable<HerdEntry> entries)
    {
        var groups = new List<HerdGroup>();
        foreach (var byProject in entries.GroupBy(e => e.Project, StringComparer.OrdinalIgnoreCase))
        {
            var tabs = byProject
                .OrderBy(e => AttentionRank(e.State, e.Unread))
                .ThenByDescending(e => e.LastActivity ?? DateTimeOffset.MinValue)
                .ThenBy(e => e.Index)
                .ToList();

            var worst = tabs.MinBy(e => AttentionRank(e.State, e.Unread))!;
            groups.Add(new HerdGroup(byProject.Key, worst.State, tabs.Count(e => NeedsAttention(e.State, e.Unread)), tabs));
        }

        return groups
            .OrderBy(g => g.Tabs.Min(e => AttentionRank(e.State, e.Unread)))
            .ThenByDescending(g => g.Tabs.Max(e => e.LastActivity ?? DateTimeOffset.MinValue))
            .ThenBy(g => g.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
