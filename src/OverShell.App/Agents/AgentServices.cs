using OverShell.App.Diagnostics;
using OverShell.Core.Agents;
using OverShell.Core.Settings;

namespace OverShell.App.Agents;

/// <summary>
/// What every tab needs to detect and track the agent inside it. One instance per
/// window; tabs share the rules, the tuning and the screen reader. Rules and tuning are
/// swapped in place on a configuration reload; tabs read them on their next heartbeat.
/// </summary>
internal sealed class AgentServices
{
    public required AgentRules Rules { get; set; }

    public required DetectionSettings Detection { get; set; }

    public ScreenReader Screen { get; } = new();

    /// <summary>Environment a tab's child process gets so integrations can find the endpoint; null when there is none.</summary>
    public Func<string, IReadOnlyDictionary<string, string?>>? EnvironmentFor { get; init; }

    public TraceLog Trace => TraceLog.Agents;
}
