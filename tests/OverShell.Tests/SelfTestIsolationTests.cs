using OverShell.App;
using Xunit;

namespace OverShell.Tests;

public class SelfTestIsolationTests
{
    [Fact]
    public void Private_desktop_run_cannot_hand_over_to_the_users_running_shell()
    {
        var normal = SingleInstance.PipeNameFor("domain\\user", 1, null);
        Assert.NotEqual(normal, SingleInstance.PipeNameFor("domain\\user", 1, "test-one"));
        Assert.NotEqual(SingleInstance.PipeNameFor("domain\\user", 1, "test-one"), SingleInstance.PipeNameFor("domain\\user", 1, "test-two"));
    }

    [Fact]
    public void Child_handoffs_share_the_test_identity_and_normal_users_keep_their_identity()
    {
        Assert.Equal(SingleInstance.PipeNameFor("DOMAIN\\USER", 1, "test"), SingleInstance.PipeNameFor("domain\\user", 1, "test"));
        Assert.Equal(SingleInstance.PipeNameFor("domain\\user", 1, null), SingleInstance.PipeNameFor("domain\\user", 1, ""));
        Assert.NotEqual(SingleInstance.PipeNameFor("domain\\user", 1, null), SingleInstance.PipeNameFor("domain\\user", 2, null));
    }
}
