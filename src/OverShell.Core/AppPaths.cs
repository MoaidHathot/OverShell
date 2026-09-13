namespace OverShell.Core;

/// <summary>
/// Where OverShell keeps its own files. Portable configuration under <c>%APPDATA%</c>,
/// machine-local state under <c>%LOCALAPPDATA%</c> — the same split Windows Terminal and
/// Palantir use, so a synced roaming profile carries settings but not caches.
/// <c>OVERSHELL_CONFIG_DIR</c> overrides the configuration root (tests, portable use).
/// </summary>
public static class AppPaths
{
    public static string ConfigRoot { get; } = ResolveConfigRoot();

    public static string StateRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverShell");

    public static string SettingsFile => Path.Combine(ConfigRoot, "settings.jsonc");

    public static string KeybindingsFile => Path.Combine(ConfigRoot, "keybindings.jsonc");

    public static string AgentsDir => Path.Combine(ConfigRoot, "agents");

    public static string LayoutsDir => Path.Combine(ConfigRoot, "layouts");

    public static string SkinsDir => Path.Combine(ConfigRoot, "skins");

    public static string StateFile => Path.Combine(StateRoot, "state.json");

    public static string LogsDir => Path.Combine(StateRoot, "logs");

    /// <summary>Creates the configuration and state directories if they do not exist yet.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(AgentsDir);
        Directory.CreateDirectory(LayoutsDir);
        Directory.CreateDirectory(SkinsDir);
        Directory.CreateDirectory(StateRoot);
        Directory.CreateDirectory(LogsDir);
    }

    private static string ResolveConfigRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("OVERSHELL_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverShell");
    }
}
