namespace OverShell.Core.Input;

public sealed record RuntimeBinding(Guid Id, string TabId, Keybinding Binding, bool Global);
public sealed record RuntimeBindingResult(RuntimeBinding? Registration, string? Error = null);

/// <summary>Tab-owned registrations. Prefix conflicts matter as much as identical keys.</summary>
public sealed class RuntimeBindings
{
    private readonly List<RuntimeBinding> _items = [];
    public IReadOnlyList<RuntimeBinding> Items => _items;

    public RuntimeBindingResult Add(string tabId, Keybinding binding, KeybindingMap effective, bool global = false)
    {
        if (!KeySequence.TryParse(binding.Keys, out var sequence)) return new(null, "Invalid shortcut or key sequence");
        if (global && sequence.Length != 1) return new(null, "Global shortcuts must be a single chord");
        var conflict = effective.Bindings.Keys.Concat(_items.Select(i => { KeySequence.TryParse(i.Binding.Keys, out var s); return s; }))
            .FirstOrDefault(s => s.Equals(sequence) || s.StartsWith(sequence.Chords) || sequence.StartsWith(s.Chords));
        if (conflict is not null) return new(null, $"Shortcut conflicts with {conflict}; choose another chord or sequence");
        var item = new RuntimeBinding(Guid.NewGuid(), tabId, binding with { Arguments = binding.Arguments?.DeepClone().AsObject() }, global);
        _items.Add(item);
        return new(item);
    }

    public bool Remove(Guid id) => _items.RemoveAll(i => i.Id == id) > 0;
}
