using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverShell.Core.Integrations;

/// <summary>
/// How to reach a running window's control API (§12.18): what <c>endpoint.json</c> holds.
/// The token is per run, so the file is rewritten at every start and removed at exit; a
/// stale file (the pid gone) means no window.
/// </summary>
public sealed record EndpointInfo(string Url, string Token, int Pid, DateTimeOffset StartedAt)
{
    public static EndpointInfo Current(string url, string token) => new(url, token, Environment.ProcessId, DateTimeOffset.Now);

    public JsonObject ToJson() => new()
    {
        ["url"] = Url,
        ["token"] = Token,
        ["pid"] = Pid,
        ["startedAt"] = StartedAt.ToString("o"),
    };

    /// <summary>The file's contents, or null when it is missing or not the expected shape.</summary>
    public static EndpointInfo? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o)
            {
                return null;
            }

            var url = ShellControl.Str(o, "url");
            var token = ShellControl.Str(o, "token");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            _ = int.TryParse(ShellControl.Str(o, "pid"), out var pid);
            _ = DateTimeOffset.TryParse(ShellControl.Str(o, "startedAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var startedAt);
            return new EndpointInfo(url, token, pid, startedAt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the process that wrote the file is still there.</summary>
    public bool IsAlive
    {
        get
        {
            if (Pid <= 0)
            {
                return false;
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(Pid);
                return !process.HasExited;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// <c>OverShell mcp</c> (§12.18): a Model Context Protocol server over stdio, so an agent -
/// or anything that speaks MCP - can see the herd and act on it: list tabs, read a screen,
/// send text, answer a permission, spawn a tab with a first prompt, wait for a state. Every
/// tool is one control-API call against the running window, found through
/// <c>endpoint.json</c>; this process holds no state of its own, and a window restarting
/// under it (new port, new token) is picked up at the next call.
/// <para>
/// JSON-RPC 2.0, one message per line, nothing else on stdout. <c>initialize</c>,
/// <c>ping</c>, <c>tools/list</c>, <c>tools/call</c>; notifications get no answer; methods
/// for capabilities not offered get -32601, as the protocol says.
/// </para>
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>The wire to the window: method, path with query, body; the status and the parsed body back.</summary>
    public delegate Task<(int Status, JsonNode? Body)> Transport(string method, string pathAndQuery, JsonObject? body, CancellationToken cancellation);

    private static readonly string[] KnownVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly Transport _http;
    private readonly string _version;

    public McpServer(Transport http, string version)
    {
        _http = http;
        _version = version;
    }

    /// <summary>
    /// A server whose transport is HTTP to the window <paramref name="endpoint"/> names -
    /// re-read at every call, so a restarted window is found without restarting the host.
    /// </summary>
    public static McpServer OverHttp(Func<EndpointInfo?> endpoint, string version)
    {
        var client = new HttpClient { Timeout = ShellControl.MaxWait + TimeSpan.FromSeconds(10) };
        return new McpServer(async (method, path, body, cancellation) =>
        {
            var info = endpoint();
            if (info is null)
            {
                throw new InvalidOperationException("OverShell is not running (no endpoint.json in its state folder) - start the window first.");
            }

            if (!info.IsAlive)
            {
                throw new InvalidOperationException($"OverShell is not running (endpoint.json names process {info.Pid}, which is gone).");
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), info.Url + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", info.Token);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            JsonNode? parsed = null;
            if (text.Length > 0)
            {
                try
                {
                    parsed = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    parsed = JsonValue.Create(text);
                }
            }

            return ((int)response.StatusCode, parsed);
        }, version);
    }

    /// <summary>
    /// The stdio loop: a line in, a line out, until the input ends. Messages are handled
    /// concurrently - a <c>wait</c> may hold for two minutes and must not stall a <c>ping</c> -
    /// and the writes are serialised. Returns the exit code.
    /// </summary>
    public async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellation)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();

        async Task Respond(JsonNode? response)
        {
            if (response is null)
            {
                return;
            }

            await writeLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        while (!cancellation.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException e)
            {
                await Respond(Error(null, -32700, $"parse error: {e.Message}")).ConfigureAwait(false);
                continue;
            }

            inFlight.RemoveAll(t => t.IsCompleted);
            inFlight.Add(Task.Run(async () => await Respond(await HandleAsync(message, cancellation).ConfigureAwait(false)).ConfigureAwait(false), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each handler answers its own errors; a failure to write at shutdown is not news.
        }

        return 0;
    }

    /// <summary>One JSON-RPC message in, its response out - null for a notification or an unreadable message without an id.</summary>
    public async Task<JsonNode?> HandleAsync(JsonNode? message, CancellationToken cancellation)
    {
        if (message is JsonArray batch)
        {
            // Batches left the protocol in 2025-06-18; an older client gets each answered in turn.
            var answers = new JsonArray();
            foreach (var item in batch.ToList())
            {
                if (await HandleAsync(item?.DeepClone(), cancellation).ConfigureAwait(false) is { } answer)
                {
                    answers.Add(answer);
                }
            }

            return answers.Count == 0 ? null : answers;
        }

        if (message is not JsonObject request)
        {
            return Error(null, -32600, "invalid request: not an object");
        }

        var id = request["id"]?.DeepClone();
        var method = ShellControl.Str(request, "method");
        if (method is null)
        {
            return id is null ? null : Error(id, -32600, "invalid request: no method");
        }

        var parameters = request["params"] as JsonObject;

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var asked = ShellControl.Str(parameters, "protocolVersion");
                    var version = asked is not null && KnownVersions.Contains(asked) ? asked : ProtocolVersion;
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "overshell", ["version"] = _version },
                        ["instructions"] = "OverShell is a Windows terminal that hosts AI coding agents in tabs and watches their state (Working, Idle, Blocked on a permission or question, Done, Error). " +
                                           "Use overshell_list_tabs to see the herd, overshell_read_screen to look at a tab, overshell_send to type into it, overshell_reply to approve/deny or answer the open request, " +
                                           "overshell_spawn to start an agent in a new tab with a first prompt, and overshell_wait to block until a tab settles. A tab is named by its id or a unique label.",
                    });
                }

                case "notifications/initialized":
                case "notifications/cancelled":
                case "notifications/roots/list_changed":
                    return null;

                case "ping":
                    return Result(id, new JsonObject());

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = Tools() });

                case "tools/call":
                {
                    var name = ShellControl.Str(parameters, "name");
                    if (name is null)
                    {
                        return Error(id, -32602, "invalid params: \"name\" is required");
                    }

                    var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
                    return Result(id, await CallToolAsync(name, arguments, cancellation).ConfigureAwait(false));
                }

                default:
                    return id is null ? null : Error(id, -32601, $"method not found: {method}");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return id is null ? null : Error(id, -32603, e.Message);
        }
    }

    /// <summary>The tools and their input schemas, as <c>tools/list</c> returns them.</summary>
    public static JsonArray Tools() =>
    [
        Tool("overshell_list_tabs", "List every tab in the running OverShell window: id, label, harness, state (Working/Idle/Blocked/Done/Error/Exited/Unknown), whether it needs attention, working directory, the open permission or question if any, and how it can be replied to.",
            new JsonObject()),
        Tool("overshell_read_screen", "Read a tab's terminal screen (the visible rows, oldest first). Use it to see what an agent is asking or printing before replying.",
            new JsonObject
            {
                ["tab"] = Prop("string", "Tab id or unique label."),
                ["rows"] = Prop("integer", "How many rows from the bottom to return (default 40, max 200)."),
            }, "tab"),
        Tool("overshell_send", "Type text into a tab, with Enter by default. For a shell this runs a command; for an agent's composer it submits a prompt. Prefer overshell_reply for permissions and questions.",
            new JsonObject
            {
                ["tab"] = Prop("string", "Tab id or unique label."),
                ["text"] = Prop("string", "The text to type (may contain newlines)."),
                ["enter"] = Prop("boolean", "Press Enter after the text (default true)."),
            }, "tab", "text"),
        Tool("overshell_reply", "Answer the request a tab is blocked on: approve or deny a permission, or send free text as the answer to a question or as the next prompt. Goes through the harness's own API when its integration listens, else its keys, else typed text.",
            new JsonObject
            {
                ["tab"] = Prop("string", "Tab id or unique label."),
                ["answer"] = Prop("string", "\"approve\" or \"deny\" for a permission prompt."),
                ["text"] = Prop("string", "Free text when the tab asks a question or is idle (ignored when \"answer\" is given)."),
            }, "tab"),
        Tool("overshell_spawn", "Open a new tab, optionally starting an agent (harness: opencode, copilot, claude, codex - or any command) in a directory, with a first prompt delivered once the agent is ready. Returns the new tab's description; use overshell_wait on its id.",
            new JsonObject
            {
                ["harness"] = Prop("string", "Agent to start, by its rule-file id (opencode, copilot, claude, codex). Its launch command is used; \"command\" is appended as arguments."),
                ["command"] = Prop("string", "A command to type at the shell prompt (used alone when no harness is given)."),
                ["cwd"] = Prop("string", "Working directory for the tab (must exist)."),
                ["label"] = Prop("string", "Tab label."),
                ["prompt"] = Prop("string", "The agent's first prompt, delivered once it is idle."),
                ["profile"] = Prop("string", "Shell profile name (default: the default profile)."),
                ["group"] = Prop("string", "Tab group."),
                ["activate"] = Prop("boolean", "Bring the new tab to the front (default false - the user's view is not stolen)."),
            }),
        Tool("overshell_wait", "Block until a tab reaches one of the given states, closes, or the timeout passes (max 120 s; call again for longer). Default states: Idle, Done, Blocked, Error, Exited - i.e. the agent stopped for some reason.",
            new JsonObject
            {
                ["tab"] = Prop("string", "Tab id or unique label."),
                ["states"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "States to wait for: Idle, Done, Blocked, Error, Exited, Working, Unknown." },
                ["timeoutSeconds"] = Prop("integer", "How long to wait (default 60, max 120)."),
            }, "tab"),
        Tool("overshell_close", "Close a tab (never the last one). Use after an agent is done and its output has been read.",
            new JsonObject { ["tab"] = Prop("string", "Tab id or unique label.") }, "tab"),
    ];

    private async Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        try
        {
            switch (name)
            {
                case "overshell_list_tabs":
                {
                    var (status, body) = await _http("GET", "/v1/tabs", null, cancellation).ConfigureAwait(false);
                    return status == 200 ? Text(Pretty(body?["tabs"] ?? new JsonArray())) : Failure(status, body);
                }

                case "overshell_read_screen":
                {
                    var tab = Require(arguments, "tab");
                    var (status, body) = await _http("GET", $"/v1/tabs/{Uri.EscapeDataString(tab)}/screen", null, cancellation).ConfigureAwait(false);
                    if (status != 200)
                    {
                        return Failure(status, body);
                    }

                    var rows = (body?["rows"] as JsonArray)?.Select(r => r?.ToString() ?? string.Empty).ToList() ?? [];
                    var count = Math.Clamp(Int(arguments, "rows") ?? 40, 1, 200);
                    var tail = rows.Count > count ? rows.Skip(rows.Count - count) : rows;
                    return Text(string.Join('\n', tail).TrimEnd('\n'));
                }

                case "overshell_send":
                {
                    var tab = Require(arguments, "tab");
                    var text = Require(arguments, "text");
                    var payload = new JsonObject { ["text"] = text, ["enter"] = ShellControl.Bool(arguments, "enter") ?? true };
                    var (status, body) = await _http("POST", $"/v1/tabs/{Uri.EscapeDataString(tab)}/input", payload, cancellation).ConfigureAwait(false);
                    return status == 200 ? Text($"sent to {tab}") : Failure(status, body);
                }

                case "overshell_reply":
                {
                    var tab = Require(arguments, "tab");
                    var payload = new JsonObject();
                    if (ShellControl.Str(arguments, "answer") is { Length: > 0 } answer)
                    {
                        payload["answer"] = answer;
                    }
                    else if (ShellControl.Str(arguments, "text") is { Length: > 0 } text)
                    {
                        payload["text"] = text;
                    }
                    else
                    {
                        return Text("either \"answer\" (approve | deny) or \"text\" is required", isError: true);
                    }

                    var (status, body) = await _http("POST", $"/v1/tabs/{Uri.EscapeDataString(tab)}/reply", payload, cancellation).ConfigureAwait(false);
                    return status == 200 ? Text($"replied to {tab}") : Failure(status, body);
                }

                case "overshell_spawn":
                {
                    var payload = new JsonObject();
                    foreach (var key in new[] { "harness", "command", "cwd", "label", "prompt", "profile", "group" })
                    {
                        if (ShellControl.Str(arguments, key) is { Length: > 0 } value)
                        {
                            payload[key] = value;
                        }
                    }

                    if (ShellControl.Bool(arguments, "activate") is { } activate)
                    {
                        payload["activate"] = activate;
                    }

                    var (status, body) = await _http("POST", "/v1/tabs", payload, cancellation).ConfigureAwait(false);
                    return status == 201 ? Text(Pretty(body)) : Failure(status, body);
                }

                case "overshell_wait":
                {
                    var tab = Require(arguments, "tab");
                    var states = arguments["states"] is JsonArray array
                        ? string.Join(',', array.Select(s => s?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)))
                        : ShellControl.Str(arguments, "states") ?? string.Empty;
                    var timeout = Math.Clamp(Int(arguments, "timeoutSeconds") ?? 60, 0, (int)ShellControl.MaxWait.TotalSeconds);
                    var (status, body) = await _http("GET", $"/v1/tabs/{Uri.EscapeDataString(tab)}/wait?states={Uri.EscapeDataString(states)}&timeout={timeout}", null, cancellation).ConfigureAwait(false);
                    return status == 200 ? Text(Pretty(body)) : Failure(status, body);
                }

                case "overshell_close":
                {
                    var tab = Require(arguments, "tab");
                    var (status, body) = await _http("DELETE", $"/v1/tabs/{Uri.EscapeDataString(tab)}", null, cancellation).ConfigureAwait(false);
                    return status == 200 ? Text($"closed {tab}") : Failure(status, body);
                }

                default:
                    return Text($"unknown tool: {name}", isError: true);
            }
        }
        catch (ArgumentException e)
        {
            return Text(e.Message, isError: true);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return Text($"OverShell is not reachable: {e.Message}", isError: true);
        }
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        }

        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static string Require(JsonObject arguments, string name) =>
        ShellControl.Str(arguments, name) is { Length: > 0 } value ? value : throw new ArgumentException($"\"{name}\" is required");

    private static int? Int(JsonObject arguments, string name) =>
        int.TryParse(ShellControl.Str(arguments, name), out var value) ? value : null;

    private static JsonObject Text(string text, bool isError = false)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (isError)
        {
            result["isError"] = true;
        }

        return result;
    }

    private static JsonObject Failure(int status, JsonNode? body) =>
        Text($"OverShell answered {status}: {(body is JsonObject o ? ShellControl.Str(o, "error") ?? o.ToJsonString() : body?.ToJsonString() ?? "(no body)")}", isError: true);

    private static string Pretty(JsonNode? node) => node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    private static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
