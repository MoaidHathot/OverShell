using System.Text.Json;

namespace OverShell.Config;

/// <summary>
/// Reads Windows Terminal fragment extensions — the mechanism third-party installers
/// (Git, Visual Studio's debug console, …) use to contribute profiles. Unlike the
/// built-in generators these carry a full commandline, so they need no re-implementation.
/// </summary>
public static class FragmentLoader
{
    public static IEnumerable<string> FragmentDirectories()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows Terminal", "Fragments");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows Terminal", "Fragments");
    }

    public static IReadOnlyList<TerminalProfile> Load(IList<string>? diagnostics = null)
    {
        var results = new List<TerminalProfile>();

        foreach (var directory in FragmentDirectories().Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                // The fragment's containing folder is the "source" Windows Terminal
                // stamps onto the profiles it contributes.
                var source = Path.GetFileName(Path.GetDirectoryName(file)) ?? "fragment";

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file), Json.DocumentOptions);

                    foreach (var element in document.RootElement.Array("profiles"))
                    {
                        var name = element.Str("name");
                        if (name is null)
                        {
                            continue;
                        }

                        results.Add(new TerminalProfile
                        {
                            Id = Json.NormalizeGuid(element.Str("guid")) ?? $"name:{source}/{name}",
                            Name = name,
                            Source = source,
                            CommandLine = element.Str("commandline"),
                            StartingDirectory = element.Str("startingDirectory"),
                            Icon = element.Str("icon"),
                            ColorSchemeName = element.Str("colorScheme"),
                            CursorShape = WindowsTerminalSettings.ParseCursorShape(element.Str("cursorShape")),
                        });
                    }
                }
                catch (Exception ex)
                {
                    diagnostics?.Add($"fragment '{file}' skipped: {ex.Message}");
                }
            }
        }

        return results;
    }
}
