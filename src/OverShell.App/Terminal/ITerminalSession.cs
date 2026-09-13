namespace OverShell.App.Terminal;

/// <summary>
/// A byte stream with a lifecycle: the process (or remote endpoint) behind a tab.
/// <para>
/// Knows nothing about pixels. The same session can be shown by any
/// <see cref="ITerminalSurface"/>, and — with the scrollback caveat in DESIGN.md §7.1 —
/// moved between surfaces while alive.
/// </para>
/// </summary>
public interface ITerminalSession : IDisposable
{
    SessionDescriptor Descriptor { get; }

    /// <summary>True once the child process exists. Starting happens off the UI thread.</summary>
    bool HasStarted { get; }

    /// <summary>
    /// True while the child is alive. Deliberately false before <see cref="HasStarted"/>,
    /// so callers must check both before concluding that a session died.
    /// </summary>
    bool IsRunning { get; }

    int? ExitCode { get; }

    /// <summary>The root process's id once it exists; the agent detector walks its descendants.</summary>
    int? ProcessId { get; }

    /// <summary>Raised on a background thread once the child process exists.</summary>
    event EventHandler? Started;

    /// <summary>Raised on a background thread, once, after the child exits and its output has drained.</summary>
    event EventHandler? Exited;

    /// <summary>
    /// Raised on the I/O thread for every chunk the child produces, in order. Observers
    /// must be cheap and must never block; marshal anything UI-facing.
    /// </summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>Starts the child with an initial grid size. Safe to call once.</summary>
    void Start(int columns, int rows);

    /// <summary>
    /// Writes to the child as if typed. Silently ignored once <see cref="CloseInput"/> has
    /// been called or the child has gone — it never throws for a dead session.
    /// </summary>
    void WriteInput(ReadOnlySpan<char> text);

    void Resize(int columns, int rows);

    /// <summary>
    /// Stops accepting input without ending the session. Called first on every teardown
    /// path, because the surface keeps delivering focus and key traffic while it unloads.
    /// </summary>
    void CloseInput();
}
