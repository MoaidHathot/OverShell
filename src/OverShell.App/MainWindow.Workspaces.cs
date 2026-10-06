using System.IO;
using OverShell.App.Chrome;
using OverShell.Config;
using OverShell.Core;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// Workspaces (DESIGN.md §12.14): named sets of tabs under <c>workspaces\</c> in the
/// configuration root. <c>workspace.open.&lt;slug&gt;</c> adds a workspace's tabs next to the
/// open ones (like reopening a session from the history); <c>workspace.save</c> writes the
/// current tabs as one. Commands follow the files: they are re-registered on reload, so
/// a new file is a command without a restart.
/// </summary>
public partial class MainWindow
{
    private WorkspaceCatalog _workspaces = new();

    internal WorkspaceCatalog Workspaces => _workspaces;

    private void InitializeWorkspaces()
    {
        _commands.Register("workspace.save", "Save tabs as a workspace…", "Workspaces", SaveWorkspacePrompt, () => Tabs.Count > 0, "Writes the open tabs (profile, directory, label, group, agent) to workspaces\\<name>.jsonc");
        LoadWorkspaces();
    }

    /// <summary>Re-reads <c>workspaces\</c> and makes the <c>workspace.open.*</c> commands match the files.</summary>
    private void LoadWorkspaces()
    {
        _workspaces = WorkspaceCatalog.Load(AppPaths.WorkspacesDir);
        foreach (var problem in _workspaces.Problems)
        {
            _trace.Write($"workspaces: {problem}");
        }

        foreach (var stale in _commands.All.Where(c => c.Id.StartsWith("workspace.open.", StringComparison.Ordinal)).Select(c => c.Id).ToArray())
        {
            _commands.Remove(stale);
        }

        foreach (var workspace in _workspaces.All.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
        {
            var captured = workspace;
            _commands.Register(workspace.CommandId, $"Workspace: {workspace.Name}", "Workspaces", () => OpenWorkspace(captured),
                description: workspace.Description ?? $"{workspace.Tabs.Count} tab{(workspace.Tabs.Count == 1 ? string.Empty : "s")} - {string.Join(", ", workspace.Tabs.Select(DescribeTab).Distinct())}");
        }

        static string DescribeTab(WorkspaceTab t) => t.Label ?? t.Command?.Split(' ')[0] ?? t.Profile ?? "shell";

        if (IsLoaded)
        {
            UpdateJumpList();
        }
    }

    /// <summary>Opens every tab of <paramref name="workspace"/> next to the open ones; the first becomes active. Returns how many opened.</summary>
    internal int OpenWorkspace(Workspace workspace)
    {
        TerminalTab? first = null;
        var opened = 0;
        foreach (var entry in workspace.Tabs)
        {
            var profile = ProfileNamed(entry.Profile);
            if (profile is null)
            {
                _trace.Write($"workspace '{workspace.Name}': no launchable profile for '{entry.Profile}' and no default; tab skipped");
                continue;
            }

            var cwd = entry.Cwd is null ? null : Environment.ExpandEnvironmentVariables(entry.Cwd);
            if (cwd is not null && Directory.Exists(cwd))
            {
                profile = profile with { StartingDirectory = cwd };
            }
            else if (cwd is not null)
            {
                _trace.Write($"workspace '{workspace.Name}': directory '{cwd}' is not there; the profile's own is used");
            }

            // The command goes through the restore planner: typed into a shell once it is quiet,
            // appended to the program of a profile that is the agent itself.
            ResumePlan? plan = null;
            if (!string.IsNullOrWhiteSpace(entry.Command))
            {
                var asSaved = new SavedTab { Harness = _agents.Rules.DetectFromCommandline(entry.Command), AgentRunning = true, ResumeCommand = entry.Command.Trim() };
                plan = SessionRestore.Plan(asSaved, RestoreCommandLine(profile), _agents.Rules, new SessionSettings { ResumeAgents = true, ResumeWithoutId = false });
                if (plan.Mode == ResumeMode.None)
                {
                    // Not an agent, or a profile the planner will not relaunch: a shell gets it typed anyway.
                    plan = _agents.Rules.DetectFromCommandline(RestoreCommandLine(profile)) is null
                        ? new ResumePlan(ResumeMode.Typed, entry.Command.Trim(), "workspace command")
                        : ResumePlan.None(plan.Reason);
                }

                if (plan.Mode == ResumeMode.Relaunch)
                {
                    profile = profile with { CommandLine = plan.Command };
                }
            }

            var tab = AddTab(profile, activate: false);
            if (!string.IsNullOrWhiteSpace(entry.Label))
            {
                tab.UserLabel = entry.Label;
            }

            tab.Group = entry.Group;
            if (plan is { Mode: ResumeMode.Typed, Command: { } command })
            {
                tab.ScheduleResume(command);
            }

            if (!string.IsNullOrWhiteSpace(entry.Prompt))
            {
                tab.ScheduleFirstPrompt(entry.Prompt.Trim());
            }

            tab.RestoreNote = $"from workspace '{workspace.Name}'{(plan is null ? string.Empty : $" · {plan.Mode.ToString().ToLowerInvariant()} `{plan.Command}`")}";
            if (entry.Detached && Tabs.Count > 1)
            {
                Dispatcher.BeginInvoke(() => { if (Tabs.Contains(tab) && !tab.Detached) { Detach(tab); } }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            first ??= tab;
            opened++;
        }

        if (first is not null)
        {
            ActiveTab = first;
        }

        if (workspace.View is { } view && AppSettings.ViewOrder.Contains(view, StringComparer.OrdinalIgnoreCase))
        {
            ApplyView(view);
        }

        _trace.Write($"workspace '{workspace.Name}': opened {opened} of {workspace.Tabs.Count} tab(s)");
        ShowStatusMessage($"Workspace '{workspace.Name}': {opened} tab{(opened == 1 ? string.Empty : "s")} opened");
        return opened;
    }

    /// <summary>A profile by name or id, else the default; null when nothing launchable exists.</summary>
    private TerminalProfile? ProfileNamed(string? nameOrId)
    {
        var profile = nameOrId is null
            ? null
            : _catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, nameOrId, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        profile ??= _catalog.DefaultProfile;
        return profile is { IsLaunchable: true } ? profile : null;
    }

    /// <summary>The open tabs as a workspace, named by the user through the prompt.</summary>
    private void SaveWorkspacePrompt()
    {
        _palette?.Close();
        _palette = PaletteWindow.Prompt(this, "Workspace name - the open tabs are written to workspaces\\<name>.jsonc", string.Empty, name =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var path = SaveWorkspace(name.Trim());
            ShowStatusMessage($"Workspace '{name.Trim()}' saved to {path}");
        });
        _palette.Closed += (_, _) =>
        {
            _palette = null;
            ActiveTab?.Surface.Focus();
        };
    }

    /// <summary>Writes the open tabs as a workspace file and registers its command. Returns the path.</summary>
    internal string SaveWorkspace(string name)
    {
        var workspace = new Workspace
        {
            Name = name,
            View = _viewId,
            Tabs = Tabs.Where(t => t.IsRunning || !t.HasStarted).Select(t => new WorkspaceTab
            {
                Profile = t.Profile.Name,
                Cwd = t.WorkingDirectory,
                Label = t.UserLabel,
                Group = t.Group,
                // A running agent is started again by its program name; a plain shell gets no command.
                Command = t.IsAgent && t.Harness is { } harness ? _agents.Rules.Find(harness)?.Detect.Process.FirstOrDefault() ?? harness : null,
                Detached = t.Detached,
            }).ToList(),
        };

        var path = WorkspaceCatalog.Save(AppPaths.WorkspacesDir, workspace);
        _trace.Write($"workspace '{name}': saved {workspace.Tabs.Count} tab(s) to {path}");
        LoadWorkspaces();
        return path;
    }
}
