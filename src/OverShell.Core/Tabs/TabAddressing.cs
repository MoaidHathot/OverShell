using System.Text.Json.Nodes;

namespace OverShell.Core.Tabs;

public sealed record TabSelector(string? Id = null, string? Alias = null, string? Label = null)
{
    public static TabSelector? Parse(JsonNode? node)
    {
        if (node is not JsonObject obj || obj.Count != 1) return null;
        if (obj["id"] is JsonValue id && id.TryGetValue<string>(out var i)) return new(Id: i);
        if (obj["alias"] is JsonValue alias && alias.TryGetValue<string>(out var a)) return new(Alias: a);
        if (obj["label"] is JsonValue label && label.TryGetValue<string>(out var l)) return new(Label: l);
        return null;
    }
}

public sealed record TabIdentity(string Id, string Label);
public sealed record TabResolution(string? TabId, string? Error = null);
public sealed record TabActionResult(bool Success, string? Error = null)
{
    public static readonly TabActionResult Ok = new(true);
    public static TabActionResult Fail(string error) => new(false, error);
}
public sealed record AliasChange(string TabId, string? Previous, string? Alias);

public interface ITabAddressing
{
    string? AliasFor(string tabId);
    TabActionResult Assign(string tabId, string? alias);
    TabResolution Resolve(TabSelector selector);
    event Action<AliasChange>? Changed;
}

/// <summary>Runtime addresses, deliberately outside session serialization and agent metadata.</summary>
public sealed class TabAddressing(Func<IReadOnlyList<TabIdentity>> tabs) : ITabAddressing
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "all", "agents", "active", "blocked", "working", "done", "idle", "error", "exited", "unknown" };

    public event Action<AliasChange>? Changed;
    public string? AliasFor(string tabId) => _aliases.GetValueOrDefault(tabId);

    public static string? Validate(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        alias = alias.Trim();
        if (alias.Length > 64 || !char.IsAsciiLetter(alias[0]) || alias.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            return "Use 1-64 letters, digits, dots, underscores or hyphens, starting with a letter";
        return Reserved.Contains(alias) ? $"'{alias}' is reserved for prompt addressing" : null;
    }

    public TabActionResult Assign(string tabId, string? alias)
    {
        var tab = tabs().FirstOrDefault(t => t.Id.Equals(tabId, StringComparison.OrdinalIgnoreCase));
        if (tab is null) return TabActionResult.Fail("The tab has closed");
        tabId = tab.Id;
        alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (Validate(alias) is { } problem) return TabActionResult.Fail(problem);
        if (alias is not null && _aliases.Any(p => p.Key != tabId && p.Value.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            return TabActionResult.Fail($"Alias '{alias}' already belongs to another open tab");
        var previous = AliasFor(tabId);
        if (previous == alias) return TabActionResult.Ok;
        if (alias is null) _aliases.Remove(tabId); else _aliases[tabId] = alias;
        Changed?.Invoke(new(tabId, previous, alias));
        return TabActionResult.Ok;
    }

    public void Forget(string tabId)
    {
        if (_aliases.Remove(tabId, out var previous)) Changed?.Invoke(new(tabId, previous, null));
    }

    public TabResolution Resolve(TabSelector selector)
    {
        if (new[] { selector.Id, selector.Alias, selector.Label }.Count(v => v is not null) != 1)
            return new(null, "Specify exactly one tab ID, alias or unique label");
        var matches = tabs().Where(t => selector.Id is { } id ? t.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            : selector.Alias is { } alias ? string.Equals(AliasFor(t.Id), alias, StringComparison.OrdinalIgnoreCase)
            : string.Equals(t.Label, selector.Label, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length switch
        {
            1 => new(matches[0].Id),
            0 => new(null, $"No open tab matches '{selector.Id ?? selector.Alias ?? selector.Label}'"),
            _ => new(null, "More than one tab has that label; use its alias or ID"),
        };
    }
}
