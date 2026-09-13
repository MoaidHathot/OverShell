using System.Text.RegularExpressions;
using OverShell.App.Terminal.Hyperlinks;

namespace OverShell.App.Terminal;

/// <summary>
/// The little bit of terminal state OverShell needs to know about but the native control
/// does not expose: which private modes the application has switched on, and which OSC 8
/// hyperlinks it has printed. Fed with every output chunk, in order, on the I/O thread.
/// <para>
/// Modes are tracked incrementally rather than by re-scanning a tail buffer: a shell
/// enables bracketed paste once at startup, and that sequence has long scrolled out of
/// any tail by the time the user pastes.
/// </para>
/// </summary>
internal sealed partial class TerminalStreamState
{
    // Longest sequence we must be able to reassemble across two chunks: an OSC 8 opener
    // with a long URI. Anything longer than this is not worth the buffer.
    private const int CarryLength = 2048;
    private const int LedgerCapacity = 128;

    // DECSET/DECRST: ESC [ ? <n> h|l — possibly several modes in one sequence.
    [GeneratedRegex("\u001b\\[\\?([0-9;]+)([hl])", RegexOptions.Compiled)]
    private static partial Regex PrivateModeRegex { get; }

    // OSC 8 ; params ; URI ST  <text>  OSC 8 ; ; ST — the text may carry SGR sequences.
    [GeneratedRegex("\u001b\\]8;[^;\u0007\u001b]*;([^\u0007\u001b]+)(?:\u0007|\u001b\\\\)(.*?)\u001b\\]8;;(?:\u0007|\u001b\\\\)", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex Osc8Regex { get; }

    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled)]
    private static partial Regex CsiRegex { get; }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _ledger = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _ledgerOrder = new();
    private string _carry = string.Empty;

    private volatile bool _bracketedPaste;
    private volatile bool _mouseTracking;

    /// <summary>DECSET 2004: the application wants pasted text wrapped in ESC[200~ … ESC[201~.</summary>
    public bool BracketedPaste => _bracketedPaste;

    /// <summary>DECSET 1000/1002/1003: the application consumes mouse clicks itself.</summary>
    public bool MouseTracking => _mouseTracking;

    public void Observe(string chunk)
    {
        lock (_gate)
        {
            var text = _carry.Length == 0 ? chunk : _carry + chunk;
            var newFrom = _carry.Length;

            foreach (Match m in PrivateModeRegex.Matches(text))
            {
                // Matches entirely inside the carried-over prefix were applied last time.
                if (m.Index + m.Length <= newFrom)
                {
                    continue;
                }

                var enable = m.Groups[2].Value == "h";
                foreach (var number in m.Groups[1].Value.Split(';'))
                {
                    switch (number)
                    {
                        case "2004":
                            _bracketedPaste = enable;
                            break;
                        case "1000" or "1002" or "1003":
                            _mouseTracking = enable;
                            break;
                    }
                }
            }

            foreach (Match m in Osc8Regex.Matches(text))
            {
                if (m.Index + m.Length <= newFrom)
                {
                    continue;
                }

                var visible = CsiRegex.Replace(m.Groups[2].Value, string.Empty).Trim();
                if (visible.Length > 0)
                {
                    Remember(visible, m.Groups[1].Value);
                }
            }

            _carry = text.Length <= CarryLength ? text : text[^CarryLength..];
        }
    }

    /// <summary>
    /// Finds an OSC 8 link whose visible text covers <paramref name="offset"/> in
    /// <paramref name="line"/>. Most recent link wins when several share the same text.
    /// </summary>
    public LinkMatch? FindOsc8Link(string line, int offset)
    {
        lock (_gate)
        {
            foreach (var text in _ledgerOrder)
            {
                var start = 0;
                while ((start = line.IndexOf(text, start, StringComparison.Ordinal)) >= 0)
                {
                    if (offset >= start && offset < start + text.Length)
                    {
                        return new LinkMatch(_ledger[text], start, text.Length);
                    }

                    start += 1;
                }
            }
        }

        return null;
    }

    private void Remember(string text, string uri)
    {
        if (_ledger.ContainsKey(text))
        {
            _ledgerOrder.Remove(text);
        }
        else if (_ledger.Count >= LedgerCapacity)
        {
            var oldest = _ledgerOrder.Last!.Value;
            _ledgerOrder.RemoveLast();
            _ledger.Remove(oldest);
        }

        _ledger[text] = uri;
        _ledgerOrder.AddFirst(text);
    }
}
