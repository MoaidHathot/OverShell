namespace OverShell.Core.Input;

/// <summary>
/// A disposable one-shot key map. Its captured bindings survive changes to the normal
/// map; completing, rejecting, Escape and timeout close it before a command runs.
/// </summary>
public sealed class KeyMode : IDisposable
{
    private readonly KeySequenceDispatcher _dispatcher;
    private DateTimeOffset _lastInput;
    public bool IsActive { get; private set; } = true;
    public event Action? Closed;
    public event Action? PendingChanged;
    public IReadOnlyList<KeyChord> Pending => _dispatcher.Pending;

    public KeyMode(IEnumerable<Keybinding> bindings, TimeSpan timeout, DateTimeOffset now)
    {
        var map = new KeybindingMap();
        map.Apply(bindings.Select(b => b with { Stay = false }), "key mode");
        map.ReportShadowed();
        if (map.Problems.Count > 0) throw new ArgumentException(string.Join("; ", map.Problems), nameof(bindings));
        _dispatcher = new KeySequenceDispatcher(map, timeout);
        _lastInput = now;
    }

    public KeyDecision Feed(KeyChord chord, DateTimeOffset now)
    {
        if (!IsActive) return new(KeyOutcome.Unbound, null, []);
        if (Expire(now) || chord.Key == "Escape")
        {
            Dispose();
            return new(KeyOutcome.Cancelled, null, []);
        }
        _lastInput = now;
        var decision = _dispatcher.Feed(chord, now);
        if (decision.Outcome == KeyOutcome.Unbound) decision = new(KeyOutcome.Rejected, null, []);
        if (decision.Outcome != KeyOutcome.Pending) Dispose();
        else PendingChanged?.Invoke();
        return decision;
    }

    public bool Expire(DateTimeOffset now)
    {
        if (!IsActive || now - _lastInput <= _dispatcher.Timeout) return false;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        if (!IsActive) return;
        IsActive = false;
        _dispatcher.Cancel();
        Closed?.Invoke();
        Closed = null;
        PendingChanged = null;
    }
}
