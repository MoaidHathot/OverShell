using System.Windows;
using OverShell.App.Chrome;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

public class FloatingPlacementTests
{
    [Theory]
    [InlineData("center")]
    [InlineData("topRight")]
    [InlineData("bottomRight")]
    public void Floating_bounds_fit_negative_monitor_coordinates_and_oversized_owners(string position)
    {
        var work = new Rect(-1920, -200, 1920, 1080);
        var placed = FloatingPlacement.Calculate(work, new Rect(-2200, -400, 4000, 2000), new FloatingSettings { Position = position });
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
        Assert.Equal(work, FloatingPlacement.Calculate(work, work, settings));
    }
}
