namespace OverShell.Core.Settings;

public sealed class TerminalSettings
{
    public int FontSizeStep { get; init; } = 1;
    public int EffectiveFontSizeStep => Math.Clamp(FontSizeStep, 1, 16);
}
