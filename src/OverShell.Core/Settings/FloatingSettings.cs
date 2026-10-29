namespace OverShell.Core.Settings;

public sealed class FloatingSettings
{
    public double WidthRatio { get; init; } = 0.8;
    public double HeightRatio { get; init; } = 0.75;
    public string Position { get; init; } = "center";
    public bool HideOnFocusLoss { get; init; }
    public double EffectiveWidth => double.IsFinite(WidthRatio) ? Math.Clamp(WidthRatio, 0.25, 1) : 0.8;
    public double EffectiveHeight => double.IsFinite(HeightRatio) ? Math.Clamp(HeightRatio, 0.25, 1) : 0.75;
}
