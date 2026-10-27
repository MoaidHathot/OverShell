using System.Diagnostics;
using System.IO;
using OverShell.Core;
using OverShell.Core.Integrations;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    /// <summary>Two real V2 clients, scratch config/database, metadata operations only; no model requests.</summary>
    private static async Task RunOpenCodeLabelsAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (opencode-labels) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var root = Path.Combine(AppPaths.StateRoot, "opencode-label-check");
        // Keep project discovery out of the user's home-directory config ancestry.
        var project = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts", "selftest", "opencode-labels", Guid.NewGuid().ToString("N"), "project")).FullName;
        var configHome = Path.Combine(root, "config");
        var config = Directory.CreateDirectory(Path.Combine(configHome, "opencode")).FullName;
        var oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Check(IntegrationInstaller.Install("opencode").Current, "the installed bundle contains both V2 entrypoints");
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig); }
        File.WriteAllText(Path.Combine(config, "opencode.json"), """{"$schema":"https://opencode.ai/config.json","update":"disable","warming":false}""");

        var fixture = Directory.CreateDirectory(Path.Combine(root, "fixture")).FullName;
        File.WriteAllText(Path.Combine(fixture, "tui.ts"), OpenCodeLabelFixture);
        File.WriteAllText(Path.Combine(fixture, "index.ts"), IntegrationInstaller.OpenCodeV2IndexContent().Replace("overshell.v2", "overshell.label-fixture-server", StringComparison.Ordinal));
        var cli = Jsonc.Serialize(new { plugins = new[] { new Uri(fixture).AbsoluteUri }, highlights = false, terminal = new { title = false } });
        var clients = new List<TerminalTab>();
        try
        {
            var one = Launch("Client A");
            await UntilAsync(() => File.Exists(Control("Client A", "ready.json")) || File.Exists(Control("Client A", "error.txt")), 45);
            Check(File.Exists(Control("Client A", "ready.json")), "first real OpenCode client loaded the fixture");
            if (!pass) return;
            var two = Launch("Client B");
            await UntilAsync(() => File.Exists(Control("Client B", "ready.json")) || File.Exists(Control("Client B", "error.txt")), 45);
            Check(File.Exists(Control("Client B", "ready.json")), "second real OpenCode client loaded the fixture");
            if (!pass) return;
            Check(await UntilAsync(() => one.Label == "Client A first" && two.Label == "Client B first", 20), "two real clients in one project report independent session titles with terminal-title output disabled");
            Check(one.SessionId is not null && two.SessionId is not null && one.SessionId != two.SessionId, "the reported session identities belong to different clients");

            File.WriteAllText(Control("Client A", "action.json"), """{"id":1,"action":"rename"}""");
            Check(await UntilAsync(() => one.Label == "Client A renamed", 15), "a real session rename updates the tab without running a turn");
            Check(two.Label == "Client B first", "renaming one client cannot rename the other tab");
            var previous = one.SessionId;
            File.WriteAllText(Control("Client A", "action.json"), """{"id":2,"action":"switch"}""");
            Check(await UntilAsync(() => one.Label == "Client A second" && one.SessionId != previous, 15), "navigating to an existing session updates the title and resume identity");
            File.WriteAllText(Control("Client A", "action.json"), """{"id":3,"action":"home"}""");
            Check(await UntilAsync(() => one.SessionId is null && one.SessionTitle is null, 15), "returning home clears the previous session identity and title");
            Check(two.Label == "Client B first", "the other client's identity remains unchanged throughout navigation");
            foreach (var name in new[] { "Client A", "Client B" })
                Check(!File.Exists(Control(name, "error.txt")), $"{name} fixture completed without an API error");
        }
        finally
        {
            if (!pass)
            {
                foreach (var tab in clients)
                {
                    tab.RequestScreen();
                    await Task.Delay(150);
                    Log($"  screen [{tab.Id}]: {tab.ScreenText}");
                }
                foreach (var file in Directory.EnumerateFiles(root, "error.txt", SearchOption.AllDirectories)) Log("  fixture error: " + File.ReadAllText(file));
            }
            foreach (var tab in clients) if (window.Tabs.Contains(tab)) window.CloseTab(tab);
        }
        Log($"=== selftest (opencode-labels) result: {(pass ? "ALL PASS" : "FAILED")} ===");

        string Control(string name, string file) => Path.Combine(root, name, file);
        TerminalTab Launch(string name)
        {
            var control = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var launch = Path.Combine(control, "launch.ps1");
            File.WriteAllText(launch, $"""
                Get-ChildItem Env:OPENCODE_* | Remove-Item
                $env:XDG_CONFIG_HOME = '{Ps(configHome)}'
                $env:XDG_DATA_HOME = '{Ps(Path.Combine(root, "data"))}'
                $env:XDG_STATE_HOME = '{Ps(Path.Combine(root, "opencode-state"))}'
                $env:XDG_CACHE_HOME = '{Ps(Path.Combine(root, "cache"))}'
                $env:OPENCODE_DB = '{Ps(Path.Combine(root, "opencode.sqlite"))}'
                $env:OPENCODE_CLI_CONFIG_CONTENT = '{Ps(cli)}'
                $env:OVERSHELL_LABEL_FIXTURE = '{Ps(control)}'
                $env:OVERSHELL_LABEL_NAME = '{Ps(name)}'
                & opencode --standalone '{Ps(project)}'
                """);
            var tab = window.AddTab(first.Profile with { Name = name, StartingDirectory = project, CommandLine = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{launch}\"" }, activate: true);
            clients.Add(tab);
            return tab;
        }
        static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        static async Task<bool> UntilAsync(Func<bool> ready, int seconds)
        {
            var time = Stopwatch.StartNew();
            while (time.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (ready()) return true;
                await Task.Delay(100);
            }
            return false;
        }
    }

    private const string OpenCodeLabelFixture = """
        import { Plugin } from "@opencode/plugin/tui"
        import { writeFileSync, readFileSync, existsSync } from "node:fs"
        import { join } from "node:path"
        export default Plugin.define({
          id: "overshell.label-fixture",
          async setup(context) {
            const root = process.env.OVERSHELL_LABEL_FIXTURE
            const name = process.env.OVERSHELL_LABEL_NAME
            if (!root || !name) return
            let timer
            try {
              const location = context.location ?? context.data.location.default()
              const first = await context.client.session.create({ location, title: name + " first" })
              const second = await context.client.session.create({ location, title: name + " second" })
              await context.data.session.sync(first.id)
              await context.data.session.sync(second.id)
              context.ui.router.navigate({ type: "session", sessionID: first.id })
              writeFileSync(join(root, "ready.json"), JSON.stringify({ first: first.id, second: second.id }))
              let after = 0
              let busy = false
              timer = setInterval(async () => {
                if (busy || !existsSync(join(root, "action.json"))) return
                busy = true
                try {
                  const action = JSON.parse(readFileSync(join(root, "action.json"), "utf8"))
                  if (action.id <= after) return
                  after = action.id
                  if (action.action === "rename") {
                    await context.client.session.update({ sessionID: first.id, title: name + " renamed" })
                    await context.data.session.sync(first.id)
                  } else if (action.action === "switch") context.ui.router.navigate({ type: "session", sessionID: second.id })
                  else if (action.action === "home") context.ui.router.navigate({ type: "home" })
                } catch (error) { writeFileSync(join(root, "error.txt"), String(error)) }
                finally { busy = false }
              }, 100)
            } catch (error) { writeFileSync(join(root, "error.txt"), String(error)) }
            return () => clearInterval(timer)
          },
        })
        """;
}
