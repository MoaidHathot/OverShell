using OverShell.Config.Generators;

namespace OverShell.Config;

/// <summary>
/// The resolved view of the user's Windows Terminal configuration.
/// <para>
/// settings.json only stores a stub (guid + name + source) for dynamic profiles; the
/// commandline lives in C++ generators inside Windows Terminal. This class stitches the
/// stubs back together with fragment extensions and our re-implemented generators,
/// matching on <c>(source, name)</c> — which survives the fact that the built-in
/// generators use GUID schemes we deliberately don't reproduce.
/// </para>
/// </summary>
public sealed class ProfileCatalog
{
    private ProfileCatalog(
        IReadOnlyList<TerminalProfile> profiles,
        IReadOnlyDictionary<string, ColorScheme> schemes,
        TerminalProfile? defaultProfile,
        ProfileSourceLocation location,
        string? settingsError,
        bool copyOnSelect,
        IReadOnlyList<string> diagnostics)
    {
        Profiles = profiles;
        Schemes = schemes;
        DefaultProfile = defaultProfile;
        Location = location;
        SettingsError = settingsError;
        CopyOnSelect = copyOnSelect;
        Diagnostics = diagnostics;
    }

    /// <summary>Launchable, non-hidden profiles, in settings.json order.</summary>
    public IReadOnlyList<TerminalProfile> Profiles { get; }

