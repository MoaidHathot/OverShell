using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using Xunit;

namespace OverShell.Tests;

public class TabCommandQueueTests
{
    [Fact]
    public async Task Poll_returns_what_was_queued_after_the_sequence_seen()
    {
        var queue = new TabCommandQueue();
        var first = queue.Enqueue("t1", "permission", "req-1", "approve");
        var second = queue.Enqueue("t1", "prompt", null, "hello");
        queue.Enqueue("t2", "permission", "req-9", "deny");

        var all = await queue.PollAsync("t1", 0, TimeSpan.Zero);
        Assert.Equal([first.Seq, second.Seq], all.Select(c => c.Seq));
        Assert.Equal("approve", all[0].Response);
        Assert.Equal("req-1", all[0].RequestId);

        // Seen up to the first: only the second remains; what was seen is consumed.
        var rest = await queue.PollAsync("t1", first.Seq, TimeSpan.Zero);
        Assert.Equal([second.Seq], rest.Select(c => c.Seq));
        Assert.Single(queue.Snapshot("t1"));

        var other = await queue.PollAsync("t2", 0, TimeSpan.Zero);
        Assert.Single(other);
        Assert.Equal("deny", other[0].Response);
    }

    [Fact]
    public async Task Poll_with_nothing_waits_until_something_arrives()
    {
        var queue = new TabCommandQueue();
        var poll = queue.PollAsync("t1", 0, TimeSpan.FromSeconds(5));
        Assert.False(poll.IsCompleted);

        await Task.Delay(50);
        queue.Enqueue("t1", "permission", null, "approve");
        var commands = await poll.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(commands);
        Assert.True(queue.IsListening("t1", TimeSpan.FromSeconds(5)));
        Assert.False(queue.IsListening("nobody", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Poll_with_nothing_returns_empty_after_the_hold()
    {
        var queue = new TabCommandQueue();
        var commands = await queue.PollAsync("t1", 0, TimeSpan.FromMilliseconds(80));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Expired_commands_are_dropped()
    {
        var queue = new TabCommandQueue();
        queue.Enqueue("t1", "permission", null, "approve", DateTimeOffset.Now - TabCommandQueue.Expiry - TimeSpan.FromSeconds(1));
        var fresh = queue.Enqueue("t1", "prompt", null, "hi");
        var commands = await queue.PollAsync("t1", 0, TimeSpan.Zero);
        Assert.Equal([fresh.Seq], commands.Select(c => c.Seq));
    }

    [Fact]
    public async Task Forget_drops_the_queue_and_wakes_the_poller()
    {
        var queue = new TabCommandQueue();
        var poll = queue.PollAsync("t1", 0, TimeSpan.FromSeconds(5));
        queue.Forget("t1");
        var commands = await poll.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(commands);
        Assert.Empty(queue.Snapshot("t1"));
    }

    [Fact]
    public void Report_carries_the_request_id()
    {
        var node = Jsonc.Parse("""{ "tab": "t", "source": "opencode", "state": "blocked", "requestId": "per_123", "requestKind": "permission", "message": "Run git push?" }""", out _);
        var report = IntegrationReport.Parse(string.Empty, node, out var error);
        Assert.Null(error);
        Assert.Equal("per_123", report!.RequestId);
        Assert.Equal("permission", report.RequestKind);
        Assert.Equal(AgentState.Blocked, report.State);
    }
}

public class MatchingLineTests
{
    [Fact]
    public void The_last_row_a_blocked_pattern_matches_is_the_question()
    {
        var rules = new StateRules { Blocked = ["Permission required", "\\bAllow\\b[^\\n]{0,40}\\bDeny\\b"], Working = ["Thinking"] };
        var rows = new[]
        {
            "Thinking about the change",
            "",
            "Permission required: run `git push`",
            "  Allow once   Allow always   Deny",
            "",
        };

        Assert.Equal("Allow once   Allow always   Deny", rules.MatchingLine(rows, AgentState.Blocked));
        Assert.Equal("Thinking about the change", rules.MatchingLine(rows, AgentState.Working));
        Assert.Null(rules.MatchingLine(rows, AgentState.Error));
        Assert.Null(rules.MatchingLine(["nothing here"], AgentState.Blocked));
    }

    [Fact]
    public void Bundled_rules_know_their_answers()
    {
        var rules = AgentRules.Load(null);
        var copilot = rules.Find("copilot")!;
        Assert.NotNull(copilot.Answers);
        Assert.False(string.IsNullOrEmpty(copilot.Answers!.Approve));
        Assert.False(string.IsNullOrEmpty(copilot.Answers.Deny));

        // OpenCode's dialog is arrows + Enter with no letter keys; its integration is the exact channel.
        Assert.Null(rules.Find("opencode")!.Answers);
        Assert.Equal("opencode", rules.Find("opencode")!.Launch);
        Assert.Equal("copilot", copilot.Launch);
    }
}
