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
