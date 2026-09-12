using System.Diagnostics;
using System.Text.Json;

namespace OverShell.Config.Generators;

/// <summary>
/// Re-implementation of Windows Terminal's <c>Windows.Terminal.VisualStudio</c>
/// generator, which contributes a Developer Command Prompt and a Developer PowerShell
/// per installed VS instance.
/// <para>
/// Commandlines are copied verbatim from VsDevCmdGenerator.cpp / VsDevShellGenerator.cpp,
/// and the "VS &lt;suffix&gt;" name suffix is <c>catalog.productLineVersion</c> as reported
/// by vswhere (e.g. "2022", "18").
/// </para>
/// </summary>
public sealed class VisualStudioGenerator : IProfileGenerator
{
    public const string SourceId = "Windows.Terminal.VisualStudio";

    public string Source => SourceId;

    public IReadOnlyList<TerminalProfile> Generate(IList<string>? diagnostics = null)
    {
        var instances = QueryVsWhere(diagnostics);
        var profiles = new List<TerminalProfile>();

        foreach (var instance in instances)
        {
            var devCmdScript = Path.Combine(instance.InstallationPath, "Common7", "Tools", "VsDevCmd.bat");
            var devShellModule = Path.Combine(
                instance.InstallationPath, "Common7", "Tools", "Microsoft.VisualStudio.DevShell.dll");

            if (File.Exists(devCmdScript))
            {
                var name = $"Developer Command Prompt for VS {instance.Suffix}";
                profiles.Add(new TerminalProfile
                {
                    Id = $"name:{SourceId}/{name}",
                    Name = name,
                    Source = SourceId,
                    CommandLine = $"cmd.exe /k \"{devCmdScript}\" -startdir=none -arch=x64 -host_arch=x64",
                    StartingDirectory = instance.InstallationPath,
                });
            }
            else
            {
                diagnostics?.Add($"Visual Studio {instance.Suffix}: VsDevCmd.bat not found");
            }

            if (File.Exists(devShellModule))
            {
                var name = $"Developer PowerShell for VS {instance.Suffix}";
                var host = PwshOnPath() ? "pwsh.exe" : "powershell.exe";

                // Triple quotes are the PowerShell escape sequence Windows Terminal uses here.
                var commandLine =
                    $"{host} -NoExit -Command \"&{{Import-Module \"\"\"{devShellModule}\"\"\"; " +
                    $"Enter-VsDevShell {instance.InstanceId} -SkipAutomaticLocation " +
                    "-DevCmdArguments \"\"\"-arch=x64 -host_arch=x64\"\"\"}\"";

                profiles.Add(new TerminalProfile
                {
                    Id = $"name:{SourceId}/{name}",
                    Name = name,
                    Source = SourceId,
                    CommandLine = commandLine,
                    StartingDirectory = instance.InstallationPath,
                });
            }
            else
            {
                diagnostics?.Add($"Visual Studio {instance.Suffix}: DevShell module not found");
            }
        }

        return profiles;
    }

    private readonly record struct VsInstance(string InstanceId, string InstallationPath, string Suffix);

    private static string VsWherePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio", "Installer", "vswhere.exe");

    private static IReadOnlyList<VsInstance> QueryVsWhere(IList<string>? diagnostics)
    {
        if (!File.Exists(VsWherePath))
        {
            diagnostics?.Add("Visual Studio: vswhere.exe not found");
            return [];
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(VsWherePath)
            {
                Arguments = "-products * -prerelease -format json -utf8",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return [];
            }

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);

            using var document = JsonDocument.Parse(json, Json.DocumentOptions);

            return document.RootElement.EnumerateArray()
                .Select(e => new VsInstance(
                    e.Str("instanceId") ?? string.Empty,
                    e.Str("installationPath") ?? string.Empty,
                    e.Prop("catalog")?.Str("productLineVersion")
                        ?? e.Str("installationVersion")?.Split('.')[0]
                        ?? "?"))
                .Where(i => i.InstallationPath.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            diagnostics?.Add($"Visual Studio: vswhere failed — {ex.Message}");
            return [];
        }
    }

    private static bool PwshOnPath() => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
}
