using OverShell.Core.Agents;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class TabDirectoryTests
{
    [Fact]
    public void Session_location_outlives_shell_announcements_and_tool_probes_until_release()
    {
        var directory = new TabDirectory(@"C:\shell");
        directory.Report("ui", "one", @"C:\session");
        directory.Announce(@"C:\new-shell");
        Assert.False(directory.Probe(@"C:\tool", false, directory.Revision));
        Assert.Equal(@"C:\session", directory.Current);
        directory.Release("another-owner");
        Assert.Equal(@"C:\session", directory.Current);
        directory.Release("UI");
        Assert.Equal(@"C:\new-shell", directory.Current);
        Assert.False(directory.Probe(@"C:\stale-root", true, directory.Revision));
        Assert.True(directory.Probe(@"C:\nested-shell", false, directory.Revision));
    }

    [Fact]
    public void Late_probes_cannot_undo_newer_observations_even_if_the_location_text_is_unchanged()
    {
        var directory = new TabDirectory(@"C:\start");
        var revision = directory.Revision;
        directory.Announce(@"C:\start");
        Assert.False(directory.Probe(@"C:\old", false, revision));
        revision = directory.Revision;
        directory.Report("ui", "one", @"C:\start");
        directory.Release();
        Assert.False(directory.Probe(@"C:\old", false, revision));
        Assert.Equal(@"C:\start", directory.Current);
    }

    [Fact]
    public void New_sessions_and_sources_never_inherit_the_previous_sessions_location()
    {
        var directory = new TabDirectory(@"C:\shell");
        directory.Report("ui", "one", @"C:\first");
        directory.Report("ui", "one", null);
        Assert.Equal(@"C:\first", directory.Current);
        directory.Report("ui", "two", null);
        Assert.Equal(@"C:\shell", directory.Current);
        Assert.True(directory.Probe(@"C:\probe", true, directory.Revision));
        directory.Report("ui", "two", @"C:\second");
        directory.Report("other-ui", "two", null);
        Assert.Equal(@"C:\probe", directory.Current);
    }

    [Fact]
    public void Empty_announcements_do_not_disable_the_fallback_probe()
    {
        var directory = new TabDirectory(@"C:\shell");
        directory.Announce(" ");
        Assert.False(directory.AnnouncesDirectory);
        Assert.True(directory.Probe(@"C:\real", true, directory.Revision));
    }

    [Fact]
    public void Agent_tools_and_shared_service_children_are_not_foreground_nested_shells()
    {
        Assert.Equal((100u, "pwsh", CwdSource.Prompt), ShellCwdTarget.Pick(100, "pwsh",
            [(101, 100, "opencode"), (102, 101, "cmd"), (103, 102, "pwsh")]));
        Assert.Equal((100u, "opencode", CwdSource.Process), ShellCwdTarget.Pick(100, "opencode",
            [(101, 100, "cmd"), (102, 101, "bash")]));
        Assert.Equal((101u, "cmd", CwdSource.Process), ShellCwdTarget.Pick(100, "pwsh",
            [(101, 100, "cmd"), (102, 101, "node"), (103, 102, "bash")]));
    }
}
