using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Extensibility;

namespace OverShell.App.Extensibility;

/// <summary>A resident command with a bounded, change-driven JSON-lines feed on stdin.</summary>
internal sealed class CommandExtension(string id, string configDirectory, string stateDirectory, Task? predecessor = null) : IExtension
{
    private readonly Channel<string> _snapshots = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly Channel<(bool Error, string Line)> _output = Channel.CreateBounded<(bool, string)>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly HashSet<ITab> _watched = [];
    private IShell _shell = null!;
    private CancellationTokenSource? _runCancellation;
    private string? _launchSettings;
    private volatile string? _latestSnapshot;
    private bool _dirty = true;
    private bool _disposed;

    public string Id => id;
    public Task Completion { get; private set; } = predecessor ?? Task.CompletedTask;

    public void Initialize(IShell shell)
    {
        _shell = shell;
        shell.TabOpened += OnOpened;
        shell.TabClosed += OnClosed;
        shell.TabOrderChanged += MarkDirty;
        shell.TabStateChanged += OnStateChanged;
        shell.ActiveTabChanged += OnFocusChanged;
        shell.FocusedTabChanged += OnFocusChanged;
        shell.SettingsChanged += OnSettingsChanged;
        shell.Heartbeat += OnHeartbeat;
        foreach (var tab in shell.Tabs) OnOpened(tab);
        OnSettingsChanged();
    }

    private void OnOpened(ITab tab)
    {
        if (_watched.Add(tab)) tab.PropertyChanged += OnPropertyChanged;
        _dirty = true;
    }

