using System.Windows;
using OverShell.App.Chrome;
using Xunit;

namespace OverShell.Tests;

public class PreviewPlacementTests
{
    [Fact]
    public void Hover_uses_the_space_beside_the_card_when_available()
    {
        var result = TerminalPreviewWindow.PlaceWithin(new Rect(0, 0, 1920, 1040), new Rect(0, 0, 1600, 900), new Rect(20, 100, 400, 300), new Size(900, 600));
        Assert.Equal(new Rect(432, 100, 900, 600), result);
    }

    [Fact]
    public void Hover_can_open_to_the_left_on_a_negative_coordinate_monitor()
    {
        var work = new Rect(-1920, -200, 1920, 1040);
        var result = TerminalPreviewWindow.PlaceWithin(work, work, new Rect(-420, 100, 400, 300), new Size(900, 600));
        Assert.True(work.Contains(result));
        Assert.Equal(-1332, result.Left);
    }

    [Fact]
    public void Oversized_previews_fit_the_monitor_work_area()
    {
        var work = new Rect(2000, 100, 640, 440);
        var result = TerminalPreviewWindow.PlaceWithin(work, new Rect(1800, 0, 1200, 900), null, new Size(1350, 900));
        Assert.Equal(work, result);
    }

    [Fact]
    public void Keyboard_preview_centres_on_owner_and_clamps_to_work_area()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var result = TerminalPreviewWindow.PlaceWithin(work, new Rect(1700, 900, 800, 600), null, new Size(900, 600));
        Assert.Equal(new Rect(1020, 440, 900, 600), result);
    }
}
