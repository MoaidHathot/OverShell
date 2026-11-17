using System.Diagnostics;
using System.Text;

namespace OverShell.Core.Extensibility;

/// <summary>A command extension's launch settings. Its other settings belong to the extension.</summary>
public sealed class ProcessExtensionSettings
{
    public string Command { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string?> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Restart { get; init; } = true;
    public int RestartDelayMs { get; init; } = 1000;
    public int ShutdownTimeoutMs { get; init; } = 1500;

    /// <summary>Exact arguments, no shell parsing. Relative paths are rooted in the config directory.</summary>
    public ProcessStartInfo StartInfo(string id, string configDirectory, string stateDirectory)
    {
        string Expand(string text) => ExpandPath(text, configDirectory, stateDirectory);
        var executable = Expand(Command);
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("process.command is required");
        if (executable.Contains('/') || executable.Contains('\\')) executable = Path.GetFullPath(executable, configDirectory);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetFullPath(Expand(WorkingDirectory ?? configDirectory), configDirectory),
        };
        foreach (var argument in Args) start.ArgumentList.Add(Expand(argument));
        foreach (var (key, value) in Environment)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = Expand(value);
        }
        start.Environment["OVERSHELL_EXTENSION_ID"] = id;
        start.Environment["OVERSHELL_CONFIG_DIR"] = configDirectory;
        start.Environment["OVERSHELL_STATE_DIR"] = stateDirectory;
        start.Environment["OVERSHELL_HOST_PID"] = System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return start;
    }

    public static string ExpandPath(string text, string configDirectory, string stateDirectory) =>
        System.Environment.ExpandEnvironmentVariables(text
            .Replace("{configDir}", configDirectory, StringComparison.Ordinal)
            .Replace("{stateDir}", stateDirectory, StringComparison.Ordinal));
}
