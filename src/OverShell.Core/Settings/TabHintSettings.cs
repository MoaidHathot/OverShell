namespace OverShell.Core.Settings;

/// <summary>First-party extension settings, under extensions["tabs.hints"].</summary>
public sealed class TabHintSettings
{
    public string Display { get; init; } = "always";

    /// <summary>Optional generated shortcuts, e.g. "alt". Null leaves only the pick mode and existing Alt+1..9.</summary>
    public string? DirectModifiers { get; init; }

    public int TimeoutMs { get; init; } = 10000;

    public bool AlwaysVisible => !string.Equals(Display, "onDemand", StringComparison.OrdinalIgnoreCase);
}
