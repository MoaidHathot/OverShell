using System.Diagnostics;
using OverShell.Core;
using OverShell.Core.Integrations;
using Xunit;

namespace OverShell.Tests;

public class CodexNotifyProcessTests
{
    [Fact]
    public async Task The_installed_notify_script_accepts_native_argv_with_quotes_and_unicode()
    {
        using var endpoint = new IntegrationEndpoint();
        var received = new TaskCompletionSource<IntegrationReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.ReportReceived += report => received.TrySetResult(report);
        endpoint.Start();
        var directory = Directory.CreateTempSubdirectory("overshell-codex-notify-");
        try
        {
            var script = Path.Combine(directory.FullName, "notify.ps1");
            File.WriteAllText(script, IntegrationInstaller.CodexScriptContent());
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("""{"type":"agent-turn-complete","thread-id":"thread-test","turn-id":"turn-test","cwd":"C:\\work","last-assistant-message":"Done: 'quoted' and \"double\" 中文"}""");
            foreach (var pair in endpoint.EnvironmentFor("test-tab")) start.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            Assert.True(process.ExitCode == 0, await output + await error);
            var report = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("test-tab", report.TabId);
            Assert.Equal("thread-test", report.SessionId);
            Assert.Equal("Done: 'quoted' and \"double\" 中文", report.Summary);
            Assert.True(report.Advisory);
        }
        finally { directory.Delete(recursive: true); }
    }
}
