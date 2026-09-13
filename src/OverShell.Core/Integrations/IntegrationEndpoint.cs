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

    private readonly HttpListener _listener = new();
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
            catch (HttpListenerException)
            {
                // Port taken or reserved; try another.
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

        if (request.HttpMethod == "GET" && path == "/v1/tabs")
        {
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
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
