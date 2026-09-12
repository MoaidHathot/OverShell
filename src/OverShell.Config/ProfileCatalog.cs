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
        string? settingsPath,
        bool copyOnSelect,
        IReadOnlyList<string> diagnostics)
    {
        Profiles = profiles;
        Schemes = schemes;
        DefaultProfile = defaultProfile;
        SettingsPath = settingsPath;
        CopyOnSelect = copyOnSelect;
        Diagnostics = diagnostics;
    }

    /// <summary>Launchable, non-hidden profiles, in settings.json order.</summary>
    public IReadOnlyList<TerminalProfile> Profiles { get; }

    public IReadOnlyDictionary<string, ColorScheme> Schemes { get; }
    public TerminalProfile? DefaultProfile { get; }
    public string? SettingsPath { get; }
    public bool CopyOnSelect { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public ColorScheme SchemeFor(TerminalProfile profile) =>
        profile.ColorSchemeName is { } name && Schemes.TryGetValue(name, out var scheme)
            ? scheme
            : DefaultSchemes.Fallback;

    public TerminalProfile? FindById(string? id) =>
        id is null ? null : Profiles.FirstOrDefault(p => p.Id == id);

    public static ProfileCatalog Load()
    {
        var diagnostics = new List<string>();

        var settings = TryLoadSettings(diagnostics);

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

        var merged = Merge(settings, resolvers, diagnostics);

        var schemes = new Dictionary<string, ColorScheme>(DefaultSchemes.All, StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in settings?.Schemes ?? [])
        {
            schemes[scheme.Name] = scheme;   // user schemes override built-ins
        }

        var visible = merged.Where(p => !p.Hidden && p.IsLaunchable).ToList();

        if (visible.Count == 0)
        {
            diagnostics.Add("no launchable profiles resolved — falling back to PowerShell");
            visible.Add(FallbackProfile());
        }

        var defaultProfile =
            visible.FirstOrDefault(p => p.Id == settings?.DefaultProfileId)
            ?? visible.FirstOrDefault(p => p.Source == PowerShellCoreGenerator.SourceId)
            ?? visible[0];

        return new ProfileCatalog(
            visible, schemes, defaultProfile, settings?.Path, settings?.CopyOnSelect ?? false, diagnostics);
    }

    private static WindowsTerminalSettings? TryLoadSettings(List<string> diagnostics)
    {
        try
        {
            var settings = WindowsTerminalSettings.Load();
            if (settings is null)
            {
                diagnostics.Add("settings.json not found — using generated profiles only");
            }

            return settings;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"settings.json could not be parsed: {ex.Message}");
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
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var byKey = resolvers
            .GroupBy(r => Key(r.Source, r.Name))
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
