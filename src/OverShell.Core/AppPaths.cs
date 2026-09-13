namespace OverShell.Core;

/// <summary>
/// Where OverShell keeps its own files. Portable configuration under <c>%APPDATA%</c>,
/// machine-local state under <c>%LOCALAPPDATA%</c> - the same split Windows Terminal and
/// Palantir use, so a synced roaming profile carries settings but not caches.
/// <c>OVERSHELL_CONFIG_DIR</c> and <c>OVERSHELL_STATE_DIR</c> override the two roots
/// (tests, portable use).
/// </summary>
public static class AppPaths
{
    public static string ConfigRoot { get; } = ResolveRoot("OVERSHELL_CONFIG_DIR", Environment.SpecialFolder.ApplicationData);

    public static string StateRoot { get; } = ResolveRoot("OVERSHELL_STATE_DIR", Environment.SpecialFolder.LocalApplicationData);

    public static string SettingsFile => Path.Combine(ConfigRoot, "settings.jsonc");

    public static string KeybindingsFile => Path.Combine(ConfigRoot, "keybindings.jsonc");

    public static string AgentsDir => Path.Combine(ConfigRoot, "agents");

    public static string LayoutsDir => Path.Combine(ConfigRoot, "layouts");

    public static string SkinsDir => Path.Combine(ConfigRoot, "skins");

    public static string SnippetsFile => Path.Combine(ConfigRoot, "snippets.jsonc");

    public static string StateFile => Path.Combine(StateRoot, "state.json");

    /// <summary>The tabs and view of the last run (§12.11); machine-local, never roams.</summary>
    public static string SessionFile => Path.Combine(StateRoot, "session.json");

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

    private static string ResolveRoot(string variable, Environment.SpecialFolder fallback)
    {
        var overridden = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(Environment.GetFolderPath(fallback), "OverShell");
    }
}
