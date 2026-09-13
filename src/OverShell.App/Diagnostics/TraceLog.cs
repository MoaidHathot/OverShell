namespace OverShell.App.Diagnostics;

/// <summary>
/// Opt-in append-only trace files under <c>%TEMP%</c>, one per subsystem, enabled by an
/// environment variable. The evidence channel for verification runs that inject no
/// input (DESIGN.md §7.8): launch, let the software act, read the log.
/// </summary>
internal sealed class TraceLog
{
    /// <summary><c>OVERSHELL_TRACE_AGENTS=1</c> → <c>%TEMP%\overshell-agents.log</c>: detection, state transitions, endpoint traffic, notifications.</summary>
    public static readonly TraceLog Agents = new("OVERSHELL_TRACE_AGENTS", "overshell-agents.log");

    private readonly string _path;

    private TraceLog(string variable, string fileName)
    {
        Enabled = Environment.GetEnvironmentVariable(variable) == "1";
        _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
    }

    public bool Enabled { get; }

    public void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Tracing must never break the thing it observes.
        }
    }
}
