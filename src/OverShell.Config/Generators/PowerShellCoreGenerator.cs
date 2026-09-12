namespace OverShell.Config.Generators;

/// <summary>
/// Re-implementation of Windows Terminal's <c>Windows.Terminal.PowershellCore</c>
/// generator. Windows Terminal computes these at runtime in C++ and never writes the
/// commandline into settings.json, so it has to be reproduced here.
/// Mirrors PowershellCoreProfileGenerator.cpp: the commandline is the quoted full
/// path to pwsh.exe and the preferred instance is simply named "PowerShell".
/// </summary>
public sealed class PowerShellCoreGenerator : IProfileGenerator
{
    public const string SourceId = "Windows.Terminal.PowershellCore";

    public string Source => SourceId;

    public IReadOnlyList<TerminalProfile> Generate(IList<string>? diagnostics = null)
    {
        var instances = Discover().ToList();
        if (instances.Count == 0)
        {
            diagnostics?.Add("PowerShell Core: no pwsh.exe found");
            return [];
        }

        // Highest version wins the bare "PowerShell" name, matching Windows Terminal.
        var ordered = instances
            .OrderByDescending(i => i.Version)
            .ThenBy(i => i.Preview)
            .ToList();

        var profiles = new List<TerminalProfile>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var instance = ordered[i];

            var name = i == 0
                ? "PowerShell"
                : $"PowerShell{(instance.Preview ? " Preview" : string.Empty)} ({instance.Version})";

            profiles.Add(new TerminalProfile
            {
                Id = $"name:{SourceId}/{name}",
                Name = name,
                Source = SourceId,
                CommandLine = $"\"{instance.Path}\"",
                StartingDirectory = "%USERPROFILE%",
                ColorSchemeName = "Campbell",
                Icon = instance.Path,
            });
        }

        return profiles;
    }

    private readonly record struct Instance(int Version, bool Preview, string Path);

    private static IEnumerable<Instance> Discover()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
        ];

        // Versioned installs: <root>\PowerShell\7\pwsh.exe, ...\7-preview\pwsh.exe
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            var powerShellRoot = Path.Combine(root, "PowerShell");
            if (!Directory.Exists(powerShellRoot))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(powerShellRoot))
            {
                var exe = Path.Combine(directory, "pwsh.exe");
                if (!File.Exists(exe) || !seen.Add(exe))
                {
                    continue;
                }

                var folder = Path.GetFileName(directory);
                var preview = folder.Contains("preview", StringComparison.OrdinalIgnoreCase);
                var versionText = folder.Split('-')[0];

                yield return new Instance(
                    int.TryParse(versionText, out var version) ? version : 0,
                    preview,
                    exe);
            }
        }

        // Store (MSIX) install exposes an app-execution alias.
        var storeAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "pwsh.exe");

        if (File.Exists(storeAlias) && seen.Add(storeAlias))
        {
            yield return new Instance(0, false, storeAlias);
        }
    }
}
