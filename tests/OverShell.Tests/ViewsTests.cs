using OverShell.Core.Agents;
using OverShell.Core.Git;
using OverShell.Core.Herd;
using OverShell.Core.Layout;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

public class LayoutTests
{
    [Fact]
    public void Presets_load_cleanly_and_cover_every_documented_name()
    {
        var catalog = LayoutCatalog.LoadDefaults();
        Assert.Empty(catalog.Problems);
        foreach (var name in new[] { "top", "bottom", "left-rail", "left-list", "right-list", "zen", "herd", "dashboard" })
        {
            Assert.NotNull(catalog.Find(name));
        }
    }

    [Fact]
    public void Presets_have_the_shape_the_chrome_expects()
    {
        var catalog = LayoutCatalog.LoadDefaults();

        var top = catalog.Find("top")!;
        Assert.True(top.TabsInCaption);
        Assert.Equal(TabsStyle.Strip, top.Tabs.EffectiveStyle);
        Assert.True(top.Status.Visible);

        var rail = catalog.Find("left-rail")!;
        Assert.Equal(TabsPlacement.Left, rail.Tabs.Placement);
        Assert.Equal(TabsStyle.Rail, rail.Tabs.EffectiveStyle);
        Assert.False(rail.TabsInCaption);

        var right = catalog.Find("right-list")!;
        Assert.Equal(TabsStyle.List, right.Tabs.EffectiveStyle);
        Assert.Equal(240, right.Tabs.Width);

        var zen = catalog.Find("zen")!;
        Assert.Equal(TabsPlacement.Hidden, zen.Tabs.Placement);
        Assert.False(zen.Status.Visible);

        var herd = catalog.Find("herd")!;
        Assert.Equal(SidePlacement.Right, herd.Sidebar.Placement);
        Assert.Equal(320, herd.Sidebar.Width);
    }

    [Fact]
    public void Side_placement_defaults_to_a_list_when_no_style_is_given()
    {
        var tabs = new TabsRegion { Placement = TabsPlacement.Right };
        Assert.Equal(TabsStyle.List, tabs.EffectiveStyle);
        Assert.Equal(TabsStyle.Strip, new TabsRegion { Placement = TabsPlacement.Bottom }.EffectiveStyle);
    }

