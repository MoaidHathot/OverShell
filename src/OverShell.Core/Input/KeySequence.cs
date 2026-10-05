namespace OverShell.Core.Input;

/// <summary>
/// One or more chords pressed in order - <c>ctrl+shift+k j</c> is the leader chord and then
/// <c>j</c> - written the way VS Code and Zed write them: chords separated by spaces. A single
/// chord is a sequence of one. Equality is by content, so a sequence is a dictionary key.
/// </summary>
public sealed class KeySequence : IEquatable<KeySequence>
{
    public KeySequence(IReadOnlyList<KeyChord> chords)
    {
        if (chords.Count == 0)
        {
            throw new ArgumentException("A key sequence needs at least one chord.", nameof(chords));
        }

        Chords = chords;
    }

    public KeySequence(KeyChord chord)
        : this([chord])
    {
    }

    public IReadOnlyList<KeyChord> Chords { get; }

    public int Length => Chords.Count;

    public KeyChord First => Chords[0];

    /// <summary>Parses <c>ctrl+shift+k j</c>; every chord must parse. A single chord is fine.</summary>
    public static bool TryParse(string? text, out KeySequence sequence)
    {
        sequence = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var chords = new List<KeyChord>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!KeyChord.TryParse(part, out var chord))
            {
                return false;
            }

            chords.Add(chord);
        }

        if (chords.Count == 0)
        {
            return false;
        }

        sequence = new KeySequence(chords);
        return true;
    }

    /// <summary>True when this sequence begins with <paramref name="prefix"/> and is longer than it.</summary>
    public bool StartsWith(IReadOnlyList<KeyChord> prefix)
    {
        if (prefix.Count >= Chords.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Count; i++)
        {
            if (!Chords[i].Equals(prefix[i]))
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(KeySequence? other)
    {
        if (other is null || other.Chords.Count != Chords.Count)
        {
            return false;
        }

        for (var i = 0; i < Chords.Count; i++)
        {
            if (!Chords[i].Equals(other.Chords[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is KeySequence other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var chord in Chords)
        {
            hash.Add(chord);
        }

        return hash.ToHashCode();
    }

    /// <summary>The user-facing form: <c>Ctrl+Shift+K J</c>.</summary>
    public override string ToString() => string.Join(' ', Chords.Select(c => c.ToString()));
}
