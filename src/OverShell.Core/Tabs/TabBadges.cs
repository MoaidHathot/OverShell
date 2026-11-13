namespace OverShell.Core.Tabs;

/// <summary>A small runtime-only navigation/decorative marker; never part of a tab's name or saved state.</summary>
public sealed record TabBadge(string Text, string? Help = null, bool Emphasized = false);

public interface ITabBadgeLayer : IDisposable
{
    /// <summary>Replace this owner's badges atomically. Omitted tabs lose this layer's badge.</summary>
    void Replace(IReadOnlyDictionary<string, TabBadge> badges);
}

public interface ITabBadges
{
    /// <summary>Later layers take precedence; disposal reveals the previous layer. UI-thread only.</summary>
    ITabBadgeLayer CreateLayer(string owner);
    TabBadge? For(string tabId);
}

/// <summary>Owned badge contributions, independent of WPF and session persistence.</summary>
public sealed class TabBadges(Func<string, bool> exists) : ITabBadges
{
    private readonly Func<string, bool> _exists = exists;
    private readonly List<Layer> _layers = [];
    public event Action<string, TabBadge?>? Changed;

    public ITabBadgeLayer CreateLayer(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var layer = new Layer(this);
        _layers.Add(layer);
        return layer;
    }

    public TabBadge? For(string tabId) => _layers.AsEnumerable().Reverse().Select(l => l.Badges.GetValueOrDefault(tabId)).FirstOrDefault(b => b is not null);

    public void Forget(string tabId)
    {
        Change([tabId], () => { foreach (var layer in _layers) layer.Badges.Remove(tabId); });
    }

    private void Change(IEnumerable<string> ids, Action update)
    {
        var before = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, For, StringComparer.OrdinalIgnoreCase);
        update();
        foreach (var (id, badge) in before)
            if (For(id) is var after && badge != after) Changed?.Invoke(id, after);
    }

    private sealed class Layer(TabBadges host) : ITabBadgeLayer
    {
        internal Dictionary<string, TabBadge> Badges { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public void Replace(IReadOnlyDictionary<string, TabBadge> badges)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var next = badges.Where(p => host._exists(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            host.Change(Badges.Keys.Concat(next.Keys).ToArray(), () => Badges = next);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            host.Change(Badges.Keys.ToArray(), () => host._layers.Remove(this));
            Badges.Clear();
        }
    }
}
