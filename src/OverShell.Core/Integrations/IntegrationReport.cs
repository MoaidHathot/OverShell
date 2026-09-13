using System.Text.Json;
using System.Text.Json.Nodes;
using OverShell.Core.Agents;

namespace OverShell.Core.Integrations;

/// <summary>What a harness integration tells OverShell about one tab.</summary>
public sealed record IntegrationReport(
    string TabId,
    string Source,
    long? Seq,
    AgentState State,
    string? Harness,
    string? Message,
    string? Summary,
    string? SessionId,
    string? ResumeCommand,
    bool Release)
{
    /// <summary>The wire shape of <c>POST /v1/report</c>.</summary>
    public static IntegrationReport? Parse(string tabId, JsonNode? body, out string? error)
    {
        error = null;
        if (body is not JsonObject o)
        {
            error = "body must be a JSON object";
            return null;
        }

        var tab = Str(o, "tab") ?? tabId;
        var source = Str(o, "source");
        if (string.IsNullOrWhiteSpace(tab) || string.IsNullOrWhiteSpace(source))
        {
            error = "\"tab\" and \"source\" are required";
            return null;
        }

        var stateText = Str(o, "state") ?? "unknown";
        if (!TryParseState(stateText, out var state))
        {
            error = $"unknown state '{stateText}'";
            return null;
        }

        long? seq = o["seq"] is JsonValue sv && sv.TryGetValue<long>(out var s) ? s : null;
        var session = o["session"] as JsonObject;

        return new IntegrationReport(
            tab,
            source,
            seq,
            state,
            Str(o, "harness"),
            Str(o, "message"),
            Str(o, "summary"),
            session is null ? Str(o, "sessionId") : Str(session, "id"),
            session is null ? null : Str(session, "resumeCommand"),
            Release: state == AgentState.Exited);
    }

    public static bool TryParseState(string text, out AgentState state)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "idle": state = AgentState.Idle; return true;
            case "working" or "busy": state = AgentState.Working; return true;
            case "blocked" or "waiting" or "permission" or "question": state = AgentState.Blocked; return true;
            case "done" or "complete" or "completed": state = AgentState.Done; return true;
            case "error" or "failed": state = AgentState.Error; return true;
            case "exited" or "ended" or "released": state = AgentState.Exited; return true;
            case "unknown": state = AgentState.Unknown; return true;
            default: state = AgentState.Unknown; return false;
        }
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// Turns a raw GitHub Copilot CLI hook payload into a report. The event name comes from
/// the URL the hook file posts to, so the payload's own shape (camelCase or the
/// VS Code-compatible snake_case) does not matter.
/// </summary>
public static class CopilotHookTranslator
{
    public const string Source = "copilot-hooks";

    /// <summary>The hook events OverShell subscribes to. Tool events are left alone: they would add latency to every tool call.</summary>
    public static readonly string[] Events =
    [
        "sessionStart", "sessionEnd", "userPromptSubmitted", "agentStop", "notification", "errorOccurred",
    ];

    public static IntegrationReport? Translate(string tabId, string eventName, JsonNode? payload)
    {
        var o = payload as JsonObject;
        var sessionId = Str(o, "sessionId") ?? Str(o, "session_id");
        var seq = o?["timestamp"] is JsonValue tv && tv.TryGetValue<long>(out var ts) ? ts : (long?)null;

        switch (eventName.ToLowerInvariant())
        {
            case "sessionstart":
                return Make(tabId, seq, AgentState.Idle, "session started", null, sessionId);

            case "userpromptsubmitted":
                var prompt = Str(o, "prompt");
                return Make(tabId, seq, AgentState.Working, "prompt submitted", Trim(prompt), sessionId);

            case "agentstop":
            case "stop":
                return Make(tabId, seq, AgentState.Idle, "turn finished", null, sessionId);

            case "notification":
                var type = Str(o, "notification_type") ?? Str(o, "notificationType") ?? string.Empty;
                var message = Str(o, "message");
                return type switch
                {
                    "permission_prompt" => Make(tabId, seq, AgentState.Blocked, message ?? "Permission needed", null, sessionId),
                    "elicitation_dialog" => Make(tabId, seq, AgentState.Blocked, message ?? "Copilot is asking a question", null, sessionId),
                    "agent_completed" or "agent_idle" => Make(tabId, seq, AgentState.Idle, message ?? "turn finished", null, sessionId),
                    _ => null,
                };

            case "erroroccurred":
                var recoverable = o?["recoverable"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
                if (recoverable)
                {
                    return null;
                }

                var errorMessage = o?["error"] is JsonObject eo ? Str(eo, "message") : Str(o, "error");
                return Make(tabId, seq, AgentState.Error, errorMessage ?? "error", null, sessionId);

            case "sessionend":
                return Make(tabId, seq, AgentState.Exited, Str(o, "reason") ?? "session ended", null, sessionId);

            default:
                return null;
        }
    }

    private static IntegrationReport Make(string tabId, long? seq, AgentState state, string message, string? summary, string? sessionId) =>
        new(tabId, Source, seq, state, "copilot", message, summary, sessionId, null, state == AgentState.Exited);

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length <= 120 ? line : line[..117] + "…";
    }
}
