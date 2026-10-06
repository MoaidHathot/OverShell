namespace OverShell.Core.Git;

/// <summary>
/// Where a new worktree for a repository goes and what to call its branch (DESIGN.md
/// §12.18). Pure: the shell runs <c>git worktree add</c> with the result. The worktree
/// sits <em>beside</em> the repository - <c>repo-feature-x</c> next to <c>repo</c> - so it
/// is never inside the checkout it was made from, and the branch takes the same name.
/// </summary>
public static class WorktreePlan
{
    /// <summary>What the user may type as a branch name, turned into something git and a file system accept.</summary>
    public static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var dash = false;
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_')
            {
                sb.Append(ch);
                dash = false;
            }
            else if (ch is '/' && sb.Length > 0 && !dash)
            {
                // Branch namespaces are fine (feature/x); a path segment cannot be, so the folder uses a dash.
                sb.Append('/');
                dash = true;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }

        var slug = sb.ToString().Trim('-', '/', '.');
        return slug.Length == 0 ? "work" : slug;
    }

    /// <param name="repositoryRoot">The checkout the worktree is made from.</param>
    /// <param name="branch">The branch name as typed.</param>
    /// <param name="exists">Whether a path exists, so a taken folder gets a numbered sibling.</param>
    public static (string Path, string Branch) For(string repositoryRoot, string branch, Func<string, bool> exists)
    {
        var root = repositoryRoot.TrimEnd('\\', '/');
        var parent = System.IO.Path.GetDirectoryName(root) ?? root;
        var repoName = System.IO.Path.GetFileName(root);
        var branchSlug = Slug(branch);
        var folderSlug = branchSlug.Replace('/', '-');

        var candidate = System.IO.Path.Combine(parent, $"{repoName}-{folderSlug}");
        var n = 2;
        while (exists(candidate))
        {
            candidate = System.IO.Path.Combine(parent, $"{repoName}-{folderSlug}-{n++}");
        }

        return (candidate, branchSlug);
    }

    /// <summary>The git arguments: a new branch at HEAD, checked out into the path.</summary>
    public static IReadOnlyList<string> AddArguments(string path, string branch) => ["worktree", "add", "-b", branch, path];
}
