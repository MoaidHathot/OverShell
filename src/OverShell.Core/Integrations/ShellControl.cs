using System.Text.Json.Nodes;
using OverShell.Core.Agents;
using OverShell.Core.Extensibility;

namespace OverShell.Core.Integrations;

public enum ControlStatus
{
    Ok,
    NotFound,
    Refused,
    BadRequest,
}

/// <summary>What a control call came to: Ok, or why not - the HTTP layer picks the status code.</summary>
public sealed record ControlResult(ControlStatus Status, string? Message = null)
{
    public static readonly ControlResult Ok = new(ControlStatus.Ok);

    public static ControlResult NotFound(string key) => new(ControlStatus.NotFound, $"no tab '{key}'");

    public static ControlResult Refused(string why) => new(ControlStatus.Refused, why);

    public static ControlResult BadRequest(string why) => new(ControlStatus.BadRequest, why);
}

/// <summary>
/// The control API's meaning (DESIGN.md §12.18), on the extensibility surface alone: list and
/// describe tabs, read a screen, send text, answer or reply, open a tab, wait for a state,
/// close. The HTTP routes and the MCP tools are two spellings of this one class, and anything
/// an extension can do in-process is what a caller gets over the wire - no more.
/// <para>
/// Everything touches the UI through <see cref="IShell.Post"/>, so callers can live on any
/// thread. A tab is named by its id or, when that is unique, its label.
/// </para>
/// </summary>
public sealed class ShellControl
{
    /// <summary>Longest a wait call is held; a caller that wants longer calls again.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(120);

    /// <summary>How long a screen read gives the terminal to answer a fresh request before the rows are taken as they are.</summary>
    private static readonly TimeSpan ScreenSettle = TimeSpan.FromMilliseconds(350);

    private readonly IShell _shell;

    public ShellControl(IShell shell)
    {
        _shell = shell;
    }

