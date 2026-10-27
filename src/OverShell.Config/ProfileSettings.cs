namespace OverShell.Config;

/// <summary>OverShell's profile import and default, stored under <c>profiles</c> in settings.jsonc.</summary>
public sealed record ProfileSettings
{
    /// <summary><c>auto</c> (Stable, Preview, unpackaged), or one explicit channel.</summary>
    public string Source { get; init; } = "auto";

    /// <summary>A file overriding Source. Relative paths are based on OverShell's configuration directory.</summary>
    public string? SettingsPath { get; init; }

    /// <summary>A profile name or id; null follows the imported Windows Terminal default.</summary>
    public string? DefaultProfile { get; init; }
}
