namespace OverShell.Config;

/// <summary>Cursor shapes, mirroring Windows Terminal's <c>cursorShape</c> setting.</summary>
public enum TerminalCursorShape
{
    Bar,
    Vintage,
    Underscore,
    FilledBox,
    EmptyBox,
    DoubleUnderscore,
}

/// <summary>
/// A colour scheme, in Windows Terminal's shape. <see cref="Palette"/> is the 16
/// ANSI colours in VT order: black, red, green, yellow, blue, purple, cyan, white,
/// then the eight bright variants.
/// </summary>
public sealed record ColorScheme
{
    public required string Name { get; init; }
    public required string Foreground { get; init; }
    public required string Background { get; init; }
    public string? CursorColor { get; init; }
    public string? SelectionBackground { get; init; }
    public required IReadOnlyList<string> Palette { get; init; }
}

/// <summary>
/// A launchable profile. Assembled from settings.json, fragment extensions and the
/// re-implemented dynamic profile generators.
/// </summary>
public sealed record TerminalProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Non-null for dynamic profiles, e.g. <c>Windows.Terminal.PowershellCore</c>.</summary>
    public string? Source { get; init; }

    public string? CommandLine { get; init; }
    public string? StartingDirectory { get; init; }
    public string? Icon { get; init; }
    public string? ColorSchemeName { get; init; }
    public string? FontFace { get; init; }
    public double? FontSize { get; init; }
    public TerminalCursorShape? CursorShape { get; init; }
    public bool Hidden { get; init; }

    /// <summary>False when a dynamic profile's generator could not find its target.</summary>
    public bool IsLaunchable => !string.IsNullOrWhiteSpace(CommandLine);

    public override string ToString() => $"{Name} [{Source ?? "static"}]";
}
