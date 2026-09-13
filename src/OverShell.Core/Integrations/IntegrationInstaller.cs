using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverShell.Core.Integrations;

/// <summary>Where an integration stands on this machine.</summary>
/// <param name="Id">opencode | copilot | claude</param>
/// <param name="Path">The file OverShell writes (or, for Claude, edits).</param>
/// <param name="Installed">Our entries are present.</param>
/// <param name="Current">They are what this build would write.</param>
/// <param name="HarnessFound">The harness executable is on PATH.</param>
/// <param name="Note">Something the user should know — e.g. why an install was refused.</param>
public sealed record IntegrationStatus(string Id, string Path, bool Installed, bool Current, bool HarnessFound, string? Note = null);

/// <summary>
/// Writes the small files that make a harness report state to OverShell — as a separate
/// file the harness auto-loads wherever the harness allows it (OpenCode plugins, Copilot's
/// hooks directory). Claude Code has no such directory: its hooks live in
/// <c>~/.claude/settings.json</c>, so there the entries are merged in and out under a
/// marker, and a file with comments is left alone rather than rewritten.
/// </summary>
public static class IntegrationInstaller
{
    public const string Marker = "OverShell integration v1";

    /// <summary>Every hook command we write contains this, which is how uninstall finds its own entries.</summary>
    public const string CommandMarker = "OVERSHELL_ENDPOINT";

    public static IReadOnlyList<string> Ids => ["opencode", "copilot", "claude"];

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

