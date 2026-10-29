namespace OverShell.Core.Tabs;

/// <summary>Host-independent return location. Host IDs are runtime tab IDs for secondary windows.</summary>
public sealed record NavigationPoint(string? TabId, string ViewId, string? HostId = null, string? Focus = null, string? CursorId = null);

public interface INavigationService
{
    NavigationPoint Capture();
    bool Restore(NavigationPoint point);
    void BeginAttention();
    bool Return();
    bool Last();
    bool CanReturn { get; }
}

/// <summary>Normal visits update recency; an attention chain keeps its first origin.</summary>
public sealed class NavigationHistory
{
    private readonly List<string> _recent = [];
    public NavigationPoint? Origin { get; private set; }

    public void Visit(string id, bool attention = false)
    {
        if (_recent.FirstOrDefault() == id) return;
        if (!attention) Origin = null;
        _recent.Remove(id);
        _recent.Insert(0, id);
    }

    public void Begin(NavigationPoint point) => Origin ??= point;
    public void End() => Origin = null;
    public void Forget(string id) => _recent.Remove(id);

    public string? Last(string? current, Func<string, bool> exists) =>
        _recent.FirstOrDefault(id => id != current && exists(id));

    public NavigationPoint? TakeReturn(string? current, Func<string, bool> exists)
    {
        var origin = Origin;
        Origin = null;
        if (origin is null) return null;
        return origin.TabId is { } id && exists(id) ? origin
            : Last(current, exists) is { } fallback ? origin with { TabId = fallback, HostId = null, Focus = null, CursorId = null } : null;
    }
}
