using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using OverShell.App.Extensibility;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Commands;
using OverShell.Core.Extensibility;
using OverShell.Core.Notifications;
using OverShell.Core.Settings;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class ConfiguredExtensionTests
{
    [Fact]
    public void Process_arguments_paths_and_environment_do_not_go_through_a_shell()
    {
        var root = Path.Combine(Path.GetTempPath(), "config with spaces");
        var state = Path.Combine(Path.GetTempPath(), "state with spaces");
        var settings = new ProcessExtensionSettings
        {
            Command = ".\\extensions\\helper.exe",
            Args = ["{configDir}/a b.cs", "a \"quote\"; $literal", "{stateDir}"],
            Environment = new() { ["CUSTOM"] = "{configDir}", ["PATH"] = null },
        };
        var start = settings.StartInfo("mine", root, state);
        Assert.False(start.UseShellExecute);
        Assert.Equal(Path.Combine(root, "extensions", "helper.exe"), start.FileName);
        Assert.Equal(root, start.WorkingDirectory);
        Assert.Equal(new[] { root + "/a b.cs", "a \"quote\"; $literal", state }, start.ArgumentList);
        Assert.Equal(root, start.Environment["CUSTOM"]);
        Assert.False(start.Environment.ContainsKey("PATH"));
        Assert.Equal("mine", start.Environment["OVERSHELL_EXTENSION_ID"]);
        Assert.Equal(Environment.ProcessId.ToString(), start.Environment["OVERSHELL_HOST_PID"]);
        Assert.NotEmpty(Environment.GetEnvironmentVariable("PATH")!);
    }

    [Fact]
    public void Snapshots_preserve_session_attention_without_reading_terminal_content()
    {
        var tab = new FeedTab { State = AgentState.Idle, Unread = true, OpenRequest = new("request", "question", "Choose", DateTimeOffset.UtcNow) };
        var shell = new FeedShell(tab);
        var snapshot = ExtensionSnapshot.Create(shell, "feed");
        Assert.Equal(1, snapshot["protocol"]!.GetValue<int>());
        var json = snapshot["tabs"]![0]!;
        Assert.Equal("session", json["sessionId"]!.GetValue<string>());
        Assert.True(json["unread"]!.GetValue<bool>());
        Assert.Equal("question", json["request"]!["kind"]!.GetValue<string>());
        Assert.Null(json["lastActivity"]);
        Assert.Null(json["screen"]);
        Assert.False(ExtensionSnapshot.IncludesProperty(nameof(ITab.LastActivity)));
        Assert.False(ExtensionSnapshot.IncludesProperty(nameof(ITab.ScreenRows)));
        Assert.True(ExtensionSnapshot.IncludesProperty(nameof(ITab.Unread)));
        Assert.True(ExtensionSnapshot.IncludesProperty(nameof(ITab.SessionId)));
    }

    [Fact]
    public async Task Configured_command_gets_changes_and_click_commands_and_reloads_without_overlapping_processes()
    {
        using var fixture = new ProcessFixture();
        var firstLog = Path.Combine(fixture.Root, "first feed.jsonl");
        var secondLog = Path.Combine(fixture.Root, "second feed.jsonl");
        var shell = new FeedShell(new FeedTab());
        shell.Settings = fixture.Settings(firstLog);
        var clicks = new List<string>();
        shell.Commands.Register("test.activate", "Test", "Test", invocation =>
        {
            clicks.Add(invocation.Arguments["target"]!["id"]!.GetValue<string>());
            return true;
        });
        using var host = new ExtensionHost();
        host.LoadConfigured(shell, fixture.Root, fixture.Root);
        var command = Assert.IsType<CommandExtension>(Assert.Single(host.Loaded));
        await Until(() => fixture.Messages(firstLog).Count >= 1, shell);
        Assert.Equal("Unicode ✓ שלום", fixture.Messages(firstLog)[0]["tabs"]![0]!["label"]!.GetValue<string>());
        await Until(() => clicks.Count == 1, shell);
        Assert.Equal("tab", clicks[0]);

        for (var i = 0; i < 20; i++) shell.Beat();
        await Task.Delay(150);
        Assert.Single(fixture.Messages(firstLog));
        shell.Tab.Unread = true;
        shell.Tab.Raise(nameof(ITab.Unread));
        await Until(() => fixture.Messages(firstLog).Count == 2, shell);
        Assert.True(fixture.Messages(firstLog)[1]["tabs"]![0]!["unread"]!.GetValue<bool>());

        shell.Settings.Extensions["feed"]!["showIdle"] = true;
        shell.Reload();
        await Until(() => fixture.Messages(firstLog).Count == 3, shell);
        Assert.True(fixture.Messages(firstLog)[2]["settings"]!["showIdle"]!.GetValue<bool>());
        Assert.Single(File.ReadAllLines(firstLog + ".starts"));

        shell.Settings = fixture.Settings(secondLog, firstLog + ".closed");
        shell.Reload();
        await Until(() => fixture.Messages(secondLog).Count > 0, shell);
        Assert.True(File.Exists(firstLog + ".closed"));
        Assert.Same(command, Assert.Single(host.Loaded));
        Assert.False(command.Completion.IsCompleted);

        shell.Settings.Extensions["feed"]!["enabled"] = false;
        shell.Reload();
        await command.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(host.Loaded);
        Assert.True(File.Exists(secondLog + ".closed"));
        Assert.False(IsRunning(int.Parse(File.ReadAllLines(secondLog + ".starts")[0])));
    }

    [Fact]
    public async Task Unexpected_exit_restarts_with_a_full_snapshot_even_without_another_state_change()
    {
        using var fixture = new ProcessFixture("""
            param($Log)
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            $line = [Console]::ReadLine()
            [IO.File]::AppendAllText($Log, $line + "`n", [Text.UTF8Encoding]::new($false))
            exit 7
            """);
        var log = Path.Combine(fixture.Root, "restarts.jsonl");
        var shell = new FeedShell(new FeedTab()) { Settings = fixture.Settings(log) };
        using var host = new ExtensionHost();
        host.LoadConfigured(shell, fixture.Root, fixture.Root);
        var command = Assert.IsType<CommandExtension>(Assert.Single(host.Loaded));
        await Until(() => fixture.Messages(log).Count >= 2, shell);
        command.Dispose();
        await command.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        var messages = fixture.Messages(log);
        Assert.Equal(messages[0].ToJsonString(), messages[1].ToJsonString());
    }

    [Fact]
    public async Task A_child_that_does_not_read_stdin_cannot_block_shutdown_or_grow_an_update_queue()
    {
        using var fixture = new ProcessFixture("""
            param($Log)
            [IO.File]::WriteAllText($Log + '.pid', [string]$PID)
            Start-Sleep -Seconds 60
            """);
        var log = Path.Combine(fixture.Root, "blocked");
        var shell = new FeedShell(new FeedTab { Label = new string('x', 500_000) }) { Settings = fixture.Settings(log) };
        shell.Settings.Extensions["feed"]!["process"]!["shutdownTimeoutMs"] = 150;
        using var host = new ExtensionHost();
        host.LoadConfigured(shell, fixture.Root, fixture.Root);
        var command = Assert.IsType<CommandExtension>(Assert.Single(host.Loaded));
        await Until(() => File.Exists(log + ".pid"), shell);
        for (var i = 0; i < 25; i++)
        {
            shell.Tab.Unread = !shell.Tab.Unread;
            shell.Tab.Raise(nameof(ITab.Unread));
            shell.Beat();
        }
        var elapsed = Stopwatch.StartNew();
        command.Dispose();
        await command.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(IsRunning(int.Parse(File.ReadAllText(log + ".pid"))));
    }

    [Fact]
    public void Dlls_share_the_host_contract_and_dispose_their_contributions_when_disabled()
    {
        var shell = new FeedShell(new FeedTab());
        shell.Settings.Extensions["test.dll"] = new JsonObject
        {
            ["assembly"] = new JsonObject { ["path"] = typeof(FeedDll).Assembly.Location, ["type"] = typeof(FeedDll).FullName },
        };
        using var host = new ExtensionHost();
        host.LoadConfigured(shell, AppContext.BaseDirectory, AppContext.BaseDirectory);
        Assert.Single(host.Loaded);
        Assert.True(shell.Commands.TryExecute("test.dll.command"));
        Assert.Contains("DLL command executed", shell.Traces);
        shell.Settings.Extensions["test.dll"]!["enabled"] = false;
        shell.Reload();
        Assert.Empty(host.Loaded);
        Assert.Null(shell.Commands.Find("test.dll.command"));
    }

    [Fact]
    public void A_bad_external_definition_is_reported_without_preventing_other_extensions()
    {
        var shell = new FeedShell(new FeedTab());
        shell.Settings.Extensions["broken"] = new JsonObject { ["assembly"] = new JsonObject { ["path"] = "missing.dll", ["type"] = "Absent" } };
        shell.Settings.Extensions["test.dll"] = new JsonObject
        {
            ["assembly"] = new JsonObject { ["path"] = typeof(FeedDll).Assembly.Location, ["type"] = typeof(FeedDll).FullName },
        };
        using var host = new ExtensionHost();
        host.LoadConfigured(shell, AppContext.BaseDirectory, AppContext.BaseDirectory);
        Assert.Single(host.Loaded);
        Assert.Contains(shell.Traces, trace => trace.Contains("broken: failed to load"));
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task Until(Func<bool> condition, FeedShell shell)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            shell.Beat();
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(20), string.Join('\n', shell.Traces));
            await Task.Delay(30);
        }
    }

    private sealed class ProcessFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("overshell command ").FullName;

        public ProcessFixture(string? script = null) => File.WriteAllText(Path.Combine(Root, "feed script.ps1"), script ?? """
            param($Log, $RequiredClosed)
            $ErrorActionPreference = 'Stop'
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            if ($RequiredClosed -and -not [IO.File]::Exists($RequiredClosed)) { throw 'Previous process has not shut down' }
            [IO.File]::AppendAllText($Log + '.starts', [string]$PID + "`n")
            $first = $true
            while ($null -ne ($line = [Console]::ReadLine())) {
                $message = $line | ConvertFrom-Json
                if ($message.type -eq 'shutdown') { break }
                [IO.File]::AppendAllText($Log, $line + "`n", [Text.UTF8Encoding]::new($false))
                if ($first) {
                    [Console]::WriteLine('{"type":"command","command":"test.activate","arguments":{"target":{"id":"tab"}}}')
                    $first = $false
                }
            }
            [IO.File]::WriteAllText($Log + '.closed', 'closed')
            """);

        public AppSettings Settings(string log, string? requiredClosed = null)
        {
            var args = new JsonArray("-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "{configDir}/feed script.ps1", log);
            if (requiredClosed is not null) args.Add(requiredClosed);
            return new AppSettings
            {
                Extensions = new() { ["feed"] = new JsonObject
                {
                    ["process"] = new JsonObject { ["command"] = "powershell.exe", ["args"] = args, ["restartDelayMs"] = 250 },
                } },
            };
        }

        public List<JsonObject> Messages(string path)
        {
            try { return File.Exists(path) ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonNode.Parse(line)!.AsObject()).ToList() : []; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return []; }
        }

        public void Dispose()
        {
            // Windows' console teardown can release its inherited cwd handle just
            // after the killed process is signalled, especially under parallel tests.
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(Root, true); return; }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(25); }
            }
        }
    }

