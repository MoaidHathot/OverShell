namespace OverShell.Core;

/// <summary>Where a root came from, so <c>OverShell settings path</c> can say why.</summary>
public enum PathSource
{
    /// <summary><c>OVERSHELL_CONFIG_DIR</c> / <c>OVERSHELL_STATE_DIR</c>.</summary>
    Override,

    /// <summary><c>XDG_CONFIG_HOME</c> / <c>XDG_STATE_HOME</c>, plus <c>overshell</c>.</summary>
    Xdg,

    /// <summary><c>%APPDATA%\OverShell</c> / <c>%LOCALAPPDATA%\OverShell</c>.</summary>
    Windows,
}

/// <summary>A resolved root and the reason it was chosen.</summary>
public sealed record ResolvedRoot(string Path, PathSource Source, string Variable);

/// <summary>
/// Where OverShell keeps its own files. Portable configuration in one root, machine-local
/// state in another — the split Windows Terminal and Palantir use, so a synced profile
/// carries settings but not caches. Each root is the first of:
/// <list type="number">
/// <item><c>OVERSHELL_CONFIG_DIR</c> / <c>OVERSHELL_STATE_DIR</c> — an explicit override (tests, portable use);</item>
/// <item><c>$XDG_CONFIG_HOME/overshell</c> / <c>$XDG_STATE_HOME/overshell</c> — for people who keep their
/// dotfiles in one place and point every tool at it, the way OpenCode, Neovim and git do on Windows;</item>
/// <item><c>%APPDATA%\OverShell</c> / <c>%LOCALAPPDATA%\OverShell</c> — the Windows default.</item>
/// </list>
/// Relative XDG values are ignored, as the specification says they should be.
/// </summary>
public static class AppPaths
{
    public static ResolvedRoot Config { get; } = ResolveConfigRoot(Environment.GetEnvironmentVariable);

    public static ResolvedRoot State { get; } = ResolveStateRoot(Environment.GetEnvironmentVariable);

    public static string ConfigRoot => Config.Path;

    public static string StateRoot => State.Path;

    public static string SettingsFile => Path.Combine(ConfigRoot, "settings.jsonc");

    public static string KeybindingsFile => Path.Combine(ConfigRoot, "keybindings.jsonc");

    public static string SnippetsFile => Path.Combine(ConfigRoot, "snippets.jsonc");

    public static string AgentsDir => Path.Combine(ConfigRoot, "agents");

    public static string LayoutsDir => Path.Combine(ConfigRoot, "layouts");

    public static string SkinsDir => Path.Combine(ConfigRoot, "skins");

    public static string StateFile => Path.Combine(StateRoot, "state.json");

    /// <summary>The tabs and view of the last run (§12.11); machine-local, never roams.</summary>
    public static string SessionFile => Path.Combine(StateRoot, "session.json");

    /// <summary>
    /// Creates the two roots if they do not exist yet. Only the roots: the sub-folders
    /// (<c>agents\</c>, <c>layouts\</c>, <c>skins\</c>) are optional and are made when
    /// something is put in them — an empty trio in someone's dotfiles repository is noise.
    /// </summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(StateRoot);
    }

    public static ResolvedRoot ResolveConfigRoot(Func<string, string?> env) =>
        Resolve(env, "OVERSHELL_CONFIG_DIR", "XDG_CONFIG_HOME", Environment.SpecialFolder.ApplicationData);

    public static ResolvedRoot ResolveStateRoot(Func<string, string?> env) =>
        Resolve(env, "OVERSHELL_STATE_DIR", "XDG_STATE_HOME", Environment.SpecialFolder.LocalApplicationData);

    private static ResolvedRoot Resolve(Func<string, string?> env, string overrideVariable, string xdgVariable, Environment.SpecialFolder fallback)
    {
        var overridden = env(overrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new ResolvedRoot(Path.GetFullPath(overridden.Trim()), PathSource.Override, overrideVariable);
        }

        var xdg = env(xdgVariable);
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg.Trim()))
        {
            // GetFullPath also straightens forward slashes and "..", which such values often carry.
            return new ResolvedRoot(Path.Combine(Path.GetFullPath(xdg.Trim()), "overshell"), PathSource.Xdg, xdgVariable);
        }

        return new ResolvedRoot(Path.Combine(Environment.GetFolderPath(fallback), "OverShell"), PathSource.Windows, fallback == Environment.SpecialFolder.ApplicationData ? "APPDATA" : "LOCALAPPDATA");
    }
}
