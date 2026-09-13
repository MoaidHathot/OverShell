using Microsoft.Terminal.Wpf;

namespace OverShell.App.Terminal.WindowsTerminal;

/// <summary>
/// Adapts an <see cref="ITerminalSession"/> to the <see cref="ITerminalConnection"/> shape
/// Microsoft's WPF terminal control expects. Pure plumbing: the control's only public
/// path to <c>TerminalSendOutput</c> is the <see cref="TerminalOutput"/> event of the
/// connection it has been given, and its key handling calls <see cref="WriteInput"/>.
/// <para>
/// Delivery is serialised with <see cref="Unbind"/>: once it returns, no output is in
/// flight into the native control, so the control can be torn down without racing the
/// session's I/O thread.
/// </para>
/// </summary>
internal sealed class SessionConnection : ITerminalConnection
{
    private readonly Lock _gate = new();
    private ITerminalSession? _session;
    private bool _bound;

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public void Bind(ITerminalSession session)
    {
        Unbind();

        lock (_gate)
        {
            _session = session;
            _bound = true;
        }

        session.OutputReceived += OnOutput;
    }

    public void Unbind()
    {
        ITerminalSession? session;

        // Bounded: if a delivery is somehow stuck inside the native control, a frozen tab
        // close would be worse than the sliver of a race this lock exists to remove.
        var entered = _gate.TryEnter(TimeSpan.FromMilliseconds(500));
        try
        {
            session = _session;
            _session = null;
            _bound = false;
        }
        finally
        {
            if (entered)
            {
                _gate.Exit();
            }
        }

        if (session is not null)
        {
            session.OutputReceived -= OnOutput;
        }
    }

    /// <summary>Feeds VT straight to the terminal, as if the child had written it.</summary>
    public void Inject(string vt)
    {
        lock (_gate)
        {
            if (_bound)
            {
                TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(vt));
            }
        }
    }

    /// <summary>Called by the control when the connection is assigned. Lifecycle belongs to the surface.</summary>
    public void Start()
    {
    }

    /// <summary>Called on the UI thread for every key the control encodes.</summary>
    public void WriteInput(string data) => _session?.WriteInput(data);

    /// <summary>Note the control's parameter order: rows first.</summary>
    public void Resize(uint rows, uint columns) => _session?.Resize((int)columns, (int)rows);

    /// <summary>Lifecycle belongs to the tab; the control never ends a session.</summary>
    public void Close()
    {
    }

    /// <summary>Runs on the session's I/O thread.</summary>
    private void OnOutput(object? sender, string chunk)
    {
        lock (_gate)
        {
            if (_bound && ReferenceEquals(sender, _session))
            {
                TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(chunk));
            }
        }
    }
}
