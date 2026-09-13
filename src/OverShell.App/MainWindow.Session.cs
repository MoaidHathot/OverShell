using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using OverShell.Config;
using OverShell.Core;
using OverShell.Core.Integrations;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// What survives a restart and what arrives from outside (DESIGN.md §12.11): the session
/// file written on close and every half minute, restored at the next start with agents
/// resumed; and the <c>overshell://</c> requests a second instance hands over through a
/// named pipe before exiting.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan SessionSaveInterval = TimeSpan.FromSeconds(30);

    private DateTimeOffset _lastSessionSave;
    private string _lastSessionJson = string.Empty;
    private bool _restoredSession;

    /// <summary>True when the last start reopened a saved session rather than the default profile.</summary>
    internal bool RestoredSession => _restoredSession;

    // ---------------------------------------------------------------- session

    /// <summary>Reopens the saved tabs, or the default profile when there is nothing to reopen. Returns the view to show.</summary>
    private string RestoreOrOpenDefault()
    {
        var view = _settings.View;
        string? error = null;

        if (_settings.Session.Restore && SessionSnapshot.Load(AppPaths.SessionFile, out error) is { Tabs.Count: > 0 } saved)
        {
            foreach (var savedTab in saved.Tabs)
            {
                var profile = _catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, savedTab.ProfileId, StringComparison.OrdinalIgnoreCase))
                              ?? _catalog.DefaultProfile;
                if (profile is null || !profile.IsLaunchable)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(savedTab.WorkingDirectory) && Directory.Exists(savedTab.WorkingDirectory))
                {
                    profile = profile with { StartingDirectory = savedTab.WorkingDirectory };
                }

                var tab = AddTab(profile, activate: false);
                if (!string.IsNullOrWhiteSpace(savedTab.Label))
                {
                    tab.UserLabel = savedTab.Label;
                }

                tab.Group = savedTab.Group;

                if (_settings.Session.ResumeAgents && savedTab.AgentRunning && !string.IsNullOrWhiteSpace(savedTab.ResumeCommand))
                {
                    tab.ScheduleResume(savedTab.ResumeCommand);
                }
            }

            if (Tabs.Count > 0)
            {
                ActiveTab = Tabs[Math.Clamp(saved.ActiveIndex, 0, Tabs.Count - 1)];
                foreach (var (viewId, layout) in saved.LayoutOverrides)
                {
                    _layoutOverrides[viewId] = layout;
                }

                if (AppSettings.ViewOrder.Contains(saved.View, StringComparer.OrdinalIgnoreCase))
                {
                    view = saved.View;
                }

                _restoredSession = true;
                _trace.Write($"session: restored {Tabs.Count} tab(s) from {AppPaths.SessionFile} (saved {saved.SavedAt:HH:mm:ss})");
            }
        }
        else if (error is not null)
        {
            _trace.Write($"session: {error}");
        }

        if (Tabs.Count == 0 && _catalog.DefaultProfile is { } defaultProfile)
        {
            AddTab(defaultProfile, activate: true);
        }

        return view;
    }

    private SessionSnapshot BuildSessionSnapshot() => new()
    {
        SavedAt = DateTimeOffset.Now,
        View = _viewId,
        ActiveIndex = ActiveTab is { } active ? Math.Max(0, Tabs.IndexOf(active)) : 0,
        Tabs = Tabs.Where(t => t.IsRunning || !t.HasStarted).Select(t => new SavedTab
        {
            ProfileId = t.Profile.Id,
            WorkingDirectory = t.WorkingDirectory,
            Label = t.UserLabel,
            Group = t.Group,
            Harness = t.Harness,
            AgentRunning = t.IsAgent && t.State is not (Core.Agents.AgentState.Exited or Core.Agents.AgentState.Unknown),
            SessionId = t.Agent.SessionId,
            ResumeCommand = t.ResumeCommand,
        }).ToList(),
        LayoutOverrides = new Dictionary<string, string>(_layoutOverrides, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Writes the session file when something changed; on close, unconditionally.</summary>
    internal void SaveSession(bool force = false)
    {
        var snapshot = BuildSessionSnapshot();

        // Compare without the timestamp, or every save would look like a change.
        var comparable = new SessionSnapshot { View = snapshot.View, ActiveIndex = snapshot.ActiveIndex, Tabs = snapshot.Tabs, LayoutOverrides = snapshot.LayoutOverrides };
        var json = Jsonc.Serialize(comparable);
        if (!force && json == _lastSessionJson)
        {
            return;
        }

        if (snapshot.Save(AppPaths.SessionFile, out var error))
        {
            _lastSessionJson = json;
        }
        else
        {
            _trace.Write($"session: could not save: {error}");
        }
    }

    /// <summary>Heartbeat step: the periodic save.</summary>
    private void TickSession(DateTimeOffset now)
    {
        if (now - _lastSessionSave >= SessionSaveInterval)
        {
            _lastSessionSave = now;
            SaveSession();
        }
    }

    // ----------------------------------------------------------- protocol

    /// <summary>Handles one command line's worth of arguments — ours at start, or a second instance's, handed over.</summary>
    internal void HandleArguments(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (ProtocolRequest.Parse(arg) is { } request)
            {
                HandleProtocol(request);
            }
        }
    }

    private void HandleProtocol(ProtocolRequest request)
    {
        _trace.Write($"protocol: {request.Action} {request.Target ?? string.Empty}");

        switch (request.Action)
        {
            case ProtocolAction.Focus when request.Target is { } tabId:
                FocusTabById(tabId);
                break;

            case ProtocolAction.View when request.Target is { } view && AppSettings.ViewOrder.Contains(view, StringComparer.OrdinalIgnoreCase):
                ApplyView(view);
                break;

            case ProtocolAction.New:
                {
                    var profile = request.Query.TryGetValue("profile", out var id)
                        ? _catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase))
                        : null;
                    profile ??= _catalog.DefaultProfile;
                    if (profile is not null)
                    {
                        if (request.Query.TryGetValue("cwd", out var cwd) && Directory.Exists(cwd))
                        {
                            profile = profile with { StartingDirectory = cwd };
                        }

                        AddTab(profile, activate: true);
                    }

                    break;
                }
        }

        BringToFront();
    }

    /// <summary>
    /// Brings the window up. Windows only lets us take the foreground because the second
    /// instance — started by the user's click — granted it with <c>AllowSetForegroundWindow</c>.
    /// </summary>
    internal void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            _ = SetForegroundWindow(hwnd);
        }

        Dispatcher.BeginInvoke(() => ActiveTab?.Surface.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}

