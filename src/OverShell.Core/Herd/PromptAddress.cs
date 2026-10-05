namespace OverShell.Core.Herd;

/// <summary>What the parser needs to know about a tab to address it.</summary>
public sealed record AddressableTab(string Id, int Index, string Label, string? Group, string State, bool IsAgent, bool IsRunning);

/// <summary>Where a prompt goes: the tabs, and the words that chose them, for the bar to echo.</summary>
/// <param name="Body">The prompt with the address words removed.</param>
/// <param name="Targets">The tabs addressed, in strip order; empty when an address matched nothing.</param>
/// <param name="Addressed">Whether any address word was present at all.</param>
/// <param name="Unmatched">Address words that named nothing.</param>
public sealed record PromptRoute(string Body, IReadOnlyList<AddressableTab> Targets, bool Addressed, IReadOnlyList<string> Unmatched);

/// <summary>
/// Address words at the front of a prompt (DESIGN.md §12.16), the grammar the switcher
/// already uses: <c>@3</c> a tab by number, <c>@blocked</c> / <c>@working</c> / <c>@done</c> /
/// <c>@idle</c> by state, <c>@agents</c> / <c>@all</c> / <c>@active</c> the prompt bar's
/// targets, <c>#group</c> a group, <c>@label</c> a tab by label (prefix, case-insensitive).
/// Several may be combined; the rest of the line is the prompt. A word that names nothing
/// is reported rather than guessed - a prompt sent to the wrong agent is worse than one
/// not sent.
/// </summary>
public static class PromptAddress
{
    /// <param name="text">The prompt as typed.</param>
    /// <param name="tabs">The open tabs in strip order.</param>
    /// <param name="activeId">The active tab, for <c>@active</c> and for a prompt with no address.</param>
    public static PromptRoute Parse(string text, IReadOnlyList<AddressableTab> tabs, string? activeId)
    {
        var chosen = new List<AddressableTab>();
        var unmatched = new List<string>();
        var addressed = false;
        var rest = text.TrimStart();

        while (rest.Length > 0 && rest[0] is '@' or '#')
        {
            var end = rest.IndexOfAny([' ', '\t', '\n', '\r']);
            var word = end < 0 ? rest : rest[..end];
            rest = end < 0 ? string.Empty : rest[(end + 1)..].TrimStart();
            if (word.Length == 1)
            {
                // A lone @ or # is text, not an address.
                rest = word + (rest.Length == 0 ? string.Empty : " " + rest);
                break;
            }

            addressed = true;
            var matches = Resolve(word, tabs, activeId);
            if (matches.Count == 0)
            {
                unmatched.Add(word);
            }

            foreach (var tab in matches)
            {
                if (!chosen.Contains(tab))
                {
                    chosen.Add(tab);
                }
            }
        }

        var targets = addressed
            ? chosen.OrderBy(t => t.Index).ToList()
            : tabs.Where(t => t.Id == activeId).ToList();

        return new PromptRoute(rest, targets, addressed, unmatched);
    }

    private static IReadOnlyList<AddressableTab> Resolve(string word, IReadOnlyList<AddressableTab> tabs, string? activeId)
    {
        var name = word[1..];
        if (word[0] == '#')
        {
            return tabs.Where(t => t.Group is not null && t.Group.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (int.TryParse(name, out var number) && number >= 1)
        {
            return tabs.Where(t => t.Index == number - 1).ToList();
        }

        switch (name.ToLowerInvariant())
        {
            case "all":
                return tabs.Where(t => t.IsRunning).ToList();
            case "agents":
                return tabs.Where(t => t.IsAgent && t.IsRunning).ToList();
            case "active":
                return tabs.Where(t => t.Id == activeId).ToList();
            case "blocked" or "working" or "done" or "idle" or "error":
                return tabs.Where(t => t.IsRunning && string.Equals(t.State, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // A label: exact first, else a unique prefix, else every prefix match (the user sees the echo).
        var exact = tabs.Where(t => string.Equals(t.Label, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        return tabs.Where(t => t.Label.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>"→ api, web" / "→ 3 tabs" for the bar; empty when nothing is addressed.</summary>
    public static string Describe(PromptRoute route)
    {
        if (!route.Addressed)
        {
            return string.Empty;
        }

        var names = route.Targets.Select(t => t.Label).ToList();
        var shown = names.Count <= 3 ? string.Join(", ", names) : $"{names.Count} tabs";
        var missing = route.Unmatched.Count == 0 ? string.Empty : $"  (no match: {string.Join(", ", route.Unmatched)})";
        return names.Count == 0 ? $"\u2192 nobody{missing}" : $"\u2192 {shown}{missing}";
    }
}
