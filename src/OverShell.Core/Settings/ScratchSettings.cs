using OverShell.Core.Tabs;

namespace OverShell.Core.Settings;

public sealed class ScratchSettings
{
    public string? Profile { get; init; }
    public bool InheritDirectory { get; init; } = true;
    public bool Restore { get; init; }
    public bool History { get; init; }
    public string OnExit { get; init; } = "keep";
    public string CommandShell { get; init; } = "powershell.exe";
    public string[] CommandArguments { get; init; } = ["-NoLogo", "-NoProfile", "-Command"];
    public TabExitBehavior EffectiveExit => Enum.TryParse<TabExitBehavior>(OnExit, true, out var value) && Enum.IsDefined(value) ? value : TabExitBehavior.Keep;
}