    /// <summary>A tab by id, else by a label only one tab carries (case-insensitive); null when neither.</summary>
    public static ITab? Resolve(IShell shell, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (shell.Find(key) is { } byId)
        {
            return byId;
        }

        var byLabel = shell.Tabs.Where(t => string.Equals(t.Label, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return byLabel.Count == 1 ? byLabel[0] : null;
    }

    /// <summary>A tab as the API describes it: identity, state and the ways back in.</summary>
    public static JsonObject Describe(ITab t) => new()
    {
        ["id"] = t.Id,
        ["label"] = t.Label,
        ["title"] = t.Title,
        ["harness"] = t.Harness,
        ["isAgent"] = t.IsAgent,
        ["state"] = t.State.ToString(),
        ["unread"] = t.Unread,
        ["needsAttention"] = t.NeedsAttention,
        ["attentionSince"] = t.AttentionSince?.ToString("o"),
        ["lastActivity"] = t.LastActivity?.ToString("o"),
        ["cwd"] = t.WorkingDirectory,
        ["project"] = t.Project,
        ["group"] = t.Group,
        ["isRunning"] = t.IsRunning,
        ["isActive"] = t.IsActive,
        ["explain"] = t.Explain,
        ["summary"] = t.Summary,
        ["sessionId"] = t.SessionId,
        ["replyChannel"] = t.ReplyChannel.ToString(),
        ["request"] = t.OpenRequest is { } r
            ? new JsonObject { ["id"] = r.Id, ["kind"] = r.Kind, ["title"] = r.Title, ["at"] = r.At.ToString("o") }
            : null,
    };

    public Task<JsonArray> ListAsync() => OnUiAsync(() =>
    {
        var array = new JsonArray();
        foreach (var tab in _shell.Tabs)
        {
            array.Add(Describe(tab));
        }

        return array;
    });

    public Task<JsonObject?> DescribeAsync(string key) => OnUiAsync(() => Resolve(_shell, key) is { } t ? Describe(t) : null);

    /// <summary>The viewport, oldest row first, after asking the terminal for a fresh copy.</summary>
    public async Task<JsonObject?> ScreenAsync(string key)
    {
        var requested = await OnUiAsync(() =>
        {
            var tab = Resolve(_shell, key);
            tab?.RequestScreen();
            return tab is not null;
        }).ConfigureAwait(false);
        if (!requested)
        {
            return null;
        }

        await Task.Delay(ScreenSettle).ConfigureAwait(false);
        return await OnUiAsync(() =>
        {
            if (Resolve(_shell, key) is not { } tab)
            {
                return null;
            }

            var rows = new JsonArray();
            foreach (var row in tab.ScreenRows)
            {
                rows.Add(row.TrimEnd());
            }

            return new JsonObject
            {
                ["id"] = tab.Id,
                ["state"] = tab.State.ToString(),
                ["rows"] = rows,
                ["text"] = string.Join('\n', tab.ScreenRows.Select(r => r.TrimEnd())).TrimEnd('\n'),
            };
        }).ConfigureAwait(false);
    }

    /// <summary>Types <paramref name="text"/> into the tab, with Enter when asked; the text itself goes through the paste path so brackets and newlines arrive whole.</summary>
    public Task<ControlResult> SendAsync(string key, string text, bool enter) => OnUiAsync(() =>
    {
        if (Resolve(_shell, key) is not { } tab)
        {
            return ControlResult.NotFound(key);
        }

        if (!tab.IsRunning)
        {
            return ControlResult.Refused("the tab's process has exited");
        }

        if (text.Length > 0)
        {
            tab.Paste(text);
        }

        if (enter)
        {
            tab.SendText("\r");
        }

        _shell.Trace($"[{tab.Id}] control: sent {text.Length} char(s){(enter ? " + Enter" : string.Empty)}");
        return ControlResult.Ok;
    });

    /// <summary>
    /// Approve or deny the open request (<paramref name="answer"/> = approve | deny), else free
    /// text as the reply - each the way <see cref="ITab.Answer"/> / <see cref="ITab.Reply"/> do it
    /// (§12.17): the integration's own API when it listens, the rule file's keys, typed text.
    /// </summary>
    public Task<ControlResult> ReplyAsync(string key, string? answer, string? text) => OnUiAsync(() =>
    {
        if (Resolve(_shell, key) is not { } tab)
        {
            return ControlResult.NotFound(key);
        }

        if (!tab.IsAgent || !tab.IsRunning)
        {
            return ControlResult.Refused("the tab is not running an agent");
        }

        if (answer is not null)
        {
            var approve = answer.Equals("approve", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase) || answer.Equals("y", StringComparison.OrdinalIgnoreCase);
            var deny = answer.Equals("deny", StringComparison.OrdinalIgnoreCase) || answer.Equals("no", StringComparison.OrdinalIgnoreCase) || answer.Equals("n", StringComparison.OrdinalIgnoreCase);
            if (!approve && !deny)
            {
                return ControlResult.BadRequest("\"answer\" is approve or deny");
            }

            if (!tab.Answer(approve))
            {
                return ControlResult.Refused($"{tab.Label} has no yes/no channel (reply channel {tab.ReplyChannel}); send text instead");
            }

            _shell.Trace($"[{tab.Id}] control: {(approve ? "approved" : "denied")} via {tab.ReplyChannel}");
            return ControlResult.Ok;
        }

        if (string.IsNullOrEmpty(text))
        {
            return ControlResult.BadRequest("\"answer\" (approve | deny) or \"text\" is required");
        }

        tab.Reply(text);
        _shell.Trace($"[{tab.Id}] control: replied via {tab.ReplyChannel}");
        return ControlResult.Ok;
    });

    /// <summary>
    /// Opens a tab from a request body: <c>profile</c>, <c>cwd</c>, <c>label</c>, <c>group</c>,
    /// <c>command</c> (typed at the shell prompt), <c>prompt</c> (the agent's first prompt),
    /// <c>activate</c>; or <c>harness</c> (opencode, copilot, ...) for the rule file's launch
    /// command. Null when no profile could be launched.
    /// </summary>
    public Task<(JsonObject? Tab, ControlResult Result)> OpenAsync(JsonObject body) => OnUiAsync(() =>
    {
        var command = Str(body, "command");
        if (Str(body, "harness") is { Length: > 0 } harness)
        {
            var rules = _shell.AgentRules.FirstOrDefault(r => r.Id.Equals(harness, StringComparison.OrdinalIgnoreCase));
            if (rules is null)
            {
                return ((JsonObject?)null, ControlResult.BadRequest($"no agent rules for harness '{harness}'; known: {string.Join(", ", _shell.AgentRules.Select(r => r.Id))}"));
            }

            if (string.IsNullOrWhiteSpace(rules.Launch))
            {
                return (null, ControlResult.Refused($"the rules for '{harness}' name no launch command"));
            }

            command = string.IsNullOrWhiteSpace(command) ? rules.Launch : $"{rules.Launch} {command}";
        }

        var request = new TabRequest(
            Profile: Str(body, "profile"),
            WorkingDirectory: Str(body, "cwd"),
            Label: Str(body, "label"),
            Group: Str(body, "group"),
            Command: command,
            Activate: Bool(body, "activate") ?? false,
            Prompt: Str(body, "prompt"));
        var tab = _shell.OpenTab(request);
        if (tab is null)
        {
            return (null, ControlResult.Refused("no launchable profile"));
        }

        _shell.Trace($"[{tab.Id}] control: opened{(command is null ? string.Empty : $" with '{command}'")}{(request.Prompt is null ? string.Empty : " and a first prompt")}");
        return (Describe(tab), ControlResult.Ok);
    });

    /// <summary>
    /// Holds until the tab's state is one of <paramref name="states"/> (default: anything but
    /// Working and Unknown - the agent stopped for some reason), the tab closes, or
    /// <paramref name="timeout"/> passes. Polls the UI thread four times a second; the answer
    /// says which of the three it was.
    /// </summary>
    public async Task<JsonObject> WaitAsync(string key, IReadOnlyCollection<AgentState>? states, TimeSpan timeout, CancellationToken cancellation)
    {
        var wanted = states is { Count: > 0 }
            ? new HashSet<AgentState>(states)
            : [AgentState.Idle, AgentState.Done, AgentState.Blocked, AgentState.Error, AgentState.Exited];
        if (timeout > MaxWait)
        {
            timeout = MaxWait;
        }

        var started = DateTimeOffset.UtcNow;
        var deadline = started + timeout;
        while (true)
        {
            var (found, id, state) = await OnUiAsync(() =>
            {
                var tab = Resolve(_shell, key);
                return tab is null ? (false, (string?)null, AgentState.Unknown) : (true, tab.Id, tab.State);
            }).ConfigureAwait(false);

            if (!found)
            {
                return new JsonObject { ["id"] = id ?? key, ["state"] = "Closed", ["closed"] = true, ["timedOut"] = false, ["waitedMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds };
            }

            if (wanted.Contains(state))
            {
                return new JsonObject { ["id"] = id, ["state"] = state.ToString(), ["closed"] = false, ["timedOut"] = false, ["waitedMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds };
            }

            if (DateTimeOffset.UtcNow >= deadline || cancellation.IsCancellationRequested)
            {
                return new JsonObject { ["id"] = id, ["state"] = state.ToString(), ["closed"] = false, ["timedOut"] = true, ["waitedMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds };
            }

            try
            {
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Reported on the next pass as a timeout rather than thrown at the HTTP layer.
            }
        }
    }

    /// <summary>Closes the tab - never the last one: that closes the window, and a remote caller does not get to do that.</summary>
    public Task<ControlResult> CloseAsync(string key) => OnUiAsync(() =>
    {
        if (Resolve(_shell, key) is not { } tab)
        {
            return ControlResult.NotFound(key);
        }

        if (_shell.Tabs.Count <= 1)
        {
            return ControlResult.Refused("not the last tab: closing it would close the window");
        }

        _shell.Trace($"[{tab.Id}] control: closed");
        _shell.Close(tab);
        return ControlResult.Ok;
    });

    /// <summary>The states a comma-separated list names; unknown names are ignored, so "idle,done,blocked" and "Idle, Done" both work.</summary>
    public static IReadOnlyCollection<AgentState> ParseStates(string? list)
    {
        var states = new List<AgentState>();
        foreach (var name in (list ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<AgentState>(name, ignoreCase: true, out var state))
            {
                states.Add(state);
            }
        }

        return states;
    }

    /// <summary>A string field, or the text of a number or boolean; null when absent or not a value.</summary>
    public static string? Str(JsonObject? body, string name) =>
        body?[name] is JsonValue value ? value.ToString() : null;

    /// <summary>A boolean field, taking "true"/"false" strings as well; null when absent or unreadable.</summary>
    public static bool? Bool(JsonObject? body, string name)
    {
        if (body?[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return bool.TryParse(value.ToString(), out flag) ? flag : null;
    }

    private Task<T> OnUiAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shell.Post(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        });
        return completion.Task;
    }
}
