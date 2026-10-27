using System.Text.Json;

namespace OverShell.Config;

/// <summary>
/// Reads Windows Terminal's <c>settings.json</c>, including <c>profiles.defaults</c>
/// inheritance. Dynamic profiles come out with a <see cref="TerminalProfile.Source"/>
/// but usually no commandline — <see cref="ProfileCatalog"/> resolves those.
/// </summary>
public sealed class WindowsTerminalSettings
{
    private WindowsTerminalSettings(
        string path,
        IReadOnlyList<TerminalProfile> profiles,
        IReadOnlyList<ColorScheme> schemes,
        string? defaultProfileId,
        bool copyOnSelect)
    {
        Path = path;
        Profiles = profiles;
        Schemes = schemes;
        DefaultProfileId = defaultProfileId;
        CopyOnSelect = copyOnSelect;
    }

    public string Path { get; }
    public IReadOnlyList<TerminalProfile> Profiles { get; }
    public IReadOnlyList<ColorScheme> Schemes { get; }
    /// <summary>A normalized GUID or a profile name, both accepted by Windows Terminal.</summary>
    public string? DefaultProfileId { get; }
    public bool CopyOnSelect { get; }

    /// <summary>Candidate settings.json locations, most preferred first.</summary>
    public static IEnumerable<string> CandidatePaths()
    {
        // An explicit file first: a portable Windows Terminal keeps its settings elsewhere,
        // and the self-tests need profiles the machine does not have (an agent profile).
        var explicitPath = Environment.GetEnvironmentVariable("OVERSHELL_WT_SETTINGS");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath.Trim();
        }

        foreach (var candidate in ProfileSourceLocation.ChannelPaths(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            yield return candidate.Path!;
    }

    public static string? Locate() => ProfileSourceLocation.Resolve(new(), Environment.CurrentDirectory).Path;

    public static WindowsTerminalSettings? Load(string? path = null)
    {
        path ??= Locate();
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path), Json.DocumentOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Windows Terminal settings must be a JSON object");

        var profilesNode = root.Prop("profiles");
        if (profilesNode is { } profilesValue && profilesValue.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new JsonException("profiles must be an object or an array");
        if (profilesNode?.Prop("list") is { ValueKind: not JsonValueKind.Array })
            throw new JsonException("profiles.list must be an array");
        if (profilesNode?.Prop("defaults") is { ValueKind: not JsonValueKind.Object })
            throw new JsonException("profiles.defaults must be an object");
        var defaults = profilesNode?.Prop("defaults") ?? default;

        // "profiles" may be an object with a "list", or (legacy) a bare array.
        var list = profilesNode is { ValueKind: JsonValueKind.Array } bare
            ? bare.EnumerateArray()
            : profilesNode?.Array("list") ?? [];

        var entries = list.ToArray();
        if (entries.Any(p => p.ValueKind != JsonValueKind.Object))
            throw new JsonException("profiles.list entries must be objects");
        var profiles = entries
            .Select(p => ReadProfile(p, defaults))
            .ToList();

        var schemes = root.Array("schemes")
            .Select(ReadScheme)
            .OfType<ColorScheme>()
            .ToList();

        return new WindowsTerminalSettings(
            path,
            profiles,
            schemes,
            Json.NormalizeGuid(root.Str("defaultProfile")) ?? root.Str("defaultProfile")?.Trim(),
            root.Bool("copyOnSelect") ?? false);
    }

    private static TerminalProfile ReadProfile(JsonElement element, JsonElement defaults)
    {
        // A profile inherits anything it doesn't set from profiles.defaults.
        string? Inherited(string name) => element.Str(name) ?? defaults.Str(name);
        double? InheritedNum(string name) => element.Num(name) ?? defaults.Num(name);

        var fontNode = element.Prop("font");
        var defaultFontNode = defaults.Prop("font");

        var fontFace = fontNode?.Str("face") ?? defaultFontNode?.Str("face") ?? Inherited("fontFace");
        var fontSize = fontNode?.Num("size") ?? defaultFontNode?.Num("size") ?? InheritedNum("fontSize");

        var name = Inherited("name") ?? "Unnamed";
        var source = Inherited("source");

        return new TerminalProfile
        {
            Id = Json.NormalizeGuid(element.Str("guid")) ?? $"name:{source}/{name}",
            Name = name,
            Source = source,
            CommandLine = Inherited("commandline"),
            StartingDirectory = Inherited("startingDirectory"),
            Icon = Inherited("icon"),
            ColorSchemeName = Inherited("colorScheme"),
            FontFace = fontFace,
            FontSize = fontSize,
            CursorShape = ParseCursorShape(Inherited("cursorShape")),
            Hidden = element.Bool("hidden") ?? defaults.Bool("hidden") ?? false,
        };
    }

    private static ColorScheme? ReadScheme(JsonElement element)
    {
        var name = element.Str("name");
        if (name is null)
        {
            return null;
        }

        string[] keys =
        [
            "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow",
            "brightBlue", "brightPurple", "brightCyan", "brightWhite",
        ];

        var palette = keys.Select(k => element.Str(k) ?? "#000000").ToArray();

        return new ColorScheme
        {
            Name = name,
            Foreground = element.Str("foreground") ?? "#CCCCCC",
            Background = element.Str("background") ?? "#0C0C0C",
            CursorColor = element.Str("cursorColor"),
            SelectionBackground = element.Str("selectionBackground"),
            Palette = palette,
        };
    }

    internal static TerminalCursorShape? ParseCursorShape(string? raw) => raw?.ToLowerInvariant() switch
    {
        "bar" => TerminalCursorShape.Bar,
        "vintage" => TerminalCursorShape.Vintage,
        "underscore" => TerminalCursorShape.Underscore,
        "filledbox" => TerminalCursorShape.FilledBox,
        "emptybox" => TerminalCursorShape.EmptyBox,
        "doubleunderscore" => TerminalCursorShape.DoubleUnderscore,
        _ => null,
    };
}