/// <summary>
/// One OverShell per user: the first instance listens on a named pipe; a later one hands
/// its arguments over and exits, so a toast click (<c>overshell://focus/…</c>) reaches
/// the window that owns the tab instead of opening a second window.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public SingleInstance()
    {
        // Per user, per session: two users on one machine, or an RDP session, each get their own.
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}".ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16].ToLowerInvariant();
        _pipeName = $"overshell-{hash}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    }

    /// <summary>Raised on a background thread with the handed-over arguments.</summary>
    public event Action<IReadOnlyList<string>>? ArgumentsReceived;

    public event Action<string>? Trace;

    /// <summary>
    /// Tries to hand <paramref name="args"/> to a running instance. True when one answered
    /// (this process should exit); false when we are the first and should run.
    /// </summary>
    public bool TryHandOver(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
            client.Connect(400);

            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

            // The server says who it is, so this process can let it come to the foreground.
            var greeting = reader.ReadLine();
            if (int.TryParse(greeting, out var pid))
            {
                _ = AllowSetForegroundWindow(pid);
            }

            writer.WriteLine(Jsonc.Serialize(args.ToArray()).ReplaceLineEndings(" "));
            _ = reader.ReadLine(); // acknowledgement; the wait keeps the message from being cut off
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Start()
    {
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException e)
            {
                Trace?.Invoke($"single-instance pipe: {e.Message}");
                await Task.Delay(500).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);

                var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is not null && Jsonc.To<string[]>(Jsonc.Parse(line, out _), out _) is { } args)
                {
                    ArgumentsReceived?.Invoke(args);
                }

                await writer.WriteLineAsync("ok").ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away; the next connection gets a fresh server.
            }
            finally
            {
                server.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>
/// The <c>overshell://</c> URL protocol under <c>HKCU\Software\Classes</c> — per user, no
/// elevation. Written only when absent or pointing at another executable.
/// </summary>
internal static class ProtocolRegistration
{
    private const string KeyPath = @"Software\Classes\overshell";

    public static string? RegisteredCommand()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command");
        return key?.GetValue(null) as string;
    }

    public static string ExpectedCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    public static bool IsRegistered(string exePath) =>
        string.Equals(RegisteredCommand(), ExpectedCommand(exePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when the registry was changed.</summary>
    public static bool Register(string exePath)
    {
        if (IsRegistered(exePath))
        {
            return false;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(null, "URL:OverShell Protocol");
        key.SetValue("URL Protocol", string.Empty);
        using (var icon = key.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue(null, $"\"{exePath}\",0");
        }

        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(null, ExpectedCommand(exePath));
        return true;
    }

    public static bool Unregister()
    {
        using var classes = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classes?.OpenSubKey("overshell") is null)
        {
            return false;
        }

        classes.DeleteSubKeyTree("overshell", throwOnMissingSubKey: false);
        return true;
    }
}
