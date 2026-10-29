using System.Text.Json.Nodes;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class TabCreationTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\space dir\\", "\"C:\\space dir\\\\\"")]
    public void Windows_process_arguments_preserve_empty_quotes_spaces_and_trailing_slashes(string value, string expected) =>
        Assert.Equal(expected, ProcessLaunch.QuoteArgument(value));

    [Theory]
    [InlineData("{\"process\":{\"executable\":\"pwsh.exe\",\"arguments\":[3]}}")]
    [InlineData("{\"process\":{\"executable\":\"pwsh.exe\"},\"command\":\"dir\"}")]
    [InlineData("{\"presentation\":\"unknown\"}")]
    [InlineData("{\"scratch\":\"unknown\"}")]
    [InlineData("{\"onExit\":\"unknown\"}")]
    [InlineData("{\"onExit\":99}")]
    public void Invalid_launch_requests_are_rejected_before_a_process_is_created(string json)
    {
        Assert.Null(TabRequestParser.Parse(JsonNode.Parse(json)!.AsObject(), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Typed_creation_carries_independent_address_presentation_and_lifetime_options()
    {
        var request = TabRequestParser.Parse(JsonNode.Parse("""
            {"profile":"PowerShell","alias":"build","scratch":true,"presentation":"floating","activate":false,
             "restore":false,"history":true,"onExit":"closeOnSuccess","process":{"executable":"dotnet.exe","arguments":["build","path with space"]}}
            """)!.AsObject(), out var error);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.True(request.Floating && request.Scratch);
        Assert.False(request.Activate);
        Assert.False(request.Restore);
        Assert.True(request.History);
        Assert.Equal(TabExitBehavior.CloseOnSuccess, request.OnExit);
        Assert.Equal("build", request.Alias);
        Assert.Equal(["build", "path with space"], request.Process!.Arguments);
    }

    [Theory]
    [InlineData(TabExitBehavior.Keep, 0, false)]
    [InlineData(TabExitBehavior.CloseOnSuccess, 0, true)]
    [InlineData(TabExitBehavior.CloseOnSuccess, 7, false)]
    [InlineData(TabExitBehavior.CloseOnSuccess, null, false)]
    [InlineData(TabExitBehavior.CloseAlways, 7, true)]
    [InlineData(TabExitBehavior.CloseAlways, null, true)]
    public void Exit_policy_uses_real_exit_results_including_launch_failure(TabExitBehavior behavior, int? code, bool closes) =>
        Assert.Equal(closes, new TabLifetime(OnExit: behavior).ShouldClose(code));
}
