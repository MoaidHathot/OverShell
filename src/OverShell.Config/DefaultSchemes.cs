using System.Reflection;
using System.Text.Json;

namespace OverShell.Config;

/// <summary>
/// Windows Terminal's built-in colour schemes, lifted verbatim from
/// <c>TerminalSettingsModel/defaults.json</c>. Users only get a <c>schemes</c> array in
/// their own settings.json for schemes they added themselves, so the built-ins have to
/// ship with us or most profiles would have nothing to render with.
/// </summary>
public static class DefaultSchemes
{
    private static readonly Lazy<IReadOnlyDictionary<string, ColorScheme>> Cache = new(Load);

    public static IReadOnlyDictionary<string, ColorScheme> All => Cache.Value;

    public const string FallbackName = "Campbell";

    public static ColorScheme Fallback =>
        All.TryGetValue(FallbackName, out var scheme) ? scheme : Hardcoded;

    private static IReadOnlyDictionary<string, ColorScheme> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("DefaultSchemes.json", StringComparison.Ordinal));

        if (resource is null)
        {
            return new Dictionary<string, ColorScheme>(StringComparer.OrdinalIgnoreCase)
            {
                [Hardcoded.Name] = Hardcoded,
            };
        }

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream, Json.DocumentOptions);

        string[] keys =
        [
            "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow",
            "brightBlue", "brightPurple", "brightCyan", "brightWhite",
        ];

        var map = new Dictionary<string, ColorScheme>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = element.Str("name");
            if (name is null)
            {
                continue;
            }

            map[name] = new ColorScheme
            {
                Name = name,
                Foreground = element.Str("foreground") ?? "#CCCCCC",
                Background = element.Str("background") ?? "#0C0C0C",
                CursorColor = element.Str("cursorColor"),
                SelectionBackground = element.Str("selectionBackground"),
                Palette = keys.Select(k => element.Str(k) ?? "#000000").ToArray(),
            };
        }

        return map;
    }

    /// <summary>Last-resort scheme if the embedded resource is somehow unavailable.</summary>
    private static readonly ColorScheme Hardcoded = new()
    {
        Name = "Campbell",
        Foreground = "#CCCCCC",
        Background = "#0C0C0C",
        CursorColor = "#FFFFFF",
        Palette =
        [
            "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
            "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2",
        ],
    };
}
