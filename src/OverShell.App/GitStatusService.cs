using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using OverShell.App.Diagnostics;

namespace OverShell.App;

/// <summary>
/// Runs <c>git status --porcelain</c> per repository root, at most once per interval,
/// off the UI thread, and remembers the answer. Several tabs in one repository share one
/// run. Off unless <c>settings.jsonc</c> enables <c>git.dirty</c>: spawning a process is
/// not free, and this is decoration.
/// </summary>
internal sealed class GitStatusService
{
    private readonly Dictionary<string, (bool? Dirty, DateTimeOffset At, bool Running)> _byRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly bool _gitAvailable;

    public GitStatusService(Dispatcher dispatcher, int intervalMs)
    {
        _dispatcher = dispatcher;
        _interval = TimeSpan.FromMilliseconds(Math.Max(2000, intervalMs));
        _gitAvailable = Core.Integrations.IntegrationInstaller.OnPath("git");
        if (!_gitAvailable)
        {
            TraceLog.Agents.Write("git status: git not on PATH; dirty markers off");
        }
    }

    /// <summary>Raised on the UI thread when a repository's answer changed.</summary>
    public event Action<string>? Updated;

    /// <summary>The last known answer, scheduling a refresh when the interval has passed. Call on the UI thread.</summary>
    public bool? Query(string root, DateTimeOffset now)
    {
        if (!_gitAvailable)
        {
            return null;
        }

        if (!_byRoot.TryGetValue(root, out var entry))
        {
            entry = (null, DateTimeOffset.MinValue, false);
        }

        if (!entry.Running && now - entry.At >= _interval)
        {
            _byRoot[root] = (entry.Dirty, entry.At, true);
            _ = RunAsync(root);
        }

        return entry.Dirty;
    }

    private async Task RunAsync(string root)
    {
        bool? dirty = null;
        try
        {
            var info = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            info.ArgumentList.Add("status");
            info.ArgumentList.Add("--porcelain");

            using var process = Process.Start(info);
            if (process is not null)
            {
                using var timeout = new CancellationTokenSource(5000);
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode == 0)
                {
                    dirty = (await output.ConfigureAwait(false)).Trim().Length > 0;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Unknown stays unknown.
        }

        await _dispatcher.InvokeAsync(() =>
        {
            var previous = _byRoot.GetValueOrDefault(root).Dirty;
            _byRoot[root] = (dirty, DateTimeOffset.Now, false);
            if (previous != dirty)
            {
                Updated?.Invoke(root);
            }
        });
    }
}
