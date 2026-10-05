using System.Text.Json.Nodes;

namespace OverShell.Core.Integrations;

/// <summary>
/// Something OverShell wants a harness to do (DESIGN.md §12.17): answer a permission,
/// answer a question, run a prompt. Queued per tab; the harness's integration long-polls
/// for them and executes them through the harness's own API - exact, where typing into a
/// dialog would be a guess.
/// </summary>
/// <param name="Seq">Position in the tab's queue; the poller asks for everything after the last it saw.</param>
/// <param name="Kind"><c>permission</c>, <c>question</c> or <c>prompt</c>.</param>
/// <param name="RequestId">The permission's or question's id, as the harness reported it.</param>
/// <param name="Response">For a permission: <c>once</c>, <c>always</c>, <c>reject</c>; for a question: the answer text; for a prompt: the text.</param>
public sealed record TabCommand(long Seq, string Kind, string? RequestId, string Response, DateTimeOffset At)
{
    public JsonObject ToJson() => new()
    {
        ["seq"] = Seq,
        ["kind"] = Kind,
        ["requestId"] = RequestId,
        ["response"] = Response,
        ["at"] = At.ToString("O"),
    };
}

/// <summary>
/// Per-tab queues of commands for the integrations, with a long-poll: a poller that finds
/// nothing waits until something arrives or the hold expires. Thread-safe; the endpoint's
/// listener threads and the UI thread both touch it.
/// </summary>
public sealed class TabCommandQueue
{
    /// <summary>A command nobody has fetched within this time is dropped: the harness is gone, and a stale "approve" must not land on a later question.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<TabCommand>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastPolled = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;

    /// <summary>Queues a command for a tab and wakes its poller.</summary>
    public TabCommand Enqueue(string tabId, string kind, string? requestId, string response, DateTimeOffset? at = null)
    {
        TaskCompletionSource<bool>? waiter;
        TabCommand command;
        lock (_gate)
        {
            command = new TabCommand(++_seq, kind, requestId, response, at ?? DateTimeOffset.Now);
            if (!_queues.TryGetValue(tabId, out var queue))
            {
                queue = [];
                _queues[tabId] = queue;
            }

            queue.Add(command);
            _waiters.Remove(tabId, out waiter);
        }

        waiter?.TrySetResult(true);
        return command;
    }

    /// <summary>
    /// The commands for a tab after <paramref name="after"/>, waiting up to <paramref name="hold"/>
    /// for one to arrive. Expired commands are dropped on the way.
    /// </summary>
    public async Task<IReadOnlyList<TabCommand>> PollAsync(string tabId, long after, TimeSpan hold, CancellationToken cancellation = default)
    {
        var deadline = DateTimeOffset.Now + hold;
        while (true)
        {
            Task<bool> waitTask;
            lock (_gate)
            {
                _lastPolled[tabId] = DateTimeOffset.Now;
                var pending = Pending(tabId, after);
                if (pending.Count > 0)
                {
                    return pending;
                }

                if (DateTimeOffset.Now >= deadline)
                {
                    return [];
                }

                if (!_waiters.TryGetValue(tabId, out var waiter))
                {
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[tabId] = waiter;
                }

                waitTask = waiter.Task;
            }

            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
            {
                return [];
            }

            var finished = await Task.WhenAny(waitTask, Task.Delay(remaining, cancellation)).ConfigureAwait(false);
            if (finished != waitTask)
            {
                lock (_gate)
                {
                    return Pending(tabId, after);
                }
            }

            // Woken with "false": the tab was forgotten - answer empty now rather than wait out the hold.
            if (!((Task<bool>)waitTask).Result)
            {
                return [];
            }
        }
    }

    /// <summary>Whether something has polled this tab's queue recently - the sign that a reply channel is live.</summary>
    public bool IsListening(string tabId, TimeSpan within)
    {
        lock (_gate)
        {
            return _lastPolled.TryGetValue(tabId, out var at) && DateTimeOffset.Now - at <= within;
        }
    }

    /// <summary>Drops a tab's queue (the tab closed).</summary>
    public void Forget(string tabId)
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            _queues.Remove(tabId);
            _lastPolled.Remove(tabId);
            _waiters.Remove(tabId, out waiter);
        }

        waiter?.TrySetResult(false);
    }

    /// <summary>What is queued for a tab right now, for diagnostics.</summary>
    public IReadOnlyList<TabCommand> Snapshot(string tabId)
    {
        lock (_gate)
        {
            return _queues.TryGetValue(tabId, out var queue) ? queue.ToList() : [];
        }
    }

    private IReadOnlyList<TabCommand> Pending(string tabId, long after)
    {
        if (!_queues.TryGetValue(tabId, out var queue))
        {
            return [];
        }

        var now = DateTimeOffset.Now;
        queue.RemoveAll(c => now - c.At > Expiry);

        // Everything the poller has already seen is consumed: the queue only grows while nobody listens.
        queue.RemoveAll(c => c.Seq <= after);
        return queue.Where(c => c.Seq > after).ToList();
    }
}
