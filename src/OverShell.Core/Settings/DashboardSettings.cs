using System.Text.Json.Serialization;

namespace OverShell.Core.Settings;

public sealed record DashboardSettings
{
    /// <summary>auto: live where supported, otherwise styled; styled: cell drawings; text: legacy rows.</summary>
    public string PreviewMode { get; init; } = "auto";
    public int SnapshotIntervalMs { get; init; } = 500;
    public bool HoverPreview { get; init; } = true;
    public int HoverDelayMs { get; init; } = 500;

    [JsonIgnore]
    public string EffectiveMode => PreviewMode?.ToLowerInvariant() is "styled" or "text" ? PreviewMode.ToLowerInvariant() : "auto";
    [JsonIgnore]
    public int EffectiveInterval => Math.Clamp(SnapshotIntervalMs, 250, 10000);
    [JsonIgnore]
    public int EffectiveHoverDelay => Math.Clamp(HoverDelayMs, 150, 3000);
}
