using OverShell.App.Diagnostics;
using OverShell.Core.Agents;
using OverShell.Core.Settings;

namespace OverShell.App.Agents;

/// <summary>
/// What every tab needs to detect and track the agent inside it. One instance per
/// window; tabs share the rules, the tuning and the screen reader.
/// </summary>
internal sealed class AgentServices
{
    public required AgentRules Rules { get; init; }

    public required DetectionSettings Detection { get; init; }

    public ScreenReader Screen { get; } = new();

    /// <summary>Environment a tab's child process gets so integrations can find the endpoint; null when there is none.</summary>
    public Func<string, IReadOnlyDictionary<string, string?>>? EnvironmentFor { get; init; }

    public TraceLog Trace => TraceLog.Agents;
}
