using OverShell.Core.Git;
using Xunit;

namespace OverShell.Tests;

public class WorktreePlanTests
{
    [Theory]
    [InlineData("feature x", "feature-x")]
    [InlineData("  Fix/Login Bug!! ", "Fix/Login-Bug")]
    [InlineData("agent/opencode-1005", "agent/opencode-1005")]
    [InlineData("///", "work")]
    [InlineData("a..b", "a..b")]
    public void Branch_names_become_git_and_folder_safe(string typed, string slug) => Assert.Equal(slug, WorktreePlan.Slug(typed));

    [Fact]
    public void The_worktree_sits_beside_the_repository_and_avoids_taken_folders()
    {
        var root = @"D:\src\repo";
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\src\repo-agent-x" };

        var (path, branch) = WorktreePlan.For(root, "agent/x", taken.Contains);
        Assert.Equal(@"D:\src\repo-agent-x-2", path);
        Assert.Equal("agent/x", branch);

        var (fresh, _) = WorktreePlan.For(root + "\\", "docs", _ => false);
        Assert.Equal(@"D:\src\repo-docs", fresh);

        Assert.Equal(["worktree", "add", "-b", "agent/x", path], WorktreePlan.AddArguments(path, branch));
    }
}
