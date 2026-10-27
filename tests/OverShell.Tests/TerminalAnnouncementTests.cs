using OverShell.App.Terminal;
using Xunit;

namespace OverShell.Tests;

public class TerminalAnnouncementTests
{
    [Fact]
    public void Old_announcements_are_not_replayed_over_new_metadata()
    {
        var observer = new TerminalAnnouncements();
        Assert.Equal(@"C:\old", observer.Observe("\u001b]9;9;C:\\old\a").Directory);
        Assert.Null(observer.Observe("ordinary output after a session move").Directory);
        Assert.Equal("New title", observer.Observe("\u001b]0;New title\a").Title);
        Assert.Null(observer.Observe("more output").Title);
    }

    [Fact]
    public void Latest_directory_wins_across_osc_formats()
    {
        var observer = new TerminalAnnouncements();
        Assert.Equal(@"C:\new folder", observer.Observe("\u001b]9;9;C:\\old\a\u001b]7;file:///C:/new%20folder\a").Directory);
        Assert.Equal(@"C:\latest", observer.Observe("\u001b]7;file:///C:/other\a\u001b]9;9;\"C:\\latest\"\a").Directory);
    }

    [Fact]
    public void Split_titles_and_announcements_before_large_output_are_kept()
    {
        var observer = new TerminalAnnouncements();
        Assert.Null(observer.Observe("\u001b]2;Split").Title);
        Assert.Equal("Split title", observer.Observe(" title\u001b\\").Title);
        Assert.Equal("Large output title", observer.Observe("\u001b]0;Large output title\a" + new string('x', 16000)).Title);
        Assert.Null(observer.Observe("x").Title);
    }
}
