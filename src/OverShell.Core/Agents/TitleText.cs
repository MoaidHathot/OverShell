using System.Text.RegularExpressions;

namespace OverShell.Core.Agents;

/// <summary>
/// Titles as shown to the user. Agents put state icons at the front of the OSC title —
/// OpenCode's tab-status plugin writes <c>🔔 OC | repo | session</c>, <c>❓ …</c>, <c>❌ …</c>;
/// Claude Code spins <c>✳ ✻ ✽</c> — which the detector reads (<c>title.blocked</c> rules) and
/// the state dot already shows. In the window caption, the tooltip and the status bar the
/// icon is noise, and WPF renders the emoji in black and white, so it is stripped there.
/// The raw title stays what detection sees.
/// </summary>
public static partial class TitleText
{
    // A leading run of symbols (emoji are Other Symbol or surrogate pairs), their variation
    // selectors and joiners, and the separators agents put after them.
    [GeneratedRegex(@"^[\p{So}\p{Cs}\p{Mn}\p{Cf}\p{Sk}\s·•|\-–—:]+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingSymbols { get; }

    /// <summary>
    /// <paramref name="title"/> without its leading state icons; the title itself when
    /// nothing readable would be left (a title that is only an icon stays an icon).
    /// </summary>
    public static string ForDisplay(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var stripped = LeadingSymbols.Replace(title, string.Empty);
        return stripped.Length == 0 ? title : stripped;
    }
}
