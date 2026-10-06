using System.Text.Json.Nodes;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using Xunit;

namespace OverShell.Tests;

public class McpServerTests
{
    /// <summary>A transport that records the calls and answers from a script keyed by "METHOD path".</summary>
    private sealed class FakeWire
    {
        public readonly List<(string Method, string Path, JsonObject? Body)> Calls = [];
        public readonly Dictionary<string, (int Status, JsonNode? Body)> Answers = new(StringComparer.Ordinal);

        public Task<(int Status, JsonNode? Body)> Send(string method, string path, JsonObject? body, CancellationToken cancellation)
        {
            Calls.Add((method, path, body));
            if (Answers.TryGetValue($"{method} {path}", out var answer))
            {
                return Task.FromResult(answer);
            }

            return Task.FromResult((404, (JsonNode?)new JsonObject { ["error"] = "not found" }));
        }
    }

    private static JsonObject Request(int id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters,
    };

    private static string ToolText(JsonNode? response) =>
        response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public async Task Initialize_answers_with_tools_and_the_version_the_client_asked_for_when_known()
    {
        var server = new McpServer(new FakeWire().Send, "0.1.0-test");
        var response = await server.HandleAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2024-11-05" }), CancellationToken.None);

        Assert.Equal("2.0", response!["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Equal("2024-11-05", response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("overshell", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("0.1.0-test", response["result"]!["serverInfo"]!["version"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);

        var unknown = await server.HandleAsync(Request(2, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }), CancellationToken.None);
        Assert.Equal(McpServer.ProtocolVersion, unknown!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifications_get_no_answer_and_unknown_methods_get_32601()
    {
        var server = new McpServer(new FakeWire().Send, "t");
        Assert.Null(await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, CancellationToken.None));

        var missing = await server.HandleAsync(Request(7, "resources/list"), CancellationToken.None);
        Assert.Equal(-32601, missing!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(7, missing["id"]!.GetValue<int>());

        var ping = await server.HandleAsync(Request(8, "ping"), CancellationToken.None);
        Assert.NotNull(ping!["result"]);
    }

    [Fact]
    public async Task Tools_list_names_the_seven_tools_with_schemas()
    {
        var server = new McpServer(new FakeWire().Send, "t");
        var response = await server.HandleAsync(Request(3, "tools/list"), CancellationToken.None);
        var tools = (JsonArray)response!["result"]!["tools"]!;

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["overshell_list_tabs", "overshell_read_screen", "overshell_send", "overshell_reply", "overshell_spawn", "overshell_wait", "overshell_close"], names);
        Assert.All(tools, t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));
        Assert.Contains("tab", ((JsonArray)tools[1]!["inputSchema"]!["required"]!).Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public async Task Tool_calls_are_control_api_calls_and_errors_come_back_as_tool_errors()
    {
        var wire = new FakeWire();
        wire.Answers["GET /v1/tabs"] = (200, new JsonObject { ["tabs"] = new JsonArray(new JsonObject { ["id"] = "a1", ["label"] = "oc", ["state"] = "Idle" }) });
        wire.Answers["GET /v1/tabs/oc/screen"] = (200, new JsonObject { ["rows"] = new JsonArray("one", "two", "three") });
        wire.Answers["POST /v1/tabs/oc/input"] = (200, new JsonObject { ["ok"] = true });
        wire.Answers["POST /v1/tabs/oc/reply"] = (409, new JsonObject { ["error"] = "no yes/no channel" });
        wire.Answers["POST /v1/tabs"] = (201, new JsonObject { ["id"] = "b2", ["label"] = "new" });
        wire.Answers["GET /v1/tabs/b2/wait?states=Idle%2CDone&timeout=5"] = (200, new JsonObject { ["id"] = "b2", ["state"] = "Done", ["timedOut"] = false });
        var server = new McpServer(wire.Send, "t");

        var list = await server.HandleAsync(Request(1, "tools/call", new JsonObject { ["name"] = "overshell_list_tabs" }), CancellationToken.None);
        Assert.Contains("\"label\": \"oc\"", ToolText(list));

        var screen = await server.HandleAsync(Request(2, "tools/call", new JsonObject { ["name"] = "overshell_read_screen", ["arguments"] = new JsonObject { ["tab"] = "oc", ["rows"] = 2 } }), CancellationToken.None);
        Assert.Equal("two\nthree", ToolText(screen));

        var send = await server.HandleAsync(Request(3, "tools/call", new JsonObject { ["name"] = "overshell_send", ["arguments"] = new JsonObject { ["tab"] = "oc", ["text"] = "dir", ["enter"] = false } }), CancellationToken.None);
        Assert.Equal("sent to oc", ToolText(send));
        var sent = wire.Calls.Single(c => c.Path == "/v1/tabs/oc/input").Body!;
        Assert.Equal("dir", sent["text"]!.GetValue<string>());
        Assert.False(sent["enter"]!.GetValue<bool>());

        var reply = await server.HandleAsync(Request(4, "tools/call", new JsonObject { ["name"] = "overshell_reply", ["arguments"] = new JsonObject { ["tab"] = "oc", ["answer"] = "approve" } }), CancellationToken.None);
        Assert.True(reply!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("409", ToolText(reply));
        Assert.Contains("no yes/no channel", ToolText(reply));

        var spawn = await server.HandleAsync(Request(5, "tools/call", new JsonObject { ["name"] = "overshell_spawn", ["arguments"] = new JsonObject { ["harness"] = "opencode", ["cwd"] = "C:\\repo", ["prompt"] = "go", ["activate"] = "false" } }), CancellationToken.None);
        Assert.Contains("\"id\": \"b2\"", ToolText(spawn));
        var opened = wire.Calls.Single(c => c.Path == "/v1/tabs" && c.Method == "POST").Body!;
        Assert.Equal("opencode", opened["harness"]!.GetValue<string>());
        Assert.False(opened["activate"]!.GetValue<bool>());

        var wait = await server.HandleAsync(Request(6, "tools/call", new JsonObject { ["name"] = "overshell_wait", ["arguments"] = new JsonObject { ["tab"] = "b2", ["states"] = new JsonArray("Idle", "Done"), ["timeoutSeconds"] = 5 } }), CancellationToken.None);
        Assert.Contains("\"state\": \"Done\"", ToolText(wait));

        var missingArgument = await server.HandleAsync(Request(7, "tools/call", new JsonObject { ["name"] = "overshell_send", ["arguments"] = new JsonObject { ["tab"] = "oc" } }), CancellationToken.None);
        Assert.True(missingArgument!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("\"text\" is required", ToolText(missingArgument));

        var unknownTool = await server.HandleAsync(Request(8, "tools/call", new JsonObject { ["name"] = "overshell_fly" }), CancellationToken.None);
        Assert.True(unknownTool!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_window_that_is_not_there_is_a_tool_error_not_a_protocol_error()
    {
        var server = new McpServer((_, _, _, _) => throw new InvalidOperationException("OverShell is not running (no endpoint.json)"), "t");
        var response = await server.HandleAsync(Request(1, "tools/call", new JsonObject { ["name"] = "overshell_list_tabs" }), CancellationToken.None);
        Assert.Null(response!["error"]);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("not running", ToolText(response));
    }

    [Fact]
    public async Task The_stdio_loop_answers_one_line_per_message_and_ends_at_eof()
    {
        var server = new McpServer(new FakeWire().Send, "t");
        var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "not json\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}\n"));
        var output = new MemoryStream();

        var code = await server.RunAsync(input, output, CancellationToken.None);

        Assert.Equal(0, code);
        var lines = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var byId = lines.Select(l => JsonNode.Parse(l)!).ToDictionary(n => n["id"]?.ToJsonString() ?? "null");
        Assert.Equal("2025-06-18", byId["1"]["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(-32700, byId["null"]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(byId["2"]["result"]);
    }

    [Fact]
    public void Endpoint_file_round_trips_and_rejects_the_half_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"overshell-endpoint-{Guid.NewGuid():N}.json");
        try
        {
            var info = EndpointInfo.Current("http://127.0.0.1:50000", "abc123");
            File.WriteAllText(path, info.ToJson().ToJsonString());
            var read = EndpointInfo.Read(path);
            Assert.NotNull(read);
            Assert.Equal(info.Url, read.Url);
            Assert.Equal(info.Token, read.Token);
            Assert.Equal(Environment.ProcessId, read.Pid);
            Assert.True(read.IsAlive);

            File.WriteAllText(path, "{\"url\":\"http://127.0.0.1:1\"}");
            Assert.Null(EndpointInfo.Read(path));
            File.WriteAllText(path, "{ not json");
            Assert.Null(EndpointInfo.Read(path));
            File.Delete(path);
            Assert.Null(EndpointInfo.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void States_parse_loosely_and_fields_read_strings_or_values()
    {
        Assert.Equal([AgentState.Idle, AgentState.Done, AgentState.Blocked], ShellControl.ParseStates("idle, Done;BLOCKED,nonsense"));
        Assert.Empty(ShellControl.ParseStates(null));

        var body = new JsonObject { ["text"] = "hi", ["n"] = 3, ["flag"] = true, ["word"] = "false", ["obj"] = new JsonObject() };
        Assert.Equal("hi", ShellControl.Str(body, "text"));
        Assert.Equal("3", ShellControl.Str(body, "n"));
        Assert.Null(ShellControl.Str(body, "obj"));
        Assert.Null(ShellControl.Str(body, "missing"));
        Assert.True(ShellControl.Bool(body, "flag"));
        Assert.False(ShellControl.Bool(body, "word"));
        Assert.Null(ShellControl.Bool(body, "text"));
        Assert.Null(ShellControl.Bool(null, "flag"));
    }
}
