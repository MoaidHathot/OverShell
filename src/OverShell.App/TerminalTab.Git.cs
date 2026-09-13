using System.IO;
using OverShell.Core.Git;

namespace OverShell.App;

/// <summary>
/// Git decoration for a tab: the branch from <c>.git/HEAD</c> (a file read, on the
/// heartbeat, only when the file changed) and, when enabled, a dirty marker from the
/// shared <see cref="GitStatusService"/>. Never spawns git itself.
/// </summary>
public sealed partial class TerminalTab
{
    private static readonly TimeSpan GitCheckInterval = TimeSpan.FromSeconds(2);

    private string? _gitCwd;
    private string? _gitRoot;
    private string? _headFile;
    private DateTime _headWriteTime;
    private DateTimeOffset _lastGitCheck;
    private string? _branch;
    private bool? _dirty;

    /// <summary>The repository root this tab's directory is in, or null.</summary>
    public string? GitRoot => _gitRoot;

    public string? Branch => _branch;

    /// <summary>True when the repository has uncommitted changes; null when unknown or the check is off.</summary>
    public bool? Dirty => _dirty;

    /// <summary>"project" / "project · main" / "project · main*", for headers.</summary>
    public string ProjectAndBranch
    {
        get
        {
            var project = Project;
            if (string.IsNullOrEmpty(_branch))
            {
                return project;
            }

            var branch = _dirty == true ? _branch + "*" : _branch;
            return project.Length == 0 ? branch : $"{project} · {branch}";
        }
    }

    /// <summary>The sidebar's second line: state and summary for agents, branch for shells.</summary>
    public string SidebarDetail
    {
        get
        {
            if (IsAgent)
            {
                var what = Agent.Summary ?? (_branch is null ? string.Empty : _dirty == true ? _branch + "*" : _branch);
                return what.Length == 0 ? StateText : StateText.Length == 0 ? what : $"{StateText} · {what}";
            }

            var branch = _branch is null ? string.Empty : _dirty == true ? _branch + "*" : _branch;
            var state = StateText;
            var parts = new[] { state, branch, string.IsNullOrEmpty(_workingDirectory) ? string.Empty : Path.GetFileName(_workingDirectory.TrimEnd('\\', '/')) }
                .Where(p => p.Length > 0);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Heartbeat step: re-reads the branch when the directory or <c>HEAD</c> changed; asks for status when wanted.</summary>
    internal void RefreshGit(DateTimeOffset now, bool branchWanted, GitStatusService? status)
    {
        if (!branchWanted)
        {
            if (_branch is not null || _dirty is not null)
            {
                _branch = null;
                _dirty = null;
                RaiseGit();
            }

            return;
        }

        if (now - _lastGitCheck < GitCheckInterval)
        {
            return;
        }

        _lastGitCheck = now;
        var changed = false;

        if (!string.Equals(_gitCwd, _workingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _gitCwd = _workingDirectory;
            var root = GitRepository.FindRoot(_workingDirectory);
            if (!string.Equals(root, _gitRoot, StringComparison.OrdinalIgnoreCase))
            {
                _gitRoot = root;
                _headFile = root is null ? null : GitRepository.HeadFile(root);
                _headWriteTime = default;
                _dirty = null;
                changed = true;
            }
        }

        if (_gitRoot is not null && _headFile is not null)
        {
            var writeTime = SafeWriteTime(_headFile);
            if (writeTime != _headWriteTime)
            {
                _headWriteTime = writeTime;
                var branch = GitRepository.ReadBranch(_gitRoot);
                if (branch != _branch)
                {
                    _branch = branch;
                    changed = true;
                }
            }

            if (status is not null)
            {
                var dirty = status.Query(_gitRoot, now);
                if (dirty != _dirty)
                {
                    _dirty = dirty;
                    changed = true;
                }
            }
        }
        else if (_branch is not null)
        {
            _branch = null;
            _dirty = null;
            changed = true;
        }

        if (changed)
        {
            RaiseGit();
        }
    }

    /// <summary>The shared status service answered for this tab's repository.</summary>
    internal void ApplyDirty(bool? dirty)
    {
        if (_dirty == dirty)
        {
            return;
        }

        _dirty = dirty;
        RaiseGit();
    }

    private void RaiseGit()
    {
        Raise(nameof(Branch));
        Raise(nameof(Dirty));
        Raise(nameof(GitRoot));
        Raise(nameof(ProjectAndBranch));
        Raise(nameof(SidebarDetail));
        Raise(nameof(Detail));
        Raise(nameof(Tooltip));
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
