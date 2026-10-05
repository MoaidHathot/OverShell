namespace OverShell.Core.Input;

/// <summary>What the dispatcher decided about one chord.</summary>
public enum KeyOutcome
{
    /// <summary>Not bound, nothing pending: the chord belongs to the terminal.</summary>
    Unbound,

    /// <summary>Run the command in <see cref="KeyDecision.Command"/>.</summary>
    Command,

    /// <summary>The chord starts (or continues) a sequence; wait for the next one.</summary>
    Pending,

    /// <summary>A sequence was pending and this chord does not continue it: swallowed, the sequence is over.</summary>
    Rejected,

    /// <summary>Escape (or the timeout) ended a pending sequence.</summary>
    Cancelled,
}

/// <param name="Outcome">What to do.</param>
/// <param name="Command">The binding to run, for <see cref="KeyOutcome.Command"/>.</param>
/// <param name="Pending">The chords pending after this one (empty when none).</param>
public sealed record KeyDecision(KeyOutcome Outcome, BoundCommand? Command, IReadOnlyList<KeyChord> Pending);

/// <summary>
/// Turns chords into commands with sequences in mind (DESIGN.md §12.16). While a sequence
/// is pending every chord is taken: one that completes a binding runs it, one that is a
/// longer prefix keeps waiting, anything else ends the sequence without reaching the
/// terminal - a leader key that leaked half its sequence into the shell would be worse
/// than no leader key. A binding marked <c>stay</c> keeps its prefix pending after running,
/// so the last chord repeats. The timeout is the caller's clock: <see cref="Expire"/>.
/// </summary>
public sealed class KeySequenceDispatcher
{
    private readonly List<KeyChord> _pending = [];

    public KeySequenceDispatcher(KeybindingMap map, TimeSpan timeout)
    {
        Map = map;
        Timeout = timeout;
    }

    public KeybindingMap Map { get; set; }

    public TimeSpan Timeout { get; set; }

    public IReadOnlyList<KeyChord> Pending => _pending;

    public bool IsPending => _pending.Count > 0;

    /// <summary>When the pending sequence started or was last extended.</summary>
    public DateTimeOffset PendingSince { get; private set; }

    public KeyDecision Feed(KeyChord chord, DateTimeOffset now)
    {
        if (_pending.Count > 0 && now - PendingSince > Timeout)
        {
            _pending.Clear();
        }

        if (_pending.Count > 0 && chord.Key == "Escape" && chord.Modifiers == ChordModifiers.None)
        {
            _pending.Clear();
            return new KeyDecision(KeyOutcome.Cancelled, null, _pending);
        }

        var candidate = new List<KeyChord>(_pending) { chord };
        switch (Map.Resolve(candidate, out var bound))
        {
            case KeyResolution.Command:
                if (bound!.Stay && _pending.Count > 0)
                {
                    // Keep the prefix (not the final chord) so the same key repeats the command.
                    PendingSince = now;
                    return new KeyDecision(KeyOutcome.Command, bound, _pending);
                }

                _pending.Clear();
                return new KeyDecision(KeyOutcome.Command, bound, _pending);

            case KeyResolution.Prefix:
                _pending.Clear();
                _pending.AddRange(candidate);
                PendingSince = now;
                return new KeyDecision(KeyOutcome.Pending, null, _pending);

            default:
                if (_pending.Count > 0)
                {
                    _pending.Clear();
                    return new KeyDecision(KeyOutcome.Rejected, null, _pending);
                }

                return new KeyDecision(KeyOutcome.Unbound, null, _pending);
        }
    }

    /// <summary>Ends a pending sequence that has waited too long. True when something was pending.</summary>
    public bool Expire(DateTimeOffset now)
    {
        if (_pending.Count == 0 || now - PendingSince <= Timeout)
        {
            return false;
        }

        _pending.Clear();
        return true;
    }

    public void Cancel() => _pending.Clear();
}