    [Fact]
    public void User_layout_files_replace_presets_and_unknown_names_resolve_to_the_default()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-layouts");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "top.jsonc"), """{ "tabs": { "placement": "bottom" } }""");
            File.WriteAllText(Path.Combine(dir.FullName, "mine.jsonc"), """{ "description": "custom", "tabs": { "placement": "left", "width": 300 }, "sidebar": { "placement": "left", "width": 260 } }""");
            File.WriteAllText(Path.Combine(dir.FullName, "broken.jsonc"), "{ nope");

            var catalog = LayoutCatalog.Load(dir.FullName);
            Assert.Single(catalog.Problems);
            Assert.Equal(TabsPlacement.Bottom, catalog.Find("top")!.Tabs.Placement);
            Assert.Equal(300, catalog.Find("mine")!.Tabs.Width);
            Assert.Equal(SidePlacement.Left, catalog.Find("mine")!.Sidebar.Placement);
            Assert.Equal("top", catalog.Resolve("does-not-exist").Name);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Settings_define_the_four_views()
    {
        var s = AppSettings.LoadDefaults();
        Assert.Empty(s.Problems);
        Assert.Equal(AppSettings.ViewOrder, s.Views.Keys.OrderBy(k => Array.IndexOf(AppSettings.ViewOrder, k)).ToArray());
        Assert.Equal(ViewContent.Dashboard, s.ViewFor("dashboard").Content);
        Assert.Equal("herd", s.ViewFor("herd").Layout);
        Assert.Equal(ViewContent.Terminal, s.ViewFor("nope").Content);
        Assert.True(s.Git.Branch);
        Assert.False(s.Git.Dirty);
    }
}

public class HerdOrderingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static HerdEntry Entry(string id, string project, AgentState state, bool unread = false, int secondsAgo = 0, int index = 0, bool agent = true) =>
        new(id, project, state, unread, agent, T0.AddSeconds(-secondsAgo), index);

    [Fact]
    public void Groups_by_project_with_attention_first_then_recency()
    {
        var groups = HerdOrdering.Group(
        [
            Entry("a", "alpha", AgentState.Working, secondsAgo: 5, index: 0),
            Entry("b", "beta", AgentState.Idle, secondsAgo: 1, index: 1),
            Entry("c", "alpha", AgentState.Blocked, secondsAgo: 60, index: 2),
            Entry("d", "beta", AgentState.Done, unread: true, secondsAgo: 30, index: 3),
            Entry("e", "gamma", AgentState.Unknown, secondsAgo: 0, index: 4, agent: false),
        ]);

        Assert.Equal(["alpha", "beta", "gamma"], groups.Select(g => g.Project).ToArray());

        var alpha = groups[0];
        Assert.Equal(AgentState.Blocked, alpha.Rollup);
        Assert.Equal(1, alpha.NeedingAttention);
        Assert.Equal(["c", "a"], alpha.Tabs.Select(t => t.TabId).ToArray()); // blocked before working, despite being older

        var beta = groups[1];
        Assert.Equal(AgentState.Done, beta.Rollup);
        Assert.Equal(["d", "b"], beta.Tabs.Select(t => t.TabId).ToArray());   // unseen done before idle

        Assert.Equal(0, groups[2].NeedingAttention);
    }

    [Fact]
    public void Within_equal_attention_the_most_recent_comes_first_then_tab_order()
    {
        var groups = HerdOrdering.Group(
        [
            Entry("old", "p", AgentState.Working, secondsAgo: 50, index: 0),
            Entry("new", "p", AgentState.Working, secondsAgo: 1, index: 1),
            Entry("same1", "p", AgentState.Idle, secondsAgo: 10, index: 2),
            Entry("same2", "p", AgentState.Idle, secondsAgo: 10, index: 3),
        ]);

        Assert.Equal(["new", "old", "same1", "same2"], groups[0].Tabs.Select(t => t.TabId).ToArray());
    }

    [Fact]
    public void A_seen_done_is_ordinary_and_exited_sinks()
    {
        Assert.Equal(HerdOrdering.AttentionRank(AgentState.Idle, false), HerdOrdering.AttentionRank(AgentState.Done, false));
        Assert.True(HerdOrdering.AttentionRank(AgentState.Exited, false) > HerdOrdering.AttentionRank(AgentState.Unknown, false));
        Assert.True(HerdOrdering.NeedsAttention(AgentState.Error, false));
        Assert.False(HerdOrdering.NeedsAttention(AgentState.Working, false));
    }
}

public class GitRepositoryTests
{
    [Fact]
    public void Finds_the_root_and_reads_the_branch_from_head()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-git");
        try
        {
            var git = Directory.CreateDirectory(Path.Combine(dir.FullName, ".git"));
            File.WriteAllText(Path.Combine(git.FullName, "HEAD"), "ref: refs/heads/feature/herd\n");
            var nested = Directory.CreateDirectory(Path.Combine(dir.FullName, "src", "deep"));

            Assert.Equal(dir.FullName, GitRepository.FindRoot(nested.FullName));
            Assert.Equal("feature/herd", GitRepository.ReadBranch(dir.FullName));
            Assert.Equal(Path.Combine(git.FullName, "HEAD"), GitRepository.HeadFile(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Detached_head_gives_a_short_commit_and_worktree_files_are_followed()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-git");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(dir.FullName, "real.git"));
            File.WriteAllText(Path.Combine(real.FullName, "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");
            var worktree = Directory.CreateDirectory(Path.Combine(dir.FullName, "wt"));
            File.WriteAllText(Path.Combine(worktree.FullName, ".git"), $"gitdir: {real.FullName}\n");

            Assert.Equal(worktree.FullName, GitRepository.FindRoot(worktree.FullName));
            Assert.Equal("0123456", GitRepository.ReadBranch(worktree.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Outside_a_repository_there_is_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-nogit");
        try
        {
            Assert.Null(GitRepository.FindRoot(null));
            Assert.Null(GitRepository.FindRoot(string.Empty));

            // The temp directory itself may sit under a repository on a developer box, so
            // only the branch read is asserted: this directory has no .git of its own.
            Assert.Null(GitRepository.ReadBranch(dir.FullName));
            Assert.Null(GitRepository.GitDirectory(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
