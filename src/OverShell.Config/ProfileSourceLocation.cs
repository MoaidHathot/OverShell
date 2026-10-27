namespace OverShell.Config;

/// <summary>The selected file, how it was selected, and a source-selection error if it cannot be used.</summary>
public sealed record ProfileSourceLocation(string Source, string? Path, string Origin, string? Error = null)
{
    public static ProfileSourceLocation Resolve(ProfileSettings settings, string configDirectory) =>
        Resolve(settings, configDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("OVERSHELL_WT_SETTINGS"));

    // Explicit dependencies keep discovery tests independent of the machine and its environment.
    internal static ProfileSourceLocation Resolve(ProfileSettings settings, string configDirectory, string localAppData, string? environmentPath)
    {
        var explicitPath = !string.IsNullOrWhiteSpace(environmentPath) ? environmentPath : settings.SettingsPath;
        var origin = !string.IsNullOrWhiteSpace(environmentPath) ? "OVERSHELL_WT_SETTINGS" : "profiles.settingsPath";
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(explicitPath.Trim());
                if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                    path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length == 1 ? "" : path[2..]);
                path = System.IO.Path.GetFullPath(path, configDirectory);
                return FileLocation("file", path, origin);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.IOException)
            {
                return new("file", null, origin, $"{origin}: invalid settings path '{explicitPath}': {e.Message}");
            }
        }

        var source = settings.Source?.Trim().ToLowerInvariant() ?? "auto";
        var candidates = ChannelPaths(localAppData).ToArray();
        if (source == "auto")
        {
            var found = candidates.FirstOrDefault(candidate => File.Exists(candidate.Path));
            return found is null ? new("auto", null, "auto discovery") : found;
        }

        var selected = candidates.FirstOrDefault(candidate => candidate.Source == source);
        return selected is null
            ? new(source, null, "profiles.source", $"profiles.source: unknown value '{settings.Source}'; expected auto, stable, preview or unpackaged")
            : FileLocation(selected.Source, selected.Path!, "profiles.source");
    }

    internal static IEnumerable<ProfileSourceLocation> ChannelPaths(string localAppData)
    {
        yield return new("stable", System.IO.Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"), "auto discovery");
        yield return new("preview", System.IO.Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"), "auto discovery");
        yield return new("unpackaged", System.IO.Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json"), "auto discovery");
    }

    private static ProfileSourceLocation FileLocation(string source, string path, string origin) =>
        new(source, path, origin, File.Exists(path) ? null : $"{origin}: settings file not found: {path}");
}
