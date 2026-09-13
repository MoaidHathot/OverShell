namespace OverShell.App.Terminal;

/// <summary>What kind of thing runs inside a session. Drives which tab template the chrome uses.</summary>
public enum SessionKind
{
    /// <summary>An interactive shell — the ordinary case.</summary>
    Shell,

    /// <summary>A long-running agent (Copilot CLI, OpenCode, Claude Code, …) that OverShell manages.</summary>
    Agent,
}

/// <summary>
/// Everything needed to (re)create a session. Deliberately a plain record: it is what a
/// layout file persists, and what a restart reuses.
/// </summary>
public sealed record SessionDescriptor
{
    /// <summary>Full command line, already environment-expanded.</summary>
    public required string CommandLine { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment overrides applied on top of OverShell's own environment. A null value
    /// removes the variable from the child.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Windows Terminal profile this came from, if any.</summary>
    public string? ProfileId { get; init; }

    public SessionKind Kind { get; init; } = SessionKind.Shell;
}
