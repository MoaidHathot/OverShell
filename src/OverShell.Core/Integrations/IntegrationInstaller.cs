using System.Text.Json.Nodes;

namespace OverShell.Core.Integrations;

/// <summary>Where an integration stands on this machine.</summary>
/// <param name="Id">opencode | copilot</param>
/// <param name="Path">The file OverShell writes.</param>
/// <param name="Installed">The file exists.</param>
/// <param name="Current">Its content is what this build would write.</param>
/// <param name="HarnessFound">The harness executable is on PATH.</param>
public sealed record IntegrationStatus(string Id, string Path, bool Installed, bool Current, bool HarnessFound);

/// <summary>
/// Writes the small files that make a harness report state to OverShell, always as a
/// separate file the harness auto-loads — never by editing the user's own configuration.
/// </summary>
public static class IntegrationInstaller
{
    public const string Marker = "OverShell integration v1";

    public static IReadOnlyList<string> Ids => ["opencode", "copilot"];

    // ------------------------------------------------------------------ paths

    /// <summary>OpenCode's global config dir, resolved the way OpenCode does: <c>$XDG_CONFIG_HOME/opencode</c>, else <c>~/.config/opencode</c>.</summary>
    public static string OpenCodeConfigDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = !string.IsNullOrWhiteSpace(xdg)
            ? Path.GetFullPath(xdg)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "opencode");
    }

    public static string OpenCodePluginPath() => Path.Combine(OpenCodeConfigDir(), "plugins", "overshell.ts");

    /// <summary>Copilot's user directory: <c>$COPILOT_HOME</c>, else <c>~/.copilot</c>.</summary>
    public static string CopilotHomeDir()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        return !string.IsNullOrWhiteSpace(home)
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
    }

    public static string CopilotHookPath() => Path.Combine(CopilotHomeDir(), "hooks", "overshell.json");

    // ------------------------------------------------------------------ content

    public static string OpenCodePluginContent() => EmbeddedResources.Read("integrations/opencode-overshell.ts");

    /// <summary>
    /// One hook entry per event, each a <c>cmd.exe /d /c</c> line that cmd expands
    /// (<c>%VAR%</c>) and curl posts. Deliberately no quotes anywhere: Node quotes an
    /// argument containing spaces and cmd strips those outer quotes, but an inner quote
    /// would survive as <c>\"</c> and break the URL. The token therefore travels in the
    /// query string rather than an <c>Authorization</c> header, and the body is stdin.
    /// Outside OverShell the variables are undefined and <c>if defined</c> skips the call.
    /// </summary>
    public static string CopilotHookContent()
    {
        var hooks = new JsonObject();
        foreach (var eventName in CopilotHookTranslator.Events)
        {
            var command =
                "if defined OVERSHELL_ENDPOINT curl.exe -s -m 3 -o NUL -X POST " +
                $"%OVERSHELL_ENDPOINT%/v1/copilot/%OVERSHELL_TAB_ID%/{eventName}?token=%OVERSHELL_TOKEN% " +
                "-H Content-Type:application/json --data-binary @-";

            hooks[eventName] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["exec"] = "cmd.exe",
                ["args"] = new JsonArray("/d", "/c", command),
                ["timeoutSec"] = 5,
            });
        }

        var root = new JsonObject
        {
            ["$comment"] = $"{Marker}. Written by `OverShell integrations install copilot`; safe to delete. Reports session state to a running OverShell over loopback; does nothing elsewhere.",
            ["version"] = 1,
            ["hooks"] = hooks,
        };

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    // ------------------------------------------------------------------ actions

    public static IntegrationStatus Status(string id) => id.ToLowerInvariant() switch
    {
        "opencode" => Describe(id, OpenCodePluginPath(), OpenCodePluginContent(), "opencode"),
        "copilot" => Describe(id, CopilotHookPath(), CopilotHookContent(), "copilot"),
        _ => throw new ArgumentException($"Unknown integration '{id}'. Known: {string.Join(", ", Ids)}."),
    };

    public static IntegrationStatus Install(string id)
    {
        var (path, content) = id.ToLowerInvariant() switch
        {
            "opencode" => (OpenCodePluginPath(), OpenCodePluginContent()),
            "copilot" => (CopilotHookPath(), CopilotHookContent()),
            _ => throw new ArgumentException($"Unknown integration '{id}'. Known: {string.Join(", ", Ids)}."),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return Status(id);
    }

    public static IntegrationStatus Uninstall(string id)
    {
        var status = Status(id);
        if (status.Installed)
        {
            File.Delete(status.Path);
        }

        return Status(id);
    }

    private static IntegrationStatus Describe(string id, string path, string expected, string exe)
    {
        var installed = File.Exists(path);
        var current = false;
        if (installed)
        {
            try
            {
                current = Normalize(File.ReadAllText(path)) == Normalize(expected);
            }
            catch (IOException)
            {
            }
        }

        return new IntegrationStatus(id, path, installed, current, OnPath(exe));
    }

    private static string Normalize(string s) => s.ReplaceLineEndings("\n").Trim();

    /// <summary>True when <paramref name="name"/> (with any PATHEXT extension) exists on PATH.</summary>
    public static bool OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.PS1")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(string.Empty);

        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), name + ext)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not our problem.
                }
            }
        }

        return false;
    }
}