    private void OnClosed(ITab tab)
    {
        if (_watched.Remove(tab)) tab.PropertyChanged -= OnPropertyChanged;
        _dirty = true;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ExtensionSnapshot.IncludesProperty(args.PropertyName)) _dirty = true;
    }

    private void MarkDirty() => _dirty = true;
    private void OnStateChanged(ITab tab, AgentTransition transition) => _dirty = true;
    private void OnFocusChanged(ITab? tab) => _dirty = true;

    private void OnSettingsChanged()
    {
        if (_disposed) return;
        try { ApplySettings(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _shell.Trace($"extension {Id}: invalid process settings: {e.Message}");
        }
    }

    private void ApplySettings()
    {
        _dirty = true;
        Publish();
        var node = _shell.Settings.Extensions.GetValueOrDefault(Id)?["process"];
        var serialized = node?.ToJsonString();
        if (serialized == _launchSettings) return;
        var settings = Jsonc.To<ProcessExtensionSettings>(node, out var error);
        if (settings is null)
        {
            _shell.Trace($"extension {Id}: invalid process settings: {error ?? "missing process"}");
            return;
        }
        // Validate on the UI thread, but start/build and all pipe I/O happen in the worker.
        var start = settings.StartInfo(Id, configDirectory, stateDirectory);
        _launchSettings = serialized;
        CancelRun();
        var previous = Completion;
        var cancellation = _runCancellation = new CancellationTokenSource();
        Completion = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                await RunAsync(settings, start, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally { cancellation.Dispose(); }
        });
    }

    private void OnHeartbeat(DateTimeOffset now)
    {
        if (_disposed) return;
        Publish();
        for (var i = 0; i < 32 && _output.Reader.TryRead(out var output); i++)
        {
            if (output.Error)
            {
                _shell.Trace($"extension {Id}: {output.Line}");
                continue;
            }
            try
            {
                if (JsonNode.Parse(output.Line) is not JsonObject message) continue;
                switch (message["type"]?.GetValue<string>())
                {
                    case "command" when message["command"]?.GetValue<string>() is { Length: > 0 } command:
                        if (!_shell.Commands.TryExecute(command, message["arguments"] as JsonObject))
                            _shell.Trace($"extension {Id}: command '{command}' declined");
                        break;
                    case "resync":
                        _latestSnapshot = null;
                        _dirty = true;
                        break;
                    case "log":
                        _shell.Trace($"extension {Id}: {message["message"]?.ToString()}");
                        break;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _shell.Trace($"extension {Id}: output ignored: {e.Message}");
            }
        }
    }

    private void Publish()
    {
        if (!_dirty) return;
        _dirty = false;
        var json = ExtensionSnapshot.Create(_shell, Id).ToJsonString();
        if (json == _latestSnapshot) return;
        _latestSnapshot = json;
        _snapshots.Writer.TryWrite(json);
    }

    private async Task RunAsync(ProcessExtensionSettings settings, ProcessStartInfo start, CancellationToken cancellation)
    {
        var delay = Math.Clamp(settings.RestartDelayMs, 250, 60_000);
        while (!cancellation.IsCancellationRequested)
        {
            var began = Stopwatch.GetTimestamp();
            try
            {
                await RunOnceAsync(settings, start, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException && e is not OperationCanceledException)
            {
                Log($"process failed: {e.Message}");
            }
            if (cancellation.IsCancellationRequested || !settings.Restart) return;
            if (Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(30)) delay = Math.Clamp(settings.RestartDelayMs, 250, 60_000);
            Log($"restarting in {delay} ms");
            await Task.Delay(delay, cancellation).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 60_000);
        }
    }

    private async Task RunOnceAsync(ProcessExtensionSettings settings, ProcessStartInfo start, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start command");
        ExtensionProcessJob? job = null;
        using var io = new CancellationTokenSource();
        using var writing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task? writer = null;
        Task[] readers = [];
        try
        {
            job = new ExtensionProcessJob(process);
            Log($"started pid={process.Id}");
            readers = [ReadOutputAsync(process.StandardOutput, false, io.Token), ReadOutputAsync(process.StandardError, true, io.Token)];
            writer = WriteSnapshotsAsync(process.StandardInput, writing.Token);
            var exited = process.WaitForExitAsync();
            var stopped = Task.Delay(Timeout.Infinite, writing.Token);
            var finished = await Task.WhenAny(exited, stopped, writer).ConfigureAwait(false);
            if (finished == writer && !writer.IsCanceled) await writer.ConfigureAwait(false);
            if (process.HasExited) Log($"exited with code {process.ExitCode}");
        }
        finally
        {
            writing.Cancel();
            if (writer is not null)
            {
                try { await writer.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(settings.ShutdownTimeoutMs, 100, 5000))).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or TimeoutException)
                {
                    // A child that stopped reading can leave a synchronous anonymous-pipe
                    // write blocked even after cancellation. Closing its job releases it.
                    if (!writer.IsCompleted) job?.Dispose();
                }
            }
            // EOF is the shutdown signal even for scripts that do not parse the final line.
            using var timeout = new CancellationTokenSource(Math.Clamp(settings.ShutdownTimeoutMs, 100, 5000));
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("{\"type\":\"shutdown\",\"protocol\":1}".AsMemory(), timeout.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { }
            finally
            {
                // Closing the job also reaps grandchildren if the launcher exited first.
                job?.Dispose();
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
                if (writer is not null)
                {
                    try { await writer.ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
                io.Cancel();
                foreach (var reader in readers)
                {
                    try { await reader.ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        }
    }

    private async Task WriteSnapshotsAsync(StreamWriter input, CancellationToken cancellation)
    {
        while (_snapshots.Reader.TryRead(out _)) { }
        if (_latestSnapshot is { } initial)
        {
            await input.WriteLineAsync(initial.AsMemory(), cancellation).ConfigureAwait(false);
            await input.FlushAsync(cancellation).ConfigureAwait(false);
        }
        await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
        {
            await input.WriteLineAsync(snapshot.AsMemory(), cancellation).ConfigureAwait(false);
            await input.FlushAsync(cancellation).ConfigureAwait(false);
        }
    }

    private async Task ReadOutputAsync(StreamReader reader, bool error, CancellationToken cancellation)
    {
        // Keep even an accidental unterminated log line bounded (65,536 characters).
        var buffer = new char[4096];
        var line = new System.Text.StringBuilder();
        var overflow = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (!overflow && line.Length > 0) _output.Writer.TryWrite((error, line.ToString().TrimEnd('\r')));
                    line.Clear();
                    overflow = false;
                }
                else if (line.Length < 65536) line.Append(buffer[i]);
                else overflow = true;
            }
        }
        if (!overflow && line.Length > 0) _output.Writer.TryWrite((error, line.ToString()));
    }

    private void Log(string message) => _output.Writer.TryWrite((true, message));

    private void CancelRun()
    {
        // A non-restarting command may have completed and disposed its token already.
        try { _runCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_shell is not null)
        {
            _shell.TabOpened -= OnOpened;
            _shell.TabClosed -= OnClosed;
            _shell.TabOrderChanged -= MarkDirty;
            _shell.TabStateChanged -= OnStateChanged;
            _shell.ActiveTabChanged -= OnFocusChanged;
            _shell.FocusedTabChanged -= OnFocusChanged;
            _shell.SettingsChanged -= OnSettingsChanged;
            _shell.Heartbeat -= OnHeartbeat;
        }
        foreach (var tab in _watched) tab.PropertyChanged -= OnPropertyChanged;
        _watched.Clear();
        CancelRun();
        _snapshots.Writer.TryComplete();
    }
}
