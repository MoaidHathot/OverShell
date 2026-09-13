using System.Text.RegularExpressions;
using OverShell.App.Terminal.Hyperlinks;

namespace OverShell.App.Terminal;

/// <summary>What kind of out-of-band signal the application in the terminal sent.</summary>
internal enum TerminalSignalKind
{
    /// <summary>A bare BEL, not one terminating an OSC string.</summary>
    Bell,

    /// <summary>OSC 9 (ConEmu/WT), OSC 99 (kitty) or OSC 777 (rxvt-unicode) desktop notification.</summary>
    Notification,

    /// <summary>OSC 9;4 taskbar progress: <see cref="TerminalSignal.State"/> 0–4, <see cref="TerminalSignal.Percent"/> 0–100.</summary>
    Progress,

    /// <summary>OSC 133 shell-integration mark: <see cref="TerminalSignal.Mark"/> is A, B, C or D.</summary>
    PromptMark,
}

/// <summary>One signal, in stream order. Timestamps are taken when the chunk arrived.</summary>
internal readonly record struct TerminalSignal(
    TerminalSignalKind Kind,
    DateTimeOffset At,
    string? Title = null,
    string? Body = null,
    int State = 0,
    int Percent = 0,
    char Mark = '\0',
    int? ExitCode = null);

/// <summary>
/// The little bit of terminal state OverShell needs to know about but the native control
/// does not expose: which private modes the application has switched on, which OSC 8
/// hyperlinks it has printed, and the out-of-band signals — bells, notifications,
/// progress, shell-integration marks — that say what the program in the tab is doing.
/// Fed with every output chunk, in order, on the I/O thread.
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

    // Any complete OSC string: ESC ] <number> ; <payload> BEL|ST. The number picks the handler below.
    [GeneratedRegex("\u001b\\]([0-9]+)(?:;([^\u0007\u001b]*))?(?:\u0007|\u001b\\\\)", RegexOptions.Compiled)]
    private static partial Regex OscRegex { get; }

    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled)]
    private static partial Regex CsiRegex { get; }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _ledger = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _ledgerOrder = new();
    private string _carry = string.Empty;

    private volatile bool _bracketedPaste;
    private volatile bool _mouseTracking;
    private volatile bool _focusEvents;

    /// <summary>DECSET 2004: the application wants pasted text wrapped in ESC[200~ … ESC[201~.</summary>
    public bool BracketedPaste => _bracketedPaste;

    /// <summary>DECSET 1000/1002/1003: the application consumes mouse clicks itself.</summary>
    public bool MouseTracking => _mouseTracking;

    /// <summary>DECSET 1004: the application wants ESC[I / ESC[O when the terminal gains or loses focus.</summary>
    public bool FocusEvents => _focusEvents;

    /// <summary>
    /// Raised on the I/O thread for every bell, notification, progress report and prompt
    /// mark, in stream order. Handlers must be cheap; the tab queues them for the UI thread.
    /// </summary>
    public event Action<TerminalSignal>? Signal;

    public void Observe(string chunk)
    {
        var now = DateTimeOffset.Now;
        List<TerminalSignal>? signals = null;

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
                        case "1004":
                            _focusEvents = enable;
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

            // Bells that terminate an OSC are string terminators, not bells: every complete
            // OSC in the new part is decoded once and its terminator remembered.
            HashSet<int>? terminators = null;
            var lastOscEnd = 0;
            foreach (Match m in OscRegex.Matches(text))
            {
                lastOscEnd = m.Index + m.Length;
                if (lastOscEnd <= newFrom)
                {
                    continue;
                }

                if (text[lastOscEnd - 1] == '\u0007')
                {
                    (terminators ??= []).Add(lastOscEnd - 1);
                }

                Decode(m, now, ref signals);
            }

            // An OSC that has begun but not ended waits for the next chunk: nothing after its
            // introducer can be judged yet, and its BEL, when it comes, completes the match. A
            // stray ESC inside it means it was never going to complete (a VT parser would have
            // abandoned it too), so judging resumes.
            var lastOpen = text.LastIndexOf("\u001b]", StringComparison.Ordinal);
            var unterminated = lastOpen >= lastOscEnd && text.IndexOf('\u001b', lastOpen + 2) < 0;
            var judgeUntil = unterminated ? lastOpen : text.Length;
            for (var i = newFrom; i < judgeUntil; i++)
            {
                if (text[i] == '\u0007' && terminators?.Contains(i) != true)
                {
                    (signals ??= []).Add(new TerminalSignal(TerminalSignalKind.Bell, now));
                }
            }

            _carry = text.Length <= CarryLength ? text : text[^CarryLength..];
        }

        if (signals is not null && Signal is { } handler)
        {
            foreach (var signal in signals)
            {
                handler(signal);
            }
        }
    }

    /// <summary>Turns one complete OSC string into a signal, if it is one we understand.</summary>
    private static void Decode(Match m, DateTimeOffset now, ref List<TerminalSignal>? signals)
    {
        var number = m.Groups[1].Value;
        var payload = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;

        switch (number)
        {
            case "9":
                // ConEmu family: 9;4;<state>;<percent> is progress, 9;9;<cwd> is the working
                // directory (read elsewhere), anything else is a notification body.
                if (payload.StartsWith("4;", StringComparison.Ordinal) || payload == "4")
                {
                    var parts = payload.Split(';');
                    var state = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
                    var percent = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
                    (signals ??= []).Add(new TerminalSignal(TerminalSignalKind.Progress, now, State: state, Percent: percent));
                }
                else if (!payload.StartsWith("9;", StringComparison.Ordinal) && payload.Length > 0 && !char.IsAsciiDigit(payload[0]))
                {
                    (signals ??= []).Add(new TerminalSignal(TerminalSignalKind.Notification, now, Body: payload));
                }

                break;

            case "99":
                // kitty: 99;<key=value:key=value>;<payload>. p=title or p=body (default body).
                {
                    var separator = payload.IndexOf(';');
                    var metadata = separator < 0 ? payload : payload[..separator];
                    var body = separator < 0 ? string.Empty : payload[(separator + 1)..];
                    var isTitle = metadata.Split(':').Any(kv => kv == "p=title");
                    if (body.Length > 0)
                    {
                        (signals ??= []).Add(isTitle
                            ? new TerminalSignal(TerminalSignalKind.Notification, now, Title: body, Body: string.Empty)
                            : new TerminalSignal(TerminalSignalKind.Notification, now, Body: body));
                    }
                }

                break;

            case "777":
                // rxvt-unicode: 777;notify;<title>;<body>
                {
                    var parts = payload.Split(';', 3);
                    if (parts.Length >= 2 && parts[0] == "notify")
                    {
                        (signals ??= []).Add(new TerminalSignal(TerminalSignalKind.Notification, now, Title: parts[1], Body: parts.Length > 2 ? parts[2] : string.Empty));
                    }
                }

                break;

            case "133":
                // FinalTerm marks: A prompt start, B prompt end / command start, C command output start, D;<exit> command end.
                if (payload.Length > 0 && payload[0] is 'A' or 'B' or 'C' or 'D')
                {
                    int? exit = payload[0] == 'D' && payload.Length > 2 && int.TryParse(payload.AsSpan(2), out var code) ? code : null;
                    (signals ??= []).Add(new TerminalSignal(TerminalSignalKind.PromptMark, now, Mark: payload[0], ExitCode: exit));
                }

                break;
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
