namespace OverShell.Core.Input;

public interface IKeyBindingLayer : IDisposable
{
    /// <summary>Replace this owner's defaults. User bindings and explicit unbound entries still win.</summary>
    void Replace(IEnumerable<Keybinding> bindings);
}

/// <summary>Live, disposable extension defaults. Runtime tab-owned shortcuts remain a separate higher layer.</summary>
public sealed class KeyBindingLayers
{
    private readonly List<Layer> _layers = [];
    public event Action? Changed;
    public IEnumerable<Keybinding> Bindings => _layers.SelectMany(l => l.Bindings);

    public IKeyBindingLayer Create(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var layer = new Layer(this);
        _layers.Add(layer);
        return layer;
    }

    private sealed class Layer(KeyBindingLayers host) : IKeyBindingLayer
    {
        internal IReadOnlyList<Keybinding> Bindings { get; private set; } = [];
        private bool _disposed;
        public void Replace(IEnumerable<Keybinding> bindings)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Bindings = bindings.Select(b => b with { Arguments = b.Arguments?.DeepClone().AsObject() }).ToArray();
            host.Changed?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            host._layers.Remove(this);
            if (Bindings.Count > 0) host.Changed?.Invoke();
            Bindings = [];
        }
    }
}
