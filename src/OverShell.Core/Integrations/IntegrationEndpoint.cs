using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OverShell.Core.Integrations;

/// <summary>A tab as the diagnostics endpoint describes it.</summary>
public sealed record TabSummary(string Id, string Label, string? Harness, string State, string? Project, string? Explain);

/// <summary>
/// The one door every harness integration comes through: a loopback HTTP listener with a
/// per-run bearer token. Copilot hooks (via a <c>cmd.exe</c>+<c>curl</c> shim), the
/// OpenCode plugin (<c>fetch</c>), a PowerShell one-liner — anything that can POST JSON.
/// <para>
/// Requests are parsed here on a listener thread; <see cref="ReportReceived"/> fires on
/// that thread and the UI layer marshals. Nothing in this class knows what a tab is.
/// </para>
/// </summary>
public sealed class IntegrationEndpoint : IDisposable
{
    private const int MaxBodyBytes = 256 * 1024;

    private HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public IntegrationEndpoint()
    {
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    public string Token { get; }

    public int Port { get; private set; }

    /// <summary>e.g. <c>http://127.0.0.1:54321</c>, no trailing slash.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public bool IsRunning => _listener.IsListening;

    /// <summary>Raised on the listener thread.</summary>
    public event Action<IntegrationReport>? ReportReceived;

    /// <summary>Supplies <c>GET /v1/tabs</c>. Set by the UI layer; called on the listener thread.</summary>
    public Func<IReadOnlyList<TabSummary>>? TabsProvider { get; set; }

    /// <summary>Commands for the integrations to carry out, per tab (§12.17): the UI enqueues, the harness's plugin long-polls.</summary>
    public TabCommandQueue Commands { get; } = new();

    /// <summary>
    /// The control API (§12.18) when it is on: set by the UI layer, null when
    /// <c>endpoint.control</c> is off - the routes then answer 403 and the endpoint is what it
    /// was before, a place for reports and polls.
    /// </summary>
    public ShellControl? Control { get; set; }

    /// <summary>How long a poll with nothing waiting is held before an empty answer.</summary>
    public static readonly TimeSpan PollHold = TimeSpan.FromSeconds(25);

    /// <summary>Every request, for the trace log: method, path, status, elapsed.</summary>
    public event Action<string>? Trace;

    /// <summary>Environment a child process needs to reach this endpoint.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentFor(string tabId) => new Dictionary<string, string?>
    {
        ["OVERSHELL_ENDPOINT"] = BaseUrl,
        ["OVERSHELL_TOKEN"] = Token,
        ["OVERSHELL_TAB_ID"] = tabId,
        // Copilot CLI refuses plain http hooks unless told localhost is fine.
        ["COPILOT_HOOK_ALLOW_LOCALHOST"] = "1",
    };

    /// <summary>Binds a free ephemeral port. Throws if none of a few dozen attempts succeeds.</summary>
    public void Start()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var port = RandomNumberGenerator.GetInt32(49152, 65535);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port;
                _loop = Task.Run(LoopAsync);
                return;
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                // Port taken or reserved (Hyper-V and WSL reserve whole ranges in 49152-65535).
                // A failed Start() leaves the HttpListener disposed - the next Prefixes.Clear()
                // would throw ObjectDisposedException - so every attempt gets a fresh one.
                _listener = new HttpListener();
            }
        }

        throw new InvalidOperationException("No free loopback port for the integration endpoint.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        int status;

        try
        {
            status = await RouteAsync(request, response, path).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            status = 500;
            await WriteAsync(response, 500, new JsonObject { ["error"] = e.Message }).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Trace?.Invoke($"{request.HttpMethod} {path} -> {status} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
    }

    private async Task<int> RouteAsync(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        if (path == "/v1/health")
        {
            return await WriteAsync(response, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
        }

        if (!Authorized(request))
        {
            return await WriteAsync(response, 401, new JsonObject { ["error"] = "unauthorized" }).ConfigureAwait(false);
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var onTabs = segments.Length >= 2 && segments[0] == "v1" && segments[1] == "tabs";
        var tabKey = onTabs && segments.Length >= 3 ? Uri.UnescapeDataString(segments[2]) : null;
        var leaf = onTabs && segments.Length == 4 ? segments[3] : null;

        if (request.HttpMethod == "GET" && onTabs && segments.Length == 2)
        {
            // The control's description when it is on (a superset), the summary otherwise.
            if (Control is { } control)
            {
                return await WriteAsync(response, 200, new JsonObject { ["tabs"] = await control.ListAsync().ConfigureAwait(false) }).ConfigureAwait(false);
            }

            var tabs = new JsonArray();
            foreach (var t in TabsProvider?.Invoke() ?? [])
            {
                tabs.Add(new JsonObject
                {
                    ["id"] = t.Id, ["label"] = t.Label, ["harness"] = t.Harness, ["state"] = t.State, ["project"] = t.Project, ["explain"] = t.Explain,
                });
            }

            return await WriteAsync(response, 200, new JsonObject { ["tabs"] = tabs }).ConfigureAwait(false);
        }

        // GET /v1/tabs/{tabId}/commands?after=N - a long poll for the integration's work queue (12.17).
        if (request.HttpMethod == "GET" && tabKey is not null && leaf == "commands")
        {
            _ = long.TryParse(request.QueryString["after"], out var after);
            var hold = PollHold;
            if (int.TryParse(request.QueryString["holdMs"], out var holdMs))
            {
                hold = TimeSpan.FromMilliseconds(Math.Clamp(holdMs, 0, 30000));
            }

            var commands = await Commands.PollAsync(tabKey, after, hold).ConfigureAwait(false);
            var array = new JsonArray();
            foreach (var command in commands)
            {
                array.Add(command.ToJson());
            }

            return await WriteAsync(response, 200, new JsonObject { ["commands"] = array }).ConfigureAwait(false);
        }

        // ---- the control API (12.18): everything else under /v1/tabs ----
        var controlRoute = onTabs && (
            (request.HttpMethod == "POST" && segments.Length == 2) ||
            (request.HttpMethod is "GET" or "DELETE" && segments.Length == 3) ||
            (segments.Length == 4 && leaf is "screen" or "wait" or "input" or "reply"));
        if (controlRoute)
        {
            if (Control is not { } control)
            {
                return await WriteAsync(response, 403, new JsonObject { ["error"] = "the control API is off (settings: endpoint.control)" }).ConfigureAwait(false);
            }

            return await RouteControlAsync(control, request, response, segments, tabKey, leaf).ConfigureAwait(false);
        }

        if (request.HttpMethod != "POST")
        {
            return await WriteAsync(response, 405, new JsonObject { ["error"] = "method not allowed" }).ConfigureAwait(false);
        }

        var body = await ReadBodyAsync(request).ConfigureAwait(false);
        var node = body is null ? null : Jsonc.Parse(body, out _);

        if (path == "/v1/report")
        {
            var report = IntegrationReport.Parse(string.Empty, node, out var error);
            if (report is null)
            {
                return await WriteAsync(response, 400, new JsonObject { ["error"] = error }).ConfigureAwait(false);
            }

            ReportReceived?.Invoke(report);
            return await WriteAsync(response, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
        }

        if (path == "/v1/release")
        {
            var tab = (node as JsonObject)?["tab"]?.GetValue<string>();
            var source = (node as JsonObject)?["source"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tab) || string.IsNullOrWhiteSpace(source))
            {
                return await WriteAsync(response, 400, new JsonObject { ["error"] = "\"tab\" and \"source\" are required" }).ConfigureAwait(false);
            }

            ReportReceived?.Invoke(new IntegrationReport(tab, source, null, Agents.AgentState.Exited, null, "released", null, null, null, Release: true));
            return await WriteAsync(response, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
        }

        // /v1/copilot/{tabId}/{event}, /v1/claude/{tabId}/{event}, /v1/codex/{tabId}/notify: raw hook payloads, translated.
        if (segments.Length == 4 && segments[0] == "v1" && segments[1] is "copilot" or "claude" or "codex")
        {
            var tabId = Uri.UnescapeDataString(segments[2]);
            var report = segments[1] switch
            {
                "copilot" => CopilotHookTranslator.Translate(tabId, segments[3], node),
                "claude" => ClaudeHookTranslator.Translate(tabId, segments[3], node),
                _ => CodexNotifyTranslator.Translate(tabId, node),
            };
            if (report is not null)
            {
                ReportReceived?.Invoke(report);
            }

            return await WriteAsync(response, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
        }

        return await WriteAsync(response, 404, new JsonObject { ["error"] = "not found" }).ConfigureAwait(false);
    }

    /// <summary>
    /// The control routes (§12.18), each a spelling of one <see cref="ShellControl"/> call:
    /// <c>GET /v1/tabs/{tab}</c>, <c>GET .../screen</c>, <c>GET .../wait?states=&amp;timeout=</c>,
    /// <c>POST /v1/tabs</c>, <c>POST .../input</c>, <c>POST .../reply</c>, <c>DELETE /v1/tabs/{tab}</c>.
    /// A tab is its id or a unique label.
    /// </summary>
    private async Task<int> RouteControlAsync(ShellControl control, HttpListenerRequest request, HttpListenerResponse response, string[] segments, string? tabKey, string? leaf)
    {
        if (request.HttpMethod == "POST" && segments.Length == 2)
        {
            var body = await ReadBodyAsync(request).ConfigureAwait(false);
            var node = (body is null ? null : Jsonc.Parse(body, out _)) as JsonObject ?? new JsonObject();
            var (tab, result) = await control.OpenAsync(node).ConfigureAwait(false);
            return tab is null
                ? await WriteResultAsync(response, result).ConfigureAwait(false)
                : await WriteAsync(response, 201, tab).ConfigureAwait(false);
        }

        if (tabKey is null)
        {
            return await WriteAsync(response, 404, new JsonObject { ["error"] = "not found" }).ConfigureAwait(false);
        }

        if (request.HttpMethod == "DELETE")
        {
            return await WriteResultAsync(response, await control.CloseAsync(tabKey).ConfigureAwait(false)).ConfigureAwait(false);
        }

        if (request.HttpMethod == "GET")
        {
            switch (leaf)
            {
                case null:
                {
                    var tab = await control.DescribeAsync(tabKey).ConfigureAwait(false);
                    return tab is null
                        ? await WriteResultAsync(response, ControlResult.NotFound(tabKey)).ConfigureAwait(false)
                        : await WriteAsync(response, 200, tab).ConfigureAwait(false);
                }

                case "screen":
                {
                    var screen = await control.ScreenAsync(tabKey).ConfigureAwait(false);
                    return screen is null
                        ? await WriteResultAsync(response, ControlResult.NotFound(tabKey)).ConfigureAwait(false)
                        : await WriteAsync(response, 200, screen).ConfigureAwait(false);
                }

                case "wait":
                {
                    var timeout = TimeSpan.FromSeconds(60);
                    if (double.TryParse(request.QueryString["timeout"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, ShellControl.MaxWait.TotalSeconds));
                    }

                    var states = ShellControl.ParseStates(request.QueryString["states"]);
                    var outcome = await control.WaitAsync(tabKey, states, timeout, _stop.Token).ConfigureAwait(false);
                    return await WriteAsync(response, 200, outcome).ConfigureAwait(false);
                }
            }

            return await WriteAsync(response, 405, new JsonObject { ["error"] = "method not allowed" }).ConfigureAwait(false);
        }

        if (request.HttpMethod == "POST" && leaf is "input" or "reply")
        {
            var body = await ReadBodyAsync(request).ConfigureAwait(false);
            var node = (body is null ? null : Jsonc.Parse(body, out _)) as JsonObject;
            if (leaf == "input")
            {
                var text = ShellControl.Str(node, "text");
                var enter = ShellControl.Bool(node, "enter") ?? true;
                if (text is null && !enter)
                {
                    return await WriteResultAsync(response, ControlResult.BadRequest("\"text\" is required (or \"enter\": true)")).ConfigureAwait(false);
                }

                return await WriteResultAsync(response, await control.SendAsync(tabKey, text ?? string.Empty, enter).ConfigureAwait(false)).ConfigureAwait(false);
            }

            return await WriteResultAsync(response, await control.ReplyAsync(tabKey, ShellControl.Str(node, "answer"), ShellControl.Str(node, "text")).ConfigureAwait(false)).ConfigureAwait(false);
        }

        return await WriteAsync(response, 405, new JsonObject { ["error"] = "method not allowed" }).ConfigureAwait(false);
    }

    private static Task<int> WriteResultAsync(HttpListenerResponse response, ControlResult result) => result.Status switch
    {
        ControlStatus.Ok => WriteAsync(response, 200, new JsonObject { ["ok"] = true }),
        ControlStatus.NotFound => WriteAsync(response, 404, new JsonObject { ["error"] = result.Message ?? "not found" }),
        ControlStatus.BadRequest => WriteAsync(response, 400, new JsonObject { ["error"] = result.Message ?? "bad request" }),
        _ => WriteAsync(response, 409, new JsonObject { ["error"] = result.Message ?? "refused" }),
    };

    private bool Authorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(header[7..].Trim()),
                Encoding.ASCII.GetBytes(Token));
        }

        // The cmd.exe + curl shim cannot quote a header, so the token may ride in the query string.
        var query = request.QueryString["token"];
        return query is not null &&
               CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(query), Encoding.ASCII.GetBytes(Token));
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return null;
        }

        if (request.ContentLength64 > MaxBodyBytes)
        {
            return null;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxBodyBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return new string(buffer, 0, total);
    }

    private static async Task<int> WriteAsync(HttpListenerResponse response, int status, JsonObject body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        return status;
    }
}
