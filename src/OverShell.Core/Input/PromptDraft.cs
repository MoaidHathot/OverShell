namespace OverShell.Core.Input;

/// <summary>A text-and-file draft, snapshotted for submission and history.</summary>
public sealed class PromptDraft
{
    public PromptDraft(string text, IEnumerable<string>? files = null)
    {
        Text = text;
        Files = Array.AsReadOnly((files ?? []).ToArray());
    }

    public string Text { get; }
    public IReadOnlyList<string> Files { get; }
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Files.Count == 0;

    public string Compose(Func<IReadOnlyList<string>, string> formatFiles)
    {
        if (Files.Count == 0) return Text;
        var references = formatFiles(Files);
        return Text + (Text.Length > 0 && !char.IsWhiteSpace(Text[^1]) ? " " : string.Empty) + references;
    }

    public bool SameAs(PromptDraft other) => Text == other.Text && Files.SequenceEqual(other.Files, StringComparer.Ordinal);
}

/// <summary>History keeps attachments and returns to the unsent draft when walking forward.</summary>
public sealed class PromptHistory
{
    private readonly List<PromptDraft> _entries = [];
    private int _index = -1;
    private PromptDraft? _unsent;

    public IReadOnlyList<PromptDraft> Entries => _entries.AsReadOnly();
    public bool IsBrowsing => _index >= 0;

    public void Record(PromptDraft draft)
    {
        if (!draft.IsEmpty && (_entries.Count == 0 || !_entries[^1].SameAs(draft)))
        {
            _entries.Add(draft);
            if (_entries.Count > 100) _entries.RemoveAt(0);
        }

        _index = -1;
        _unsent = null;
    }

    public PromptDraft? Previous(PromptDraft current)
    {
        if (_entries.Count == 0) return null;
        if (_index < 0)
        {
            _unsent = current;
            _index = _entries.Count - 1;
        }
        else _index = Math.Max(0, _index - 1);
        return _entries[_index];
    }

    public PromptDraft? Next()
    {
        if (_index < 0) return null;
        if (++_index < _entries.Count) return _entries[_index];
        _index = -1;
        var result = _unsent ?? new PromptDraft(string.Empty);
        _unsent = null;
        return result;
    }
}
