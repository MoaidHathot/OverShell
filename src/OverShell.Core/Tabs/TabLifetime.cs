namespace OverShell.Core.Tabs;

public enum TabExitBehavior { Keep, CloseOnSuccess, CloseAlways }

/// <summary>Retention is independent of presentation and of temporary addresses.</summary>
public sealed record TabLifetime(bool Scratch = false, bool Restore = true, bool History = true, TabExitBehavior OnExit = TabExitBehavior.Keep)
{
    public static readonly TabLifetime Standard = new();
    public bool ShouldClose(int? exitCode) => OnExit == TabExitBehavior.CloseAlways || OnExit == TabExitBehavior.CloseOnSuccess && exitCode == 0;
}