    /// <summary>Claude Code's user settings: <c>$CLAUDE_CONFIG_DIR/settings.json</c>, else <c>~/.claude/settings.json</c>.</summary>
    public static string ClaudeSettingsPath()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var root = !string.IsNullOrWhiteSpace(dir)
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(root, "settings.json");
    }

    // ------------------------------------------------------------------ content

    public static string OpenCodePluginContent() => EmbeddedResources.Read("integrations/opencode-overshell.ts");

    /// <summary>
    /// The one shim every hook-style harness gets: a <c>cmd.exe /d /c</c> line that cmd
    /// expands (<c>%VAR%</c>) and curl posts, the payload from stdin. Deliberately no quotes
    /// anywhere: a spawner quotes an argument containing spaces and cmd strips those outer
    /// quotes, but an inner quote would survive as <c>\"</c> and break the URL. The token
    /// therefore travels in the query string. Outside OverShell the variables are undefined
    /// and <c>if defined</c> skips the call. Works the same whether the harness runs the
    /// line through cmd or through bash, which passes <c>%…%</c> to cmd untouched.
    /// </summary>
    public static string ShimCommand(string route, string eventName) =>
        "if defined OVERSHELL_ENDPOINT curl.exe -s -m 3 -o NUL -X POST " +
        $"%OVERSHELL_ENDPOINT%/v1/{route}/%OVERSHELL_TAB_ID%/{eventName}?token=%OVERSHELL_TOKEN% " +
        "-H Content-Type:application/json --data-binary @-";

    /// <summary>One hook entry per event, in Copilot's hook-file shape.</summary>
    public static string CopilotHookContent()
    {
        var hooks = new JsonObject();
        foreach (var eventName in CopilotHookTranslator.Events)
        {
            hooks[eventName] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["exec"] = "cmd.exe",
                ["args"] = new JsonArray("/d", "/c", ShimCommand("copilot", eventName)),
                ["timeoutSec"] = 5,
            });
        }

        var root = new JsonObject
        {
            ["$comment"] = $"{Marker}. Written by `OverShell integrations install copilot`; safe to delete. Reports session state to a running OverShell over loopback; does nothing elsewhere.",
            ["version"] = 1,
            ["hooks"] = hooks,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>Claude's shape: <c>hooks.&lt;Event&gt;</c> is a list of matcher groups, each with its own <c>hooks</c> list.</summary>
    public static JsonObject ClaudeHookGroup(string eventName) => new()
    {
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = $"cmd.exe /d /c \"{ShimCommand("claude", eventName)}\"",
            ["timeout"] = 5,
        }),
    };

    // ------------------------------------------------------------------ actions

    public static IntegrationStatus Status(string id) => id.ToLowerInvariant() switch
    {
        "opencode" => Describe(id, OpenCodePluginPath(), OpenCodePluginContent(), "opencode"),
        "copilot" => Describe(id, CopilotHookPath(), CopilotHookContent(), "copilot"),
        "claude" => ClaudeStatus(),
        _ => throw new ArgumentException($"Unknown integration '{id}'. Known: {string.Join(", ", Ids)}."),
    };

    public static IntegrationStatus Install(string id)
    {
        switch (id.ToLowerInvariant())
        {
            case "opencode":
                WriteFile(OpenCodePluginPath(), OpenCodePluginContent());
                return Status(id);
            case "copilot":
                WriteFile(CopilotHookPath(), CopilotHookContent());
                return Status(id);
            case "claude":
                return ClaudeInstall(remove: false);
            default:
                throw new ArgumentException($"Unknown integration '{id}'. Known: {string.Join(", ", Ids)}.");
        }
    }

    public static IntegrationStatus Uninstall(string id)
    {
        if (id.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            return ClaudeInstall(remove: true);
        }

        var status = Status(id);
        if (status.Installed)
        {
            File.Delete(status.Path);
        }

        return Status(id);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
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

    // ------------------------------------------------------------------ claude

    private static IntegrationStatus ClaudeStatus()
    {
        var path = ClaudeSettingsPath();
        var found = OnPath("claude");
        if (!File.Exists(path))
        {
            return new IntegrationStatus("claude", path, false, false, found);
        }

        var (root, note) = ReadClaudeSettings(path);
        if (root is null)
        {
            return new IntegrationStatus("claude", path, false, false, found, note);
        }

        var ours = OurClaudeEvents(root);
        var installed = ours.Count > 0;
        var current = installed && ClaudeHookTranslator.Events.All(ours.Contains);
        return new IntegrationStatus("claude", path, installed, current, found, note);
    }

    /// <summary>Merges our hook groups in (or takes them out) and rewrites the file; everything else is kept as parsed.</summary>
    private static IntegrationStatus ClaudeInstall(bool remove)
    {
        var path = ClaudeSettingsPath();
        JsonObject root;
        if (File.Exists(path))
        {
            var (parsed, note) = ReadClaudeSettings(path);
            if (parsed is null)
            {
                // Refusing beats destroying: a file we cannot round-trip faithfully is the user's.
                return new IntegrationStatus("claude", path, false, false, OnPath("claude"),
                    note ?? "settings.json could not be parsed") with
                {
                    Note = (note ?? "settings.json could not be parsed") + ". Add the hook entries by hand: `OverShell integrations show claude`.",
                };
            }

            root = parsed;
        }
        else if (remove)
        {
            return new IntegrationStatus("claude", path, false, false, OnPath("claude"));
        }
        else
        {
            root = new JsonObject();
        }

        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var eventName in ClaudeHookTranslator.Events)
        {
            var groups = hooks[eventName] as JsonArray ?? [];
            var kept = new JsonArray();
            foreach (var group in groups.ToArray())
            {
                if (group is not null && !IsOurs(group))
                {
                    kept.Add(group.DeepClone());
                }
            }

            if (!remove)
            {
                kept.Add(ClaudeHookGroup(eventName));
            }

            if (kept.Count > 0)
            {
                hooks[eventName] = kept;
            }
            else
            {
                hooks.Remove(eventName);
            }
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        WriteFile(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return ClaudeStatus();
    }

    /// <summary>Strict JSON only: a file with comments would lose them on rewrite, so it is reported instead.</summary>
    private static (JsonObject? Root, string? Note) ReadClaudeSettings(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException e)
        {
            return (null, e.Message);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return (new JsonObject(), null);
        }

        try
        {
            var strict = JsonNode.Parse(text, Jsonc.NodeOptions, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            return strict is JsonObject o ? (o, null) : (null, "settings.json is not a JSON object");
        }
        catch (JsonException)
        {
            // Lenient parse tells comments apart from corruption.
            return Jsonc.Parse(text, out var error) is JsonObject
                ? (null, "settings.json contains comments or trailing commas, which a rewrite would drop")
                : (null, $"settings.json is not valid JSON: {error}");
        }
    }

    private static HashSet<string> OurClaudeEvents(JsonObject root)
    {
        var events = new HashSet<string>(StringComparer.Ordinal);
        if (root["hooks"] is not JsonObject hooks)
        {
            return events;
        }

        foreach (var (eventName, groups) in hooks)
        {
            if (groups is JsonArray array && array.Any(g => g is not null && IsOurs(g)))
            {
                events.Add(eventName);
            }
        }

        return events;
    }

    /// <summary>A matcher group is ours when any of its hooks' commands names our environment variable.</summary>
    private static bool IsOurs(JsonNode group) =>
        group is JsonObject o && o["hooks"] is JsonArray list &&
        list.Any(h => h is JsonObject ho && ho["command"] is JsonValue v && v.TryGetValue<string>(out var c) && c.Contains(CommandMarker, StringComparison.Ordinal));

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
