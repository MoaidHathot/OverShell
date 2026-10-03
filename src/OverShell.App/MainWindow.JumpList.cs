using System.Windows;
using System.Windows.Shell;
using OverShell.Core;
using OverShell.Core.Integrations;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// The taskbar jump list (DESIGN.md §12.14): right-click the taskbar button for a new tab,
/// the last closed tab, the session history, the last few sessions and the workspaces.
/// Every entry starts OverShell with an <c>overshell://</c> URL; a running instance gets it
/// handed over (§12.11), a cold start handles it once the window is up - so the list works
/// from a pinned button with nothing running. Keyed by the process's AppUserModelID.
/// </summary>
public partial class MainWindow
{
    private const int JumpListSessions = 5;

    /// <summary>Rebuilds the jump list from the current history and workspaces.</summary>
    internal void UpdateJumpList()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };

        list.JumpItems.Add(Task("New tab", "Open a tab with the default profile", ProtocolRequest.NewUrl, null));
        list.JumpItems.Add(Task("Reopen closed tab", "The most recently closed tab, agent session included", ProtocolRequest.ReopenUrl, null));
        list.JumpItems.Add(Task("Session history", "Recently closed tabs and earlier sessions", ProtocolRequest.HistoryUrl(), null));

        foreach (var session in SessionHistory.List(AppPaths.StateRoot).Take(JumpListSessions))
        {
            var when = session.SavedAt.ToLocalTime();
            var how = session.Interrupted ? "interrupted" : session.CloseReason == SessionCloseReason.SessionEnding ? "signed out" : "closed";
            list.JumpItems.Add(Task(
                $"{when:ddd HH:mm} · {SessionSnapshot.Describe(session.Tabs, NameOf)}",
                $"Session {how} {when:f} - opens its tabs next to the open ones",
                ProtocolRequest.HistoryUrl(System.IO.Path.GetFileNameWithoutExtension(session.Path)),
                "Recent sessions"));
        }

        foreach (var workspace in _workspaces.All.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.JumpItems.Add(Task(
                workspace.Name,
                workspace.Description ?? $"{workspace.Tabs.Count} tab{(workspace.Tabs.Count == 1 ? string.Empty : "s")}",
                ProtocolRequest.WorkspaceUrl(workspace.Name),
                "Workspaces"));
        }

        try
        {
            JumpList.SetJumpList(Application.Current, list);
            _trace.Write($"jump list: {list.JumpItems.Count} item(s)");
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // The shell declined (a restricted session, a policy); the list is a convenience.
            _trace.Write($"jump list: not applied: {e.Message}");
        }

        JumpTask Task(string title, string description, string url, string? category) => new()
        {
            Title = title,
            Description = description,
            ApplicationPath = exe,
            Arguments = url,
            IconResourcePath = exe,
            IconResourceIndex = 0,
            CustomCategory = category,
        };
    }
}
