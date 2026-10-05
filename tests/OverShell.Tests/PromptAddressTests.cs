using OverShell.Core.Herd;
using Xunit;

namespace OverShell.Tests;

public class PromptAddressTests
{
    private static readonly IReadOnlyList<AddressableTab> Tabs =
    [
        new("a", 0, "api", "backend", "Working", true, true),
        new("b", 1, "web", "frontend", "Blocked", true, true),
        new("c", 2, "worker", "backend", "Idle", true, true),
        new("d", 3, "PowerShell", null, "Unknown", false, true),
        new("e", 4, "dead", null, "Exited", true, false),
    ];

    [Fact]
    public void No_address_means_the_active_tab_and_the_whole_text()
    {
        var route = PromptAddress.Parse("fix the tests", Tabs, "c");
        Assert.False(route.Addressed);
        Assert.Equal("fix the tests", route.Body);
        Assert.Equal(["c"], route.Targets.Select(t => t.Id));
        Assert.Equal(string.Empty, PromptAddress.Describe(route));
    }

    [Theory]
    [InlineData("@2 go", new[] { "b" }, "go")]
    [InlineData("@api go", new[] { "a" }, "go")]
    [InlineData("@API go", new[] { "a" }, "go")]
    [InlineData("@wo go", new[] { "c" }, "go")]
    [InlineData("@w go", new[] { "b", "c" }, "go")]
    [InlineData("#backend go", new[] { "a", "c" }, "go")]
    [InlineData("#back go", new[] { "a", "c" }, "go")]
    [InlineData("@blocked answer: yes", new[] { "b" }, "answer: yes")]
    [InlineData("@agents hello", new[] { "a", "b", "c" }, "hello")]
    [InlineData("@all hello", new[] { "a", "b", "c", "d" }, "hello")]
    [InlineData("@active hi", new[] { "c" }, "hi")]
    [InlineData("@1 @2 both", new[] { "a", "b" }, "both")]
    [InlineData("@2 #backend   spaced  body", new[] { "a", "b", "c" }, "spaced  body")]
    public void Address_words_choose_tabs_and_leave_the_body(string text, string[] ids, string body)
    {
        var route = PromptAddress.Parse(text, Tabs, "c");
        Assert.True(route.Addressed);
        Assert.Equal(ids, route.Targets.Select(t => t.Id));
        Assert.Equal(body, route.Body);
        Assert.Empty(route.Unmatched);
    }

    [Fact]
    public void Unknown_words_are_reported_not_guessed()
    {
        var route = PromptAddress.Parse("@nothing @2 text", Tabs, "c");
        Assert.True(route.Addressed);
        Assert.Equal(["b"], route.Targets.Select(t => t.Id));
        Assert.Equal(["@nothing"], route.Unmatched);
        Assert.Equal("\u2192 web  (no match: @nothing)", PromptAddress.Describe(route));

        var none = PromptAddress.Parse("@9 text", Tabs, "c");
        Assert.Empty(none.Targets);
        Assert.Equal("\u2192 nobody  (no match: @9)", PromptAddress.Describe(none));
    }

    [Fact]
    public void A_lone_at_sign_is_text()
    {
        var route = PromptAddress.Parse("@ mention this", Tabs, "a");
        Assert.False(route.Addressed);
        Assert.Equal("@ mention this", route.Body);
    }

    [Fact]
    public void An_exited_tab_is_not_addressed_by_state_or_all_but_is_by_number_or_label()
    {
        Assert.DoesNotContain(PromptAddress.Parse("@all x", Tabs, "a").Targets, t => t.Id == "e");
        Assert.Contains(PromptAddress.Parse("@5 x", Tabs, "a").Targets, t => t.Id == "e");
        Assert.Contains(PromptAddress.Parse("@dead x", Tabs, "a").Targets, t => t.Id == "e");
    }

    [Fact]
    public void Describe_summarises_many_targets()
    {
        var route = PromptAddress.Parse("@all x", Tabs, "a");
        Assert.Equal("\u2192 4 tabs", PromptAddress.Describe(route));
        Assert.Equal("\u2192 api, worker", PromptAddress.Describe(PromptAddress.Parse("#backend x", Tabs, "a")));
    }
}
