using OverShell.Config;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

public sealed class ProfileCatalogTests : IDisposable
{
    private const string First = "11111111-aaaa-4444-8888-111111111111";
    private const string Second = "22222222-bbbb-4444-8888-222222222222";
    private readonly string _root = Directory.CreateTempSubdirectory("overshell-profiles-").FullName;
    private string Config => Path.Combine(_root, "config");
    private string Local => Path.Combine(_root, "local");

    private string Write(string channel, string json)
    {
        var path = channel == "custom" ? Path.Combine(Config, "custom settings.json")
            : ProfileSourceLocation.ChannelPaths(Local).Single(c => c.Source == channel).Path!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private static string Settings(string name, string defaultProfile = First) => $$"""
        {
          // Comments, trailing commas and inherited appearance must survive source selection.
          "defaultProfile": "{{defaultProfile}}",
          "copyOnSelect": true,
          "profiles": {
            "defaults": { "font": { "face": "Test Font", "size": 14 }, "colorScheme": "Imported" },
            "list": [
              { "guid": "{{First}}", "name": "{{name}}", "commandline": "cmd.exe" },
              { "guid": "{{Second}}", "name": "Second", "commandline": "powershell.exe" },
            ]
          },
          "schemes": [ { "name": "Imported", "foreground": "#123456", "background": "#111111" } ]
        }
        """;

    private ProfileCatalog Load(ProfileSettings? options = null, string? environmentPath = null, IReadOnlyList<TerminalProfile>? generated = null) =>
        ProfileCatalog.Load(options ?? new(), Config, Local, environmentPath, generated ?? []);

    [Theory]
    [InlineData("auto", "Stable")]
    [InlineData("stable", "Stable")]
    [InlineData("preview", "Preview")]
    [InlineData(" PREVIEW ", "Preview")]
    [InlineData("unpackaged", "Unpackaged")]
    public void Channel_selection_imports_profiles_and_appearance_from_the_same_file(string source, string expected)
    {
        Write("stable", Settings("Stable"));
        Write("preview", Settings("Preview"));
        Write("unpackaged", Settings("Unpackaged"));
        var catalog = Load(new() { Source = source });
        Assert.Equal(expected, catalog.DefaultProfile!.Name);
        Assert.Equal("Test Font", catalog.DefaultProfile.FontFace);
        Assert.Equal(14, catalog.DefaultProfile.FontSize);
        Assert.Equal("#123456", catalog.SchemeFor(catalog.DefaultProfile).Foreground);
        Assert.True(catalog.CopyOnSelect);
        Assert.Null(catalog.SettingsError);
        Assert.Empty(catalog.Diagnostics);
    }

    [Fact]
    public void Auto_uses_preview_then_unpacked_when_earlier_channels_are_absent()
    {
        Write("unpackaged", Settings("Unpackaged"));
        Assert.Equal("unpackaged", Load().Location.Source);
        Write("preview", Settings("Preview"));
        Assert.Equal("preview", Load().Location.Source);
    }

    [Fact]
    public void Explicit_file_overrides_channel_and_environment_overrides_file()
    {
        var stable = Write("stable", Settings("Stable"));
        Write("preview", Settings("Preview"));
        var custom = Write("custom", Settings("Custom"));
        var options = new ProfileSettings { Source = "preview", SettingsPath = custom };
        Assert.Equal("Custom", Load(options).DefaultProfile!.Name);
        var overridden = Load(options, stable);
        Assert.Equal("Stable", overridden.DefaultProfile!.Name);
        Assert.Equal("OVERSHELL_WT_SETTINGS", overridden.Location.Origin);
    }

    [Fact]
    public void Relative_files_use_the_configuration_directory()
    {
        var custom = Write("custom", Settings("Custom"));
        var catalog = Load(new() { SettingsPath = "custom settings.json" });
        Assert.Equal(custom, catalog.SettingsPath);
        Assert.Equal("Custom", catalog.DefaultProfile!.Name);
    }

    [Fact]
    public void Explicit_paths_expand_environment_variables_and_home()
    {
        var path = ProfileSourceLocation.Resolve(new() { SettingsPath = "%USERPROFILE%\\not-there\\settings.json" }, Config, Local, null);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "not-there", "settings.json"), path.Path);
        var home = ProfileSourceLocation.Resolve(new() { SettingsPath = "~/not-there/settings.json" }, Config, Local, null);
        Assert.Equal(path.Path, home.Path);
    }

    [Theory]
    [InlineData("Second")]
    [InlineData("second")]
    [InlineData(Second)]
    [InlineData("{22222222-BBBB-4444-8888-222222222222}")]
    public void Windows_terminal_defaults_accept_names_and_normalized_guids(string defaultProfile)
    {
        Write("stable", Settings("First", defaultProfile));
        Assert.Equal(Second, Load().DefaultProfile!.Id);
    }

    [Fact]
    public void Overshell_default_overrides_imported_default_and_null_follows_it()
    {
        Write("stable", Settings("First", "Second"));
        Assert.Equal(First, Load(new() { DefaultProfile = "First" }).DefaultProfile!.Id);
        Assert.Equal(Second, Load(new() { DefaultProfile = null }).DefaultProfile!.Id);
        var invalid = Load(new() { DefaultProfile = "missing" });
        Assert.Equal(Second, invalid.DefaultProfile!.Id);
        Assert.Contains(invalid.Diagnostics, d => d.Contains("profiles.defaultProfile") && d.Contains("missing"));
    }

    [Fact]
    public void Explicit_profile_requests_do_not_fall_back_and_ids_win_over_names()
    {
        Write("stable", Settings(Second));
        var catalog = Load();
        Assert.Null(catalog.ResolveProfile("missing", out var error));
        Assert.Contains("unavailable", error);
        Assert.Equal(Second, catalog.ResolveProfile(" {22222222-BBBB-4444-8888-222222222222} ", out error)!.Id);
        Assert.Null(error);
        Assert.Equal(Second, catalog.ResolveProfile(Second, out _)!.Id);
        Assert.Equal(First, catalog.ResolveProfile(null, out _)!.Id);
        Assert.Equal(Second, catalog.FindById("{22222222-BBBB-4444-8888-222222222222}")!.Id);
    }

    [Fact]
    public void Ambiguous_names_require_an_id()
    {
        Write("stable", Settings("Second"));
        var catalog = Load();
        Assert.Null(catalog.ResolveProfile("Second", out var error));
        Assert.Contains("ambiguous", error);
        Assert.Equal(Second, catalog.ResolveProfile(Second, out _)!.Id);
    }

    [Theory]
    [InlineData("preview", null, null)]
    [InlineData("stable", "missing.json", null)]
    [InlineData("stable", null, "missing.json")]
    [InlineData("unknown", null, null)]
    [InlineData("stable", "bad\0path", null)]
    public void An_unavailable_explicit_source_is_reported_instead_of_importing_another_channel(string source, string? file, string? environment)
    {
        Write("stable", Settings("Stable"));
        var catalog = Load(new() { Source = source, SettingsPath = file }, environment);
        Assert.NotNull(catalog.SettingsError);
        Assert.NotNull(catalog.DefaultProfile);
        Assert.DoesNotContain(catalog.Profiles, p => p.Name == "Stable");
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("[]")]
    public void Malformed_source_is_a_load_error_with_a_usable_startup_fallback(string contents)
    {
        var file = Write("custom", contents);
        var catalog = Load(new() { SettingsPath = file });
        Assert.Contains(file, catalog.SettingsError);
        Assert.True(catalog.DefaultProfile!.IsLaunchable);
    }

    [Fact]
    public void Hidden_duplicate_ids_and_generator_keys_cannot_break_the_catalog()
    {
        Write("stable", $$"""
            { "profiles": [
              { "guid": "{{First}}", "name": "hidden", "hidden": true, "commandline": "cmd.exe" },
              { "guid": "{{First}}", "name": "duplicate", "commandline": "cmd.exe" },
              { "guid": "{{Second}}", "name": "Shell", "source": "test" }
            ] }
            """);
        var catalog = Load(generated: [
            new() { Id = "generated", Name = "Shell", Source = "TEST", CommandLine = "shell.exe" },
            new() { Id = "GENERATED", Name = "SHELL", Source = "test", CommandLine = "other.exe" }
        ]);
        Assert.Null(catalog.FindById(First));
        Assert.Equal("shell.exe", catalog.FindById(Second)!.CommandLine);
        Assert.Single(catalog.Profiles);
        Assert.Contains(catalog.Diagnostics, d => d.Contains("duplicate profile id"));
    }

    [Fact]
    public void User_settings_merge_profile_preferences_and_null_restores_imported_default()
    {
        var file = Write("custom", """{ "profiles": { "source": "preview", "defaultProfile": "Second" } }""");
        var settings = AppSettings.Load(file);
        Assert.Equal("preview", settings.Profiles.Source);
        Assert.Equal("Second", settings.Profiles.DefaultProfile);
        Assert.Null(settings.Profiles.SettingsPath);
        File.WriteAllText(file, """{ "profiles": { "defaultProfile": null } }""");
        Assert.Equal(new ProfileSettings(), AppSettings.Load(file).Profiles);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
