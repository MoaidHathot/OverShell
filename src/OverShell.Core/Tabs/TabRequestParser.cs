using System.Text.Json.Nodes;
using OverShell.Core.Extensibility;
using OverShell.Core.Integrations;

namespace OverShell.Core.Tabs;

/// <summary>The creation shape shared by parameterized commands and control transports.</summary>
public static class TabRequestParser
{
    public static TabRequest? Parse(JsonObject body, out string? error, bool defaultActivate = false)
    {
        error = null;
        foreach (var name in new[] { "profile", "cwd", "label", "group", "command", "prompt", "alias", "presentation", "onExit" })
            if (body[name] is not null && !(body[name] is JsonValue value && value.TryGetValue<string>(out _)))
            { error = $"{name} must be text"; return null; }
        foreach (var name in new[] { "activate", "scratch", "restore", "history" })
            if (body[name] is not null && ShellControl.Bool(body, name) is null)
            { error = $"{name} must be a boolean"; return null; }
        var presentation = ShellControl.Str(body, "presentation") ?? "tab";
        if (presentation is not ("tab" or "floating")) { error = "presentation is tab or floating"; return null; }
        TabExitBehavior? onExit = null;
        if (ShellControl.Str(body, "onExit") is { } behavior)
        {
            if (!Enum.TryParse<TabExitBehavior>(behavior, true, out var parsed) || !Enum.IsDefined(parsed))
            { error = "onExit is keep, closeOnSuccess or closeAlways"; return null; }
            onExit = parsed;
        }
        ProcessLaunch? process = null;
        if (body["process"] is not null)
        {
            if (body["process"] is not JsonObject p || p["executable"] is not JsonValue exe || !exe.TryGetValue<string>(out var executable))
            { error = "process requires an executable and an optional array of string arguments"; return null; }
            var args = new List<string>();
            if (p["arguments"] is not null)
            {
                if (p["arguments"] is not JsonArray array) { error = "process.arguments must be an array"; return null; }
                foreach (var arg in array)
                {
                    if (arg is not JsonValue v || !v.TryGetValue<string>(out var text)) { error = "process.arguments must contain only strings"; return null; }
                    args.Add(text);
                }
            }
            process = new ProcessLaunch(executable, args);
            if ((error = process.Validate()) is not null) return null;
            if (!string.IsNullOrWhiteSpace(ShellControl.Str(body, "command")) || !string.IsNullOrWhiteSpace(ShellControl.Str(body, "harness")))
            { error = "Specify either process or a shell command/harness"; return null; }
        }
        return new TabRequest(
            Profile: ShellControl.Str(body, "profile"), WorkingDirectory: ShellControl.Str(body, "cwd"),
            Label: ShellControl.Str(body, "label"), Group: ShellControl.Str(body, "group"), Command: ShellControl.Str(body, "command"),
            Activate: ShellControl.Bool(body, "activate") ?? defaultActivate, Prompt: ShellControl.Str(body, "prompt"),
            Alias: ShellControl.Str(body, "alias"), Floating: presentation == "floating", Scratch: ShellControl.Bool(body, "scratch") ?? false,
            Process: process, OnExit: onExit, Restore: ShellControl.Bool(body, "restore"), History: ShellControl.Bool(body, "history"));
    }
}
