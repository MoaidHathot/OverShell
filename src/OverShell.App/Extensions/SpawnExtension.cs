using System.Diagnostics;
using System.IO;
using OverShell.Core.Agents;
using OverShell.Core.Extensibility;
using OverShell.Core.Git;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// Spawning agents (DESIGN.md §12.18): <c>agent.new</c> asks for a harness, a directory and
/// an optional first prompt, opens a tab, starts the harness in it (the rule file's
/// <c>launch</c>) and delivers the prompt once the agent is idle - through its
/// integration when it listens, else pasted. <c>agent.newWorktree</c> first makes a git
/// worktree on a new branch beside the repository, so several agents can work the same
/// repository without stepping on each other's checkout.
/// </summary>
public sealed class SpawnExtension : IExtension
{
    private IShell _shell = null!;

    public string Id => "spawn";

    /// <summary>What the last spawn did, for the self-test.</summary>
    internal string LastOutcome { get; private set; } = string.Empty;

    public void Initialize(IShell shell)
    {
        _shell = shell;
        shell.Commands.Register("agent.new", "New agent: harness, directory, first prompt", "Agents", () => _ = SpawnAsync(worktree: false),
            description: "Opens a tab, starts the harness there and sends your first prompt once it is ready");
        shell.Commands.Register("agent.newWorktree", "New agent in a git worktree", "Agents", () => _ = SpawnAsync(worktree: true),
            () => shell.TargetTab?.WorkingDirectory is { } cwd && GitRepository.FindRoot(cwd) is not null,
            "A new branch and worktree beside this tab's repository, then an agent in it");
        shell.Keys.AddDefaults(
        [
            new Keybinding($"{HerdModeExtension.Leader} shift+n", "agent.new"),
            new Keybinding($"{HerdModeExtension.Leader} shift+w", "agent.newWorktree"),
        ]);
    }

    private async Task SpawnAsync(bool worktree)
    {
        var harnesses = _shell.AgentRules.Where(r => !string.IsNullOrWhiteSpace(r.Launch)).OrderBy(r => r.DisplayName).ToList();
        if (harnesses.Count == 0)
        {
            _shell.Ui.Status("No rule file names a launch command (\"launch\" in agents\\<id>.jsonc)");
            return;
        }

        var pick = await _shell.Ui.PickAsync("\uE99A", "No harness has a launch command", harnesses.Select(r => new PickItem(r.DisplayName, r.Launch, Keywords: r.Id, Tag: r)).ToList());
        if (pick?.Tag is not AgentRuleSet rules)
        {
            return;
        }

        var startIn = _shell.TargetTab?.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? directory;
        string note;
        if (worktree)
        {
            var root = GitRepository.FindRoot(startIn);
            if (root is null)
            {
                _shell.Ui.Status("This tab is not in a git repository");
                return;
            }

            var branch = await _shell.Ui.PromptAsync($"Branch for the new worktree (beside {Path.GetFileName(root)})", $"agent/{rules.Id}-{DateTime.Now:MMdd-HHmm}");
            if (branch is null || branch.Trim().Length == 0)
            {
                return;
            }

            var (path, branchName) = WorktreePlan.For(root, branch, Directory.Exists);
            var (ok, output) = await RunGitAsync(root, WorktreePlan.AddArguments(path, branchName));
            if (!ok)
            {
                _shell.Ui.Status($"git worktree add failed: {output.Trim().Split('\n').LastOrDefault()}");
                LastOutcome = $"worktree failed: {output.Trim()}";
                return;
            }

            directory = path;
            note = $"worktree {branchName} at {path}";
        }
        else
        {
            directory = await _shell.Ui.PromptAsync("Directory for the agent", startIn);
            if (directory is null)
            {
                return;
            }

            directory = Environment.ExpandEnvironmentVariables(directory.Trim());
            if (directory.Length == 0)
            {
                directory = startIn;
            }

            if (!Directory.Exists(directory))
            {
                _shell.Ui.Status($"No such directory: {directory}");
                return;
            }

            note = directory;
        }

        var prompt = await _shell.Ui.PromptAsync($"First prompt for {rules.DisplayName} (empty for none)", string.Empty);
        if (prompt is null)
        {
            return;
        }

        var tab = _shell.OpenTab(new TabRequest(
            WorkingDirectory: directory,
            Label: $"{rules.DisplayName} {Path.GetFileName(directory.TrimEnd('\\', '/'))}",
            Command: rules.Launch,
            Prompt: prompt.Trim().Length == 0 ? null : prompt.Trim(),
            Activate: true));

        LastOutcome = tab is null ? "no tab" : $"{rules.Id} in {note}{(prompt.Trim().Length == 0 ? string.Empty : " with a prompt")}";
        _shell.Trace($"spawn: {LastOutcome}");
        if (tab is not null)
        {
            _shell.Ui.Status($"{rules.DisplayName} starting in {note}{(prompt.Trim().Length == 0 ? string.Empty : "; the prompt goes in once it is ready")}");
        }
    }

    /// <summary>Runs git in <paramref name="root"/>; (success, combined output).</summary>
    internal static async Task<(bool Ok, string Output)> RunGitAsync(string root, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return (false, "git did not start");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = (await stdout) + (await stderr);
            return (process.ExitCode == 0, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return (false, e.Message);
        }
    }

    public void Dispose()
    {
    }
}
