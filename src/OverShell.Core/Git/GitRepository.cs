namespace OverShell.Core.Git;

/// <summary>
/// Reads what git leaves on disk, without spawning git: the repository root above a
/// directory and the branch in <c>HEAD</c>. Worktrees and submodules keep a <c>.git</c>
/// <em>file</em> pointing at the real directory; that is followed. Cheap enough to call
/// on a heartbeat.
/// </summary>
public static class GitRepository
{
    /// <summary>The directory that contains <c>.git</c>, walking up from <paramref name="path"/>; null outside a repository.</summary>
    public static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            {
                var marker = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                {
                    return dir.FullName;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // Not a path we can look at; not a repository as far as we are concerned.
        }

        return null;
    }

    /// <summary>The git directory for a root: <c>root\.git</c>, or where a <c>.git</c> file's <c>gitdir:</c> line points.</summary>
    public static string? GitDirectory(string root)
    {
        var marker = Path.Combine(root, ".git");
        if (Directory.Exists(marker))
        {
            return marker;
        }

        if (!File.Exists(marker))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(marker))
            {
                if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    var target = line["gitdir:".Length..].Trim();
                    return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(root, target));
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    /// <summary>The path of the file whose modification time says the branch may have changed.</summary>
    public static string? HeadFile(string root) => GitDirectory(root) is { } dir ? Path.Combine(dir, "HEAD") : null;

    /// <summary>
    /// The current branch, or a short commit id when detached, or null when unreadable.
    /// Only <c>HEAD</c> is read; no process is started.
    /// </summary>
    public static string? ReadBranch(string root)
    {
        var head = HeadFile(root);
        if (head is null || !File.Exists(head))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(head).Trim();
            if (text.StartsWith("ref:", StringComparison.Ordinal))
            {
                var reference = text[4..].Trim();
                const string heads = "refs/heads/";
                return reference.StartsWith(heads, StringComparison.Ordinal) ? reference[heads.Length..] : reference;
            }

            // Detached: the file holds a commit id.
            return text.Length >= 7 ? text[..7] : text;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
