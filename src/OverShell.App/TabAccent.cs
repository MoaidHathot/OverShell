using System.Windows.Media;

namespace OverShell.App;

/// <summary>
/// Deterministic accent colour per profile, so a given profile always gets the same
/// dot colour across restarts and tabs are told apart at a glance.
/// </summary>
public static class TabAccent
{
    private static readonly string[] Palette =
    [
        "#4C8DFF", "#3FB950", "#D29922", "#DB6D28",
        "#A371F7", "#39C5CF", "#F778BA", "#7EE787",
    ];

    private static readonly Dictionary<string, Brush> Cache = [];

    public static Brush For(string key)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var brush = TerminalThemeMapper.ToBrush(Palette[StableIndex(key, Palette.Length)]);
            Cache[key] = brush;
            return brush;
        }
    }

    /// <summary>FNV-1a — stable across processes, unlike string.GetHashCode.</summary>
    private static int StableIndex(string key, int modulo)
    {
        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % (uint)modulo);
    }
}
