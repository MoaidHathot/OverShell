namespace OverShell.Core.Git;

/// <summary>One line of <c>git status --porcelain</c>: the two status letters and the path (the new one for a rename).</summary>
public sealed record GitChange(string Status, string Path)
{
    /// <summary>The status in words, for a list a person reads.</summary>
    public string Describe() => Status switch
    {
        "??" => "untracked",
        "!!" => "ignored",
        _ when Status.Contains('U') || Status is "AA" or "DD" => "conflict",
        _ when Status.Contains('R') => "renamed",
        _ when Status.Contains('C') => "copied",
        _ when Status.Contains('A') => "added",
        _ when Status.Contains('D') => "deleted",
        _ when Status.Contains('M') => "modified",
        _ when Status.Contains('T') => "type changed",
        "  " => "clean",
        _ => Status.Trim(),
    };
}

/// <summary>
/// <c>git status --porcelain</c> read and compared (DESIGN.md §12.19): what an agent changed
/// in its repository between two looks. Parsing and the comparison are pure, so they are
/// tested without git; running git is the small part at the end.
/// </summary>
public static class GitStatus
{
    /// <summary>Parses porcelain v1 output: <c>XY path</c>, <c>R  old -> new</c>; blank lines skipped.</summary>
    public static IReadOnlyList<GitChange> Parse(string? output)
    {
        var changes = new List<GitChange>();
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4)
            {
                continue;
            }

            var status = line[..2];
            var path = line[3..];
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..];
            }

            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            {
                path = path[1..^1];
            }

            changes.Add(new GitChange(status, path));
        }

        return changes;
    }

    /// <summary>
    /// What is different between <paramref name="before"/> and <paramref name="now"/>, by path:
    /// a path that appeared or whose status changed is reported with its status now; a path
    /// that left the list (committed, reverted) is reported as "clean" - that was a change too.
    /// </summary>
    public static IReadOnlyList<GitChange> Compare(IReadOnlyList<GitChange> before, IReadOnlyList<GitChange> now)
    {
        var previous = before.ToDictionary(c => c.Path, c => c.Status, StringComparer.Ordinal);
        var changed = new List<GitChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in now)
        {
            seen.Add(change.Path);
            if (!previous.TryGetValue(change.Path, out var was) || was != change.Status)
            {
                changed.Add(change);
            }
        }

        foreach (var gone in before.Where(c => !seen.Contains(c.Path)))
        {
            changed.Add(new GitChange("  ", gone.Path));
        }

        return changed;
    }

    /// <summary>
    /// Runs <c>git status --porcelain</c> in <paramref name="root"/>; null when git is missing,
    /// fails or takes longer than five seconds - unknown, not "clean".
    /// </summary>
    public static async Task<IReadOnlyList<GitChange>?> RunAsync(string root, CancellationToken cancellation = default)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            info.ArgumentList.Add("status");
            info.ArgumentList.Add("--porcelain");
            info.ArgumentList.Add("--untracked-files=all");

            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(5000);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? Parse(await output.ConfigureAwait(false)) : null;
        }
        catch (Exception e) when (e is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
