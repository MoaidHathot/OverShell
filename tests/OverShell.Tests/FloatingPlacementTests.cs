using System.Windows;
using OverShell.App.Chrome;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

public class FloatingPlacementTests
{
    [Fact]
    public void Large_displays_use_a_comfortable_initial_size_and_limits_can_be_removed()
    {
        var size = new Size(4000, 2200);
        var bounded = FloatingPlacement.Calculate(size, new FloatingSettings());
        Assert.Equal(1200, bounded.Width);
        Assert.Equal(800, bounded.Height);
        var unlimited = FloatingPlacement.Calculate(size, new FloatingSettings { MaxWidth = 0, MaxHeight = 0 });
        Assert.Equal(3200, unlimited.Width);
        Assert.Equal(1650, unlimited.Height);
    }

    [Fact]
    public void Background_clips_cover_only_the_region_outside_the_panel()
    {
        var area = new Rect(0, 0, 1000, 600);
        var panel = new Rect(100, 50, 800, 500);
        var regions = FloatingBackdrop.Regions(area, panel);
        Assert.Equal(area.Width * area.Height - panel.Width * panel.Height, regions.Sum(r => r.Width * r.Height));
        Assert.All(regions, r =>
        {
            Assert.True(area.Contains(r));
            var overlap = Rect.Intersect(r, panel);
            Assert.True(overlap.IsEmpty || overlap.Width == 0 || overlap.Height == 0);
        });
    }

    [Theory]
    [InlineData("center")]
    [InlineData("topRight")]
    [InlineData("bottomRight")]
    public void Floating_bounds_fit_inside_the_owner_client_area(string position)
    {
        var work = new Rect(0, 0, 900, 600);
        var placed = FloatingPlacement.Calculate(work.Size, new FloatingSettings { Position = position });
        Assert.True(work.Contains(placed));
        Assert.True(placed.Width > 0 && placed.Height > 0);
    }

    [Fact]
    public void Invalid_ratios_use_bounded_defaults_and_small_work_areas_are_respected()
    {
        var settings = new FloatingSettings { WidthRatio = double.NaN, HeightRatio = double.PositiveInfinity };
        Assert.Equal(0.8, settings.EffectiveWidth);
        Assert.Equal(0.75, settings.EffectiveHeight);
        var work = new Rect(0, 0, 320, 200);
        Assert.Equal(work, FloatingPlacement.Calculate(work.Size, settings));
    }

    [Fact]
    public void Dragging_resizing_and_owner_shrink_cannot_move_a_panel_outside_its_owner()
    {
        var area = new Size(480, 300);
        foreach (var requested in new[] { new Rect(-500, -100, 800, 700), new Rect(1000, 1000, 200, 140), new Rect(0, 0, 1, 1) })
            Assert.True(new Rect(area).Contains(FloatingPlacement.Constrain(requested, area)));
        Assert.True(FloatingPlacement.Calculate(new Size(), new FloatingSettings()).IsEmpty);
    }
}