    public IReadOnlyDictionary<string, ColorScheme> Schemes { get; }
    public TerminalProfile? DefaultProfile { get; }
    public ProfileSourceLocation Location { get; }
    public string? SettingsPath => Location.Path;
    /// <summary>A file/source error. A live reload retains the last good catalog when this is set.</summary>
    public string? SettingsError { get; }
    public bool CopyOnSelect { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public ColorScheme SchemeFor(TerminalProfile profile) =>
        profile.ColorSchemeName is { } name && Schemes.TryGetValue(name, out var scheme)
            ? scheme
            : DefaultSchemes.Fallback;

    public TerminalProfile? FindById(string? id) => id is null ? null :
        Profiles.FirstOrDefault(p => string.Equals(p.Id, NormalizeId(id), StringComparison.OrdinalIgnoreCase));

    /// <summary>Null/empty selects the default. An explicit name/id resolves exactly or explains why it cannot.</summary>
    public TerminalProfile? ResolveProfile(string? nameOrId, out string? error)
    {
        error = null;
        return string.IsNullOrWhiteSpace(nameOrId) ? DefaultProfile : Match(Profiles, nameOrId, out error);
    }

    private static string NormalizeId(string id) => Json.NormalizeGuid(id) ?? id.Trim();

    private static TerminalProfile? Match(IReadOnlyList<TerminalProfile> profiles, string nameOrId, out string? error)
    {
        error = null;
        var id = NormalizeId(nameOrId);
        var byId = profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var matches = profiles.Where(p => string.Equals(p.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length == 1) return matches[0];
        error = matches.Length == 0
            ? $"Profile '{nameOrId}' is unavailable in the selected source"
            : $"Profile name '{nameOrId}' is ambiguous; use its profile ID";
        return null;
    }

    public static ProfileCatalog Load(ProfileSettings? options = null, string? configDirectory = null) =>
        Load(options ?? new(), configDirectory ?? Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("OVERSHELL_WT_SETTINGS"));

    internal static ProfileCatalog Load(ProfileSettings options, string configDirectory, string localAppData, string? environmentPath,
        IReadOnlyList<TerminalProfile>? generatedProfiles = null)
    {
        var diagnostics = new List<string>();
        var location = ProfileSourceLocation.Resolve(options, configDirectory, localAppData, environmentPath);
        var settings = TryLoadSettings(location, out var settingsError);
        if (settingsError is not null) diagnostics.Add(settingsError);
        else if (settings is null) diagnostics.Add("settings.json not found — using generated profiles only");

        var resolvers = generatedProfiles?.ToList() ?? Generate(diagnostics);
        return Build(settings, resolvers, options, location, settingsError, diagnostics);
    }

    private static List<TerminalProfile> Generate(List<string> diagnostics)
    {
        // Everything that can supply a real commandline for a dynamic profile stub.
        var resolvers = new List<TerminalProfile>();
        resolvers.AddRange(FragmentLoader.Load(diagnostics));

        foreach (var generator in new IProfileGenerator[]
                 {
                     new PowerShellCoreGenerator(),
                     new VisualStudioGenerator(),
                 })
        {
            try
            {
                resolvers.AddRange(generator.Generate(diagnostics));
            }
            catch (Exception ex)
            {
                diagnostics.Add($"generator '{generator.Source}' failed: {ex.Message}");
            }
        }

        return resolvers;
    }

    private static ProfileCatalog Build(WindowsTerminalSettings? settings, List<TerminalProfile> resolvers, ProfileSettings options,
        ProfileSourceLocation location, string? settingsError, List<string> diagnostics)
    {
        var merged = Merge(settings, resolvers, diagnostics);

        var schemes = new Dictionary<string, ColorScheme>(DefaultSchemes.All, StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in settings?.Schemes ?? [])
        {
            schemes[scheme.Name] = scheme;   // user schemes override built-ins
        }

        // A duplicate id must not produce colliding commands or an arbitrary launch target.
        // Deduplicate before filtering so a hidden user entry cannot be resurrected by a resolver.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visible = new List<TerminalProfile>();
        foreach (var profile in merged)
        {
            if (!ids.Add(NormalizeId(profile.Id)))
            {
                diagnostics.Add($"duplicate profile id '{profile.Id}': keeping the first entry");
                continue;
            }
            if (!profile.Hidden && profile.IsLaunchable) visible.Add(profile with { Id = NormalizeId(profile.Id) });
        }

        if (visible.Count == 0)
        {
            diagnostics.Add("no launchable profiles resolved — falling back to PowerShell");
            visible.Add(FallbackProfile());
        }

        var defaultProfile = SelectDefault(options.DefaultProfile, "profiles.defaultProfile")
            ?? SelectDefault(settings?.DefaultProfileId, "Windows Terminal defaultProfile")
            ?? visible.FirstOrDefault(p => p.Source == PowerShellCoreGenerator.SourceId)
            ?? visible[0];

        return new ProfileCatalog(
            visible, schemes, defaultProfile, location, settingsError, settings?.CopyOnSelect ?? false, diagnostics);

        TerminalProfile? SelectDefault(string? value, string origin)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Match(visible, value, out var error);
            if (error is not null) diagnostics.Add($"{origin}: {error}; using the next available default");
            return match;
        }
    }

    private static WindowsTerminalSettings? TryLoadSettings(ProfileSourceLocation location, out string? error)
    {
        error = location.Error;
        if (error is not null || location.Path is null) return null;
        try
        {
            var settings = WindowsTerminalSettings.Load(location.Path);
            if (settings is null) error = $"settings.json not found: {location.Path}";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            error = $"{location.Path}: settings.json could not be loaded: {ex.Message}";
            return null;
        }
    }

    private static List<TerminalProfile> Merge(
        WindowsTerminalSettings? settings,
        List<TerminalProfile> resolvers,
        List<string> diagnostics)
    {
        var byId = resolvers
            .Where(r => !r.Id.StartsWith("name:", StringComparison.Ordinal))
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var byKey = resolvers
            .GroupBy(r => Key(r.Source, r.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var results = new List<TerminalProfile>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in settings?.Profiles ?? [])
        {
            if (entry.IsLaunchable || entry.Source is null)
            {
                results.Add(entry);          // static profile, already complete
                continue;
            }

            if (!byId.TryGetValue(entry.Id, out var resolver))
            {
                byKey.TryGetValue(Key(entry.Source, entry.Name), out resolver);
            }

            if (resolver is null)
            {
                diagnostics.Add($"unresolved dynamic profile: '{entry.Name}' (source {entry.Source})");
                continue;                    // e.g. Azure Cloud Shell, which we don't implement
            }

            consumed.Add(Key(resolver.Source, resolver.Name));

            // The user's settings.json wins wherever it actually specifies something.
            results.Add(entry with
            {
                CommandLine = resolver.CommandLine,
                StartingDirectory = entry.StartingDirectory ?? resolver.StartingDirectory,
                Icon = entry.Icon ?? resolver.Icon,
                ColorSchemeName = entry.ColorSchemeName ?? resolver.ColorSchemeName,
                CursorShape = entry.CursorShape ?? resolver.CursorShape,
            });
        }

        // Dynamic profiles that exist on this machine but aren't in settings.json yet.
        foreach (var resolver in resolvers)
        {
            if (resolver.IsLaunchable && consumed.Add(Key(resolver.Source, resolver.Name)))
            {
                var alreadyPresent = results.Any(r =>
                    string.Equals(r.Name, resolver.Name, StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent)
                {
                    results.Add(resolver);
                }
            }
        }

        return results;
    }

    private static string Key(string? source, string name) => $"{source}\u0000{name}";

    private static TerminalProfile FallbackProfile() => new()
    {
        Id = "fallback:powershell",
        Name = "Windows PowerShell",
        CommandLine = "powershell.exe",
        StartingDirectory = "%USERPROFILE%",
        ColorSchemeName = "Campbell",
    };
}
