using System.Text.Json.Nodes;

namespace OverShell.Core.Layout;

/// <summary>Where the tab list lives. <c>Top</c> shares the caption bar with the window buttons.</summary>
public enum TabsPlacement
{
    Top,
    Bottom,
    Left,
    Right,
    Hidden,
}

/// <summary>How tabs are drawn. A strip is horizontal two-line items; a list is the same vertically; a rail is dots and glyphs only.</summary>
public enum TabsStyle
{
    Strip,
    List,
    Rail,
}

public enum SidePlacement
{
    Hidden,
    Left,
    Right,
}

public sealed class TabsRegion
{
    public TabsPlacement Placement { get; init; } = TabsPlacement.Top;

    /// <summary>Null picks the natural style for the placement: strip at top/bottom, list at the sides.</summary>
    public TabsStyle? Style { get; init; }

    /// <summary>Width of a left/right tab list in DIPs; the rail ignores it.</summary>
    public double Width { get; init; } = 240;

    public TabsStyle EffectiveStyle => Style ?? (Placement is TabsPlacement.Left or TabsPlacement.Right ? TabsStyle.List : TabsStyle.Strip);
}

public sealed class SidebarRegion
{
    public SidePlacement Placement { get; init; } = SidePlacement.Hidden;

    public double Width { get; init; } = 320;
}

public sealed class StatusRegion
{
    public bool Visible { get; init; } = true;
}

/// <summary>
/// One arrangement of the chrome around the terminal (DESIGN.md §12.5): where the tabs
/// go and how they look, whether the Herd sidebar shows and on which side, whether the
/// status bar shows. The terminal itself always keeps the middle, so switching layouts
/// never re-parents the native window — its neighbours change, it only resizes.
/// </summary>
public sealed class ChromeLayout
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public TabsRegion Tabs { get; init; } = new();

    public SidebarRegion Sidebar { get; init; } = new();

    public StatusRegion Status { get; init; } = new();

    /// <summary>True when the caption bar carries the tab strip and therefore needs its taller form.</summary>
    public bool TabsInCaption => Tabs.Placement == TabsPlacement.Top;
}

/// <summary>
/// Bundled presets plus every <c>*.jsonc</c> in the user's <c>layouts\</c> directory,
/// same-name files replacing presets wholesale — the agents-directory convention.
/// </summary>
public sealed class LayoutCatalog
{
    public const string DefaultName = "top";

    private readonly Dictionary<string, ChromeLayout> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];

    public IReadOnlyCollection<ChromeLayout> All => _byName.Values;

    public IReadOnlyList<string> Problems => _problems;

    public ChromeLayout? Find(string? name) => name is null ? null : _byName.GetValueOrDefault(name);

    /// <summary>The named layout, else the default preset — a misspelt name never leaves the window without chrome.</summary>
    public ChromeLayout Resolve(string? name) => Find(name) ?? Find(DefaultName) ?? new ChromeLayout { Name = DefaultName };

    public static LayoutCatalog LoadDefaults() => Load(null);

    public static LayoutCatalog Load(string? userDirectory)
    {
        var catalog = new LayoutCatalog();

        foreach (var resource in EmbeddedResources.List("layouts"))
        {
            catalog.Add(EmbeddedResources.Read(resource), resource);
        }

        if (userDirectory is not null && Directory.Exists(userDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(userDirectory, "*.jsonc").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    catalog.Add(File.ReadAllText(file), file);
                }
                catch (IOException e)
                {
                    catalog._problems.Add($"{file}: {e.Message}");
                }
            }
        }

        return catalog;
    }

    private void Add(string text, string sourceName)
    {
        var node = Jsonc.Parse(text, out var parseError);
        if (node is null)
        {
            _problems.Add($"{sourceName}: {parseError ?? "empty"}");
            return;
        }

        if (node is JsonObject obj && obj["name"] is null)
        {
            obj["name"] = Path.GetFileNameWithoutExtension(sourceName);
        }

        var layout = Jsonc.To<ChromeLayout>(node, out var shapeError);
        if (layout is null)
        {
            _problems.Add($"{sourceName}: {shapeError ?? "not an object"}");
            return;
        }

        _byName[layout.Name] = layout;
    }
}