#pragma warning disable CS0067 // The fake implements the entire public host contract.
    private sealed class FeedShell(FeedTab tab) : IShell
    {
        public FeedTab Tab => tab;
        public AppSettings Settings { get; set; } = new();
        public List<string> Traces { get; } = [];
        public IReadOnlyList<ITab> Tabs => [tab];
        public IReadOnlyList<ITab> DisplayOrder => Tabs;
        public ITab? ActiveTab => tab;
        public ITab? TargetTab => tab;
        public ITab? FocusedTab => tab;
        public ITab? Cursor => null;
        public bool IsReady => true;
        public CommandRegistry Commands { get; } = new();
        public ITabBadges Badges => throw new NotSupportedException();
        public INavigationService Navigation => throw new NotSupportedException();
        public ITabAddressing Addresses => throw new NotSupportedException();
        public ITabPresentations Presentations => throw new NotSupportedException();
        public IKeyBindings Keys => throw new NotSupportedException();
        public IHostUi Ui => throw new NotSupportedException();
        public IReadOnlyCollection<AgentRuleSet> AgentRules => [];
        public string ViewId => "terminal";
        public bool SidebarVisible => false;
        public bool DashboardVisible => false;
        public IReadOnlyList<ITab> SidebarOrder => [];
        public Func<NotificationEvent, bool>? NotificationFilter { get; set; }
        public ITab? Find(string id) => id == tab.Id ? tab : null;
        public T ExtensionSettings<T>(string id) where T : class, new() => Settings.ExtensionSettings<T>(id, out _);
        public void Trace(string message) => Traces.Add(message);
        public void Post(Action action) => action();
        public void Beat() => Heartbeat?.Invoke(DateTimeOffset.UtcNow);
        public void Reload() => SettingsChanged?.Invoke();
        public void SetCursor(ITab? target) => throw new NotSupportedException();
        public void ShowView(string id) => throw new NotSupportedException();
        public ITab? OpenTab(TabRequest request) => throw new NotSupportedException();
        public void Activate(ITab target) => throw new NotSupportedException();
        public void Close(ITab target) => throw new NotSupportedException();
        public TabActionResult SetLifetime(string id, TabLifetime lifetime) => throw new NotSupportedException();
        public void TabPropertiesChanged(ITab target) => throw new NotSupportedException();
        public event Action? TabOrderChanged;
        public event Action<ITab?>? FocusedTabChanged;
        public event Action<ITab>? TabOpened;
        public event Action<ITab>? TabClosed;
        public event Action<ITab?>? ActiveTabChanged;
        public event Action<ITab, AgentTransition>? TabStateChanged;
        public event Action<ITab, AgentAttention>? TabAttention;
        public event Action<DateTimeOffset>? Heartbeat;
        public event Action? Ready;
        public event Action? SettingsChanged;
    }

    private sealed class FeedTab : ITab
    {
        public string Id => "tab";
        public string Label { get; set; } = "Unicode ✓ שלום";
        public string Title => Label;
        public string Harness => "opencode";
        public bool IsAgent => true;
        public AgentRuleSet Rules => throw new NotSupportedException();
        public AgentState State { get; set; } = AgentState.Working;
        public bool Unread { get; set; }
        public bool NeedsAttention => Unread || OpenRequest is not null;
        public DateTimeOffset? AttentionSince => null;
        public DateTimeOffset? LastActivity => DateTimeOffset.UtcNow;
        public string WorkingDirectory => "C:\\project";
        public string Project => "project";
        public string? Group => null;
        public bool Detached => false;
        public bool IsRunning => true;
        public bool IsActive => true;
        public string Explain => "test";
        public string? Summary => null;
        public string SessionId => "session";
        public string? ResumeCommand => null;
        public IReadOnlyList<string> ScreenRows => throw new InvalidOperationException("Must not read a screen");
        public OpenRequest? OpenRequest { get; set; }
        public ReplyChannel ReplyChannel => ReplyChannel.None;
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string property) => PropertyChanged?.Invoke(this, new(property));
        public void RequestScreen() => throw new InvalidOperationException("Must not request a screen");
        public void SendText(string text) => throw new NotSupportedException();
        public void Paste(string text) => throw new NotSupportedException();
        public bool Answer(bool approve) => throw new NotSupportedException();
        public void Reply(string text) => throw new NotSupportedException();
        public void ScheduleFirstPrompt(string text) => throw new NotSupportedException();
    }
#pragma warning restore CS0067
}

// Loaded from a private AssemblyLoadContext, rather than instantiated by the test.
public sealed class FeedDll : IExtension
{
    private IShell? _shell;
    public string Id => "test.dll";
    public void Initialize(IShell shell)
    {
        _shell = shell;
        shell.Commands.Register("test.dll.command", "Fixture", "Fixture", () => shell.Trace("DLL command executed"));
    }
    public void Dispose() => _shell?.Commands.Remove("test.dll.command");
}
