using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using OverShell.App.Chrome;
using OverShell.Config;
using OverShell.Core;
using OverShell.Core.Integrations;
using OverShell.Core.Search;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// What survives a restart and what arrives from outside (DESIGN.md §12.11, §12.13): the
/// session file written as the window closes and every couple of seconds while it runs —
/// so a crash or a power cut loses seconds, not the session — restored at the next start
/// with agents resumed; and the <c>overshell://</c> requests a second instance hands over
/// through a named pipe before exiting.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// How often the live state is compared with the file. Two seconds is the window a
    /// crash can lose; the compare is a few microseconds and the write happens only on change.
    /// </summary>
    private static readonly TimeSpan SessionSaveInterval = TimeSpan.FromSeconds(2);

    private DateTimeOffset _lastSessionSave;
    private string _lastSessionJson = string.Empty;
    private bool _restoredSession;
    private bool _sessionClosed;
    private WindowState _lastVisibleState = WindowState.Normal;
    private SessionSnapshot? _previousSession;
    private readonly List<SavedTab> _recentlyClosed = [];
    private readonly List<(TerminalTab Tab, SavedWindow? Window)> _pendingDetach = [];

    /// <summary>Set by the command line (<c>--fresh</c>) before the window is built: skip the restore once.</summary>
    internal static bool StartFresh { get; set; }

    /// <summary>True when the last start reopened a saved session rather than the default profile.</summary>
    internal bool RestoredSession => _restoredSession;

    /// <summary>The file as it was when this run started, before this run overwrote it; null when there was none.</summary>
    internal SessionSnapshot? PreviousSession => _previousSession;

    /// <summary>Tabs closed by the user, oldest first, carried across restarts.</summary>
    internal IReadOnlyList<SavedTab> RecentlyClosed => _recentlyClosed;

    // ---------------------------------------------------------------- session

    private void InitializeSession()
    {
        // A minimized window reports RestoreBounds but forgets whether it was maximized
        // before; remembering the last visible state keeps the save honest.
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                _lastVisibleState = WindowState;
            }
        };

        _previousSession = SessionSnapshot.Load(AppPaths.SessionFile, out var error);
        if (error is not null)
        {
            _trace.Write($"session: {error}");
        }

        if (_previousSession is not null)
        {
            _recentlyClosed.AddRange(_previousSession.RecentlyClosed.TakeLast(SessionSnapshot.RecentlyClosedLimit));

            // A session the last run never got to archive (it was killed) is archived now,
            // before this run's first save overwrites the only copy.
            if (_previousSession.Interrupted)
            {
                var archived = SessionHistory.Archive(AppPaths.StateRoot, _previousSession, out var archiveError);
                _trace.Write($"session: interrupted session {(archived is null ? $"not archived ({archiveError ?? "nothing new"})" : $"archived to {archived}")}");
            }
        }

        _commands.Register("tab.reopenClosed", "Reopen closed tab", "Tabs", () => ReopenClosedTab(), () => _recentlyClosed.Count > 0, "The most recently closed tab, with its directory and agent session");
        _commands.Register("session.history", "Session history", "Settings", OpenSessionHistory, description: "Recently closed tabs and earlier sessions, to bring back");

        if (_settings.Session.Restore && _settings.Session.RestoreWindows && !StartFresh)
        {
            ApplyPlacement(this, _previousSession?.Window, "main window");
        }

        _trace.Write($"session: restart with Windows - {ApplicationRestart.Apply(_settings.Session.RestartWithWindows)}");

        Loaded += (_, _) =>
        {
            AnnounceRestore();

            // The tear-offs wait for the first layout: a surface moves between windows only
            // once its HWND exists (§12.12), and that happens when the main host lays out.
            if (_pendingDetach.Count > 0)
            {
                Dispatcher.BeginInvoke(RestoreTearOffs, System.Windows.Threading.DispatcherPriority.Background);
            }
        };
    }

    /// <summary>Puts a window where the session file says, clamped to the desktop that exists now (<see cref="WindowPlacement.Clamp"/>).</summary>
    private void ApplyPlacement(Window window, SavedWindow? saved, string what)
    {
        var desktop = new Bounds(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (WindowPlacement.Clamp(saved, desktop, window.MinWidth, window.MinHeight) is not { } bounds)
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        if (saved!.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        // What Win32 was actually asked for, before anything else (a tiling window manager,
        // say) has had a chance to move the window: the evidence that the placement landed.
        window.SourceInitialized += (_, _) =>
        {
            if (GetWindowRect(new WindowInteropHelper(window).Handle, out var rect))
            {
                _trace.Write($"session: {what} created at physical {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
            }
        };

        _trace.Write($"session: {what} placed at {bounds.Left:F0},{bounds.Top:F0} {bounds.Width:F0}x{bounds.Height:F0}{(saved.Maximized ? " maximized" : string.Empty)}" +
                     (bounds.Left != saved.Left || bounds.Top != saved.Top || bounds.Width != saved.Width || bounds.Height != saved.Height ? $" (saved {saved.Left:F0},{saved.Top:F0} {saved.Width:F0}x{saved.Height:F0}, clamped to the desktop)" : string.Empty));
    }

    /// <summary>The status-bar note about how the previous run ended, when that is worth a word.</summary>
    private void AnnounceRestore()
    {
        if (!_restoredSession || _previousSession is not { } saved)
        {
            return;
        }

        var count = $"{Tabs.Count} tab{(Tabs.Count == 1 ? string.Empty : "s")}";
        if (saved.Interrupted)
        {
            ShowStatusMessage($"Restored {count} from an interrupted session (saved {saved.SavedAt.ToLocalTime():HH:mm})");
        }
        else if (saved.CloseReason == SessionCloseReason.SessionEnding)
        {
            ShowStatusMessage($"Restored {count} after Windows signed out or restarted");
        }
    }

    private void RestoreTearOffs()
    {
        foreach (var (tab, placement) in _pendingDetach.ToArray())
        {
            if (Tabs.Contains(tab) && !tab.Detached)
            {
                Detach(tab, placement);
            }
        }

        _pendingDetach.Clear();
    }

    /// <summary>Reopens the saved tabs, or the default profile when there is nothing to reopen. Returns the view to show.</summary>
    private string RestoreOrOpenDefault()
    {
        var view = _settings.View;

        if (StartFresh)
        {
            _trace.Write("session: --fresh, the saved session is not restored (it stays in the history)");
        }
        else if (_settings.Session.Restore && _previousSession is { Tabs.Count: > 0 } saved)
        {
            var plans = SessionRestore.PlanAll(saved.Tabs, saved.ActiveIndex, t => RestoreCommandLine(ProfileFor(t)), _agents.Rules, _settings.Session);
            for (var i = 0; i < saved.Tabs.Count; i++)
            {
                var tab = OpenSavedTab(saved.Tabs[i], activate: false, plans[i]);
                if (tab is not null && saved.Tabs[i].Detached && _settings.Session.RestoreWindows)
                {
                    _pendingDetach.Add((tab, saved.Tabs[i].Window));
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
                _trace.Write($"session: restored {Tabs.Count} tab(s) from {AppPaths.SessionFile} (saved {saved.SavedAt:HH:mm:ss}, {(saved.Interrupted ? "interrupted" : saved.CloseReason?.ToString().ToLowerInvariant() ?? "v1")})");
            }
        }

        if (Tabs.Count == 0 && _catalog.DefaultProfile is { } defaultProfile)
        {
            AddTab(defaultProfile, activate: true);
        }

        return view;
    }

    /// <summary>The saved tab's profile, else the default; null when nothing launchable exists.</summary>
    private TerminalProfile? ProfileFor(SavedTab savedTab)
    {
        var profile = _catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, savedTab.ProfileId, StringComparison.OrdinalIgnoreCase))
                      ?? _catalog.DefaultProfile;
        return profile is { IsLaunchable: true } ? profile : null;
    }

    /// <summary>The command line the planner judges: expanded, as the tab would launch it.</summary>
    private static string? RestoreCommandLine(TerminalProfile? profile) =>
        profile?.CommandLine is { } commandLine ? Environment.ExpandEnvironmentVariables(commandLine) : null;

    /// <summary>
    /// Reopens one remembered tab: its profile (the default when the profile is gone), its
    /// directory when it still exists, its label and group, and — when an agent was
    /// running — the agent, the way <see cref="SessionRestore.Plan"/> says is safe for the
    /// profile: relaunched with resume arguments when the program is the agent, typed into
    /// the shell otherwise. Null when no launchable profile exists at all.
    /// </summary>
    internal TerminalTab? OpenSavedTab(SavedTab savedTab, bool activate, ResumePlan? plan = null)
    {
        var profile = ProfileFor(savedTab);
        if (profile is null)
        {
            return null;
        }

        plan ??= SessionRestore.Plan(savedTab, RestoreCommandLine(profile), _agents.Rules, _settings.Session);

        if (!string.IsNullOrWhiteSpace(savedTab.WorkingDirectory) && Directory.Exists(savedTab.WorkingDirectory))
        {
            profile = profile with { StartingDirectory = savedTab.WorkingDirectory };
        }

        if (plan.Mode == ResumeMode.Relaunch)
        {
            profile = profile with { CommandLine = plan.Command };
        }

        var tab = AddTab(profile, activate);
        if (!string.IsNullOrWhiteSpace(savedTab.Label))
        {
            tab.UserLabel = savedTab.Label;
        }

        tab.Group = savedTab.Group;
        tab.SeedResume(savedTab.SessionId, savedTab.ResumeCommand ?? (plan.Mode == ResumeMode.Typed ? plan.Command : null));

        if (plan.Mode == ResumeMode.Typed)
        {
            tab.ScheduleResume(plan.Command!);
        }

        if (savedTab.AgentRunning)
        {
            _trace.Write($"[{tab.Id}] restore: {savedTab.Harness ?? "agent"} {plan.Mode.ToString().ToLowerInvariant()} - {plan.Reason}{(plan.Command is null ? string.Empty : $": {plan.Command}")}");
        }

        return tab;
    }

    /// <summary>One tab as the session file remembers it.</summary>
    private SavedTab CaptureTab(TerminalTab t, DateTimeOffset? closedAt = null) => new()
    {
        ProfileId = t.Profile.Id,
        WorkingDirectory = t.WorkingDirectory,
        Label = t.UserLabel,
        Group = t.Group,
        Harness = t.Harness,
        AgentRunning = t.IsAgent && t.State is not (Core.Agents.AgentState.Exited or Core.Agents.AgentState.Unknown),
        SessionId = t.Agent.SessionId,
        ResumeCommand = t.ResumeCommand,
        Detached = t.Detached,
        Window = t.Detached ? CaptureWindow(_tearOffs.FirstOrDefault(w => ReferenceEquals(w.Tab, t)), WindowState.Normal) : null,
        ClosedAt = closedAt,
    };

    /// <summary>A window's placement in DIPs, or null before it has been laid out.</summary>
    private static SavedWindow? CaptureWindow(Window? window, WindowState lastVisible)
    {
        if (window is null)
        {
            return null;
        }

        var maximized = (window.WindowState == WindowState.Minimized ? lastVisible : window.WindowState) == WindowState.Maximized;
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || double.IsNaN(bounds.X) || double.IsNaN(bounds.Y))
        {
            return null;
        }

        return new SavedWindow { Left = bounds.X, Top = bounds.Y, Width = bounds.Width, Height = bounds.Height, Maximized = maximized };
    }

    private SessionSnapshot BuildSessionSnapshot(SessionCloseReason? reason) => new()
    {
        SavedAt = DateTimeOffset.Now,
        CloseReason = reason,
        View = _viewId,
        ActiveIndex = ActiveTab is { } active ? Math.Max(0, Tabs.IndexOf(active)) : 0,
        Window = CaptureWindow(this, _lastVisibleState),
        Tabs = Tabs.Where(t => t.IsRunning || !t.HasStarted).Select(t => CaptureTab(t)).ToList(),
        LayoutOverrides = new Dictionary<string, string>(_layoutOverrides, StringComparer.OrdinalIgnoreCase),
        RecentlyClosed = _recentlyClosed.TakeLast(SessionSnapshot.RecentlyClosedLimit).ToList(),
    };

    /// <summary>
    /// Writes the session file when something changed; with a <paramref name="reason"/>,
    /// unconditionally — that is the window closing, and nothing is written after it, so
    /// a late heartbeat cannot turn "closed" back into "running".
    /// </summary>
    internal void SaveSession(bool force = false, SessionCloseReason? reason = null)
    {
        if (_sessionClosed)
        {
            return;
        }

        var snapshot = BuildSessionSnapshot(reason);
        var json = snapshot.ComparableJson();
        if (!force && reason is null && json == _lastSessionJson)
        {
            return;
        }

        if (snapshot.Save(AppPaths.SessionFile, out var error))
        {
            _lastSessionJson = json;
            _sessionClosed = reason is not null;
            if (reason is not null)
            {
                var archived = SessionHistory.Archive(AppPaths.StateRoot, snapshot, out var archiveError);
                _trace.Write($"session: {reason.ToString()!.ToLowerInvariant()}, {(archived is null ? $"not archived ({archiveError ?? "nothing new"})" : $"archived to {archived}")}");
            }
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

    // ---------------------------------------------------------------- history

    /// <summary>A tab the user is closing: remembered for <c>tab.reopenClosed</c> and the history picker. Called before the tab is disposed.</summary>
    private void RememberClosed(TerminalTab tab)
    {
        // A shell that already exited has nothing to bring back but its directory - still
        // worth a line; a tab that never started (bad command line) has not even that.
        if (!tab.HasStarted)
        {
            return;
        }

        _recentlyClosed.Add(CaptureTab(tab, DateTimeOffset.Now));
        if (_recentlyClosed.Count > SessionSnapshot.RecentlyClosedLimit)
        {
            _recentlyClosed.RemoveRange(0, _recentlyClosed.Count - SessionSnapshot.RecentlyClosedLimit);
        }
    }

    /// <summary>Reopens the newest closed tab (or <paramref name="entry"/>), activated, resuming its agent the way a restore would.</summary>
    internal TerminalTab? ReopenClosedTab(SavedTab? entry = null)
    {
        entry ??= _recentlyClosed.LastOrDefault();
        if (entry is null)
        {
            return null;
        }

        _recentlyClosed.Remove(entry);
        var tab = OpenSavedTab(entry, activate: true);
        if (tab is null)
        {
            ShowStatusMessage("No launchable profile to reopen the tab with");
            return null;
        }

        _trace.Write($"[{tab.Id}] reopened closed tab ({entry.Label ?? entry.Harness ?? entry.WorkingDirectory ?? "tab"}, closed {entry.ClosedAt:HH:mm:ss})");
        return tab;
    }

    /// <summary>Adds every tab of an archived session next to the current ones, agents resumed the way a restore would.</summary>
    internal int ReopenSession(ArchivedSession session)
    {
        var plans = SessionRestore.PlanAll(session.Tabs, -1, t => RestoreCommandLine(ProfileFor(t)), _agents.Rules, _settings.Session);
        TerminalTab? first = null;
        var opened = 0;
        for (var i = 0; i < session.Tabs.Count; i++)
        {
            var tab = OpenSavedTab(session.Tabs[i], activate: false, plans[i]);
            if (tab is not null)
            {
                first ??= tab;
                opened++;
            }
        }

        if (first is not null)
        {
            ActiveTab = first;
        }

        _trace.Write($"session: reopened {opened} tab(s) from {session.Path}");
        ShowStatusMessage($"Reopened {opened} tab{(opened == 1 ? string.Empty : "s")} from the session saved {session.SavedAt.ToLocalTime():HH:mm}");
        return opened;
    }

    /// <summary>The picker: recently closed tabs first (newest on top), then earlier sessions, newest first.</summary>
    private void OpenSessionHistory()
    {
        if (_palette is { IsVisible: true })
        {
            _palette.Close();
            return;
        }

        _palette = PaletteWindow.Picker(this, "\uE81C", "Nothing closed yet, no earlier sessions", BuildHistoryItems); // history glyph
        _palette.Closed += (_, _) =>
        {
            _palette = null;
            ActiveTab?.Surface.Focus();
        };
    }

    /// <summary>What a saved tab is called in the picker: its label, else its harness, else its profile.</summary>
    private string NameOf(SavedTab tab) =>
        tab.Label ?? (tab.Harness is { } h ? _agents.Rules.Find(h)?.DisplayName ?? h : null)
        ?? _catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, tab.ProfileId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? "tab";

    internal IReadOnlyList<PaletteItem> BuildHistoryItems(PaletteQuery query)
    {
        var items = new List<(int Score, int Order, PaletteItem Item)>();
        var order = 0;

        foreach (var entry in Enumerable.Reverse(_recentlyClosed))
        {
            var name = NameOf(entry);
            var detail = $"closed {entry.ClosedAt?.ToLocalTime():HH:mm}{(entry.AgentRunning ? " · agent was running" : string.Empty)}{(entry.WorkingDirectory is null ? string.Empty : $"  ·  {entry.WorkingDirectory}")}";
            var score = FuzzyMatcher.Score(query.Text, name, entry.WorkingDirectory ?? string.Empty, entry.Harness ?? string.Empty);
            if (score is null)
            {
                continue;
            }

            var captured = entry;
            items.Add((score.Value + 1000, order++, new PaletteItem
            {
                Title = name,
                Detail = detail,
                Glyph = "\uE8A7", // reopen (open-in-new)
                Invoke = () => ReopenClosedTab(captured),
            }));
        }

        foreach (var session in SessionHistory.List(AppPaths.StateRoot))
        {
            var when = session.SavedAt.ToLocalTime();
            var how = session.Interrupted ? "interrupted" : session.CloseReason == SessionCloseReason.SessionEnding ? "Windows signed out" : "closed";
            var title = $"Session {how} {when:ddd HH:mm}";
            var detail = SessionSnapshot.Describe(session.Tabs, NameOf);
            var score = FuzzyMatcher.Score(query.Text, title, detail, how);
            if (score is null)
            {
                continue;
            }

            var captured = session;
            items.Add((score.Value, order++, new PaletteItem
            {
                Title = title,
                Detail = detail,
                Glyph = session.Interrupted ? "\uE7BA" : "\uE81C", // warning / history
                Invoke = () => ReopenSession(captured),
            }));
        }

        return items.OrderByDescending(i => i.Score).ThenBy(i => i.Order).Select(i => i.Item).ToList();
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Win32Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left, Top, Right, Bottom;
    }
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
