using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.App.Agents;
using OverShell.App.Terminal;
using OverShell.App.Terminal.Hyperlinks;
using OverShell.Config;

namespace OverShell.App;

/// <summary>
/// One terminal tab: a profile, its live session, and the surface that shows it.
/// <para>
/// Each tab keeps its own surface rather than sharing one and swapping sessions. The
/// scrollback buffer lives in the surface, not the session, so sharing a surface would
/// discard history every time you switched tabs.
/// </para>
/// <para>
/// This is the only type that touches <see cref="ITerminalSession"/> and
/// <see cref="ITerminalSurface"/>; the chrome sees a <see cref="View"/> and a handful of
/// properties. Keep it that way — it is what makes a second surface a local change.
/// </para>
/// </summary>
public sealed partial class TerminalTab : INotifyPropertyChanged, IDisposable
{
    // OSC 0 / OSC 2 -> window title. OSC 9;9 -> working directory (shell integration).
    [GeneratedRegex("\u001b\\][02];([^\u0007\u001b]*)(?:\u0007|\u001b\\\\)", RegexOptions.Compiled)]
    private static partial Regex OscTitleRegex { get; }

    [GeneratedRegex("\u001b\\]9;9;\"?([^\"\u0007\u001b]*)\"?(?:\u0007|\u001b\\\\)", RegexOptions.Compiled)]
    private static partial Regex OscCwdRegex { get; }

    // OSC 7 -> file://host/path, the cross-shell convention.
    [GeneratedRegex("\u001b\\]7;file://[^/]*(/[^\u0007\u001b]*)(?:\u0007|\u001b\\\\)", RegexOptions.Compiled)]
    private static partial Regex Osc7CwdRegex { get; }

    private readonly StringBuilder _tail = new();
    private readonly Lock _tailGate = new();
    private readonly TerminalStreamState _stream = new();
    private readonly Dispatcher _dispatcher;

    private string? _shellTitle;
    private string? _workingDirectory;
    private long _outputVersion;
    private bool _isActive;
    private bool _revealed;
    private bool _exitNotified;
    private bool _disposed;
    private DispatcherTimer? _revealTimeout;

    internal TerminalTab(TerminalProfile profile, ColorScheme scheme, Dispatcher dispatcher, AgentServices agents, string? userLabel = null)
    {
        Profile = profile;
        Scheme = scheme;
        _dispatcher = dispatcher;
        _userLabel = string.IsNullOrWhiteSpace(userLabel) ? null : userLabel.Trim();

        Accent = TabAccent.For(profile.Id);
        Background = TerminalThemeMapper.ToBrush(scheme.Background);
        Foreground = TerminalThemeMapper.ToBrush(scheme.Foreground);

        var startingDirectory = ResolveStartingDirectory(profile);

        // Seed the directory so the status bar is useful before the shell emits OSC 7/9;9.
        _workingDirectory = startingDirectory;

        var commandLine = Expand(profile.CommandLine) ?? "powershell.exe";

        // Integrations inside the child find their way back by these variables (§12.4).
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (agents.EnvironmentFor?.Invoke(Id) is { } endpoint)
        {
            foreach (var (key, value) in endpoint)
            {
                environment[key] = value;
            }
        }

        var descriptor = new SessionDescriptor
        {
            CommandLine = commandLine,
            WorkingDirectory = startingDirectory,
            ProfileId = profile.Id,
            Environment = environment,
        };

        // Before the session exists: the stream's signals must have a listener from the first byte.
        InitializeAgent(agents, commandLine);

        (Session, Surface) = TerminalFactory.Create(descriptor);

        Surface.ApplyTheme(scheme, profile);
        Surface.View.Visibility = Visibility.Hidden;

        // Breathing room so glyphs never touch the window chrome. The host grid paints
        // the same background, so this reads as terminal padding.
        Surface.View.Margin = new Thickness(10, 6, 2, 6);

        Session.OutputReceived += OnOutput;
        Session.Started += (_, _) => _dispatcher.BeginInvoke(() => Raise(nameof(IsRunning)));
        Session.Exited += (_, _) => _dispatcher.BeginInvoke(NotifyExited);
        Surface.Ready += (_, _) => MarkRevealed();

        Surface.Attach(Session);

        // Safety net: if the session never signals ready — a bad commandline, a shell
        // that dies instantly — reveal anyway rather than leaving a blank pane.
        _revealTimeout = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _revealTimeout.Tick += (_, _) => MarkRevealed();
        _revealTimeout.Start();

        // Note: no KeyboardNavigation overrides here. Every mode either cycles focus
        // among the control's children or skips past the container — all of them mark
        // the key handled, so the shell never sees Tab. ShortcutRouter claims Tab and
        // the arrow keys and delivers them to the terminal directly instead.
    }

    public TerminalProfile Profile { get; }

    public ColorScheme Scheme { get; }

    public ITerminalSession Session { get; }

    public ITerminalSurface Surface { get; }

    /// <summary>The element the chrome hosts. Shorthand for <c>Surface.View</c>.</summary>
    public FrameworkElement View => Surface.View;

    public Brush Accent { get; }

    public Brush Background { get; }

    /// <summary>The scheme's default foreground — what a hovered link is underlined with.</summary>
    public Brush Foreground { get; }

    /// <summary>What the tab shows: the shell's own title when it is meaningful, else the profile name.</summary>
    public string Title =>
        IsMeaningfulTitle(_shellTitle) ? _shellTitle! : Profile.Name;

    public string? WorkingDirectory => _workingDirectory;

    /// <summary>
    /// Secondary line for the status bar. Prefers a real working directory, and only
    /// falls back to the shell's own title when that title is actually informative
    /// (shells love setting it to their own exe path).
    /// </summary>
    public string StatusDetail
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_workingDirectory))
            {
                return _workingDirectory;
            }

            return IsMeaningfulTitle(_shellTitle) ? _shellTitle! : string.Empty;
        }
    }

    /// <summary>True once the session's process actually exists.</summary>
    public bool HasStarted => Session.HasStarted;

    /// <summary>
    /// True while the shell process is alive. Deliberately false before
    /// <see cref="HasStarted"/> — the session starts on a background thread, so callers
    /// must check <see cref="HasStarted"/> before treating this as "the shell died".
    /// </summary>
    public bool IsRunning => Session.IsRunning;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            UpdateVisibility();
            Raise();

            if (value)
            {
                _dispatcher.BeginInvoke(Surface.Focus, DispatcherPriority.Input);
            }
        }
    }

    /// <summary>
    /// Shows the surface only once it has something to draw.
    /// <para>
    /// A freshly created native terminal owns a child HWND whose swapchain has not
    /// presented a frame yet. That surface composites as fully transparent, so revealing
    /// it immediately flashes the desktop through the window before the first paint
    /// lands. While the view stays hidden the host grid — painted with the profile's
    /// background — shows instead, so the transition is a solid colour throughout.
    /// </para>
    /// </summary>
    private void UpdateVisibility() =>
        View.Visibility = _isActive && _revealed ? Visibility.Visible : Visibility.Hidden;

    private void MarkRevealed()
    {
        if (_revealed)
        {
            return;
        }

        _revealed = true;
        _revealTimeout?.Stop();

        // One turn at Render priority so the engine's first Present completes before the
        // window is unhidden.
        _dispatcher.BeginInvoke(UpdateVisibility, DispatcherPriority.Render);
    }

    public event EventHandler? Exited;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the application in the shell has asked for mouse clicks itself (DECSET 1000/1002/1003).</summary>
    public bool MouseTracking => _stream.MouseTracking;

    /// <summary>
    /// Increments with every chunk the session produces. Lets the chrome tell whether what
    /// is on screen may have changed since it last looked — without knowing what changed.
    /// </summary>
    public long OutputVersion => Volatile.Read(ref _outputVersion);

    /// <summary>Writes text to the shell as if typed, verbatim.</summary>
    public void SendText(string text)
    {
        if (_disposed)
        {
            return;
        }

        Session.WriteInput(text);
    }

    /// <summary>
    /// Pastes the way Windows Terminal does: line endings become CR, other C0 controls
    /// are dropped, and the whole thing is bracketed when the application asked for it
    /// (DECSET 2004) — so a multi-line paste lands as one unit in shells that support it.
    /// </summary>
    public void Paste(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text))
        {
            return;
        }

        var filtered = new StringBuilder(text.Length + 16);

        if (_stream.BracketedPaste)
        {
            filtered.Append("\u001b[200~");
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\r')
            {
                filtered.Append('\r');

                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c == '\n')
            {
                filtered.Append('\r');
            }
            else if (c < ' ' && c != '\t')
            {
                // Other control codes have no business being typed.
            }
            else
            {
                filtered.Append(c);
            }
        }

        if (_stream.BracketedPaste)
        {
            filtered.Append("\u001b[201~");
        }

        Session.WriteInput(filtered.ToString());
    }

    /// <summary>
    /// The link at an offset in a logical line, if any: a URL printed as text, else an OSC 8
    /// hyperlink whose visible text sits there. Pure text logic; safe on any thread.
    /// </summary>
    internal LinkMatch? ResolveLink(string line, int offset) =>
        HyperlinkDetector.MatchUrl(line, offset) ?? _stream.FindOsc8Link(line, offset);

    public string SelectedText() => Surface.GetSelectedText();

    public (int Columns, int Rows) Grid => Surface.Grid;

    /// <summary>Stops the surface from writing any further input into the session.</summary>
    public void SuppressInput() => Session.CloseInput();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _revealTimeout?.Stop();
        _revealTimeout = null;

        Session.OutputReceived -= OnOutput;
        _stream.Signal -= OnSignal;
        if (_hwnd != 0)
        {
            _agents.Screen.Forget(_hwnd);
        }

        // Input first, then the process, then the view: the surface keeps generating
        // focus traffic while it unloads, and that must land on a closed input path.
        Session.Dispose();
        Surface.Dispose();
    }

    // ------------------------------------------------------------ internals

    /// <summary>Runs on the session's I/O thread — keep it cheap and marshal anything UI-facing.</summary>
    private void OnOutput(object? sender, string chunk)
    {
        Interlocked.Increment(ref _outputVersion);
        _stream.Observe(chunk);
        NoteOutput();

        string? title;
        string? cwd;

        lock (_tailGate)
        {
            _tail.Append(chunk);
            if (_tail.Length > 8192)
            {
                _tail.Remove(0, _tail.Length - 8192);
            }

            var text = _tail.ToString();
            var titleMatches = OscTitleRegex.Matches(text);
            var cwdMatches = OscCwdRegex.Matches(text);
            var cwd7Matches = Osc7CwdRegex.Matches(text);

            title = titleMatches.Count > 0 ? titleMatches[^1].Groups[1].Value : null;

            cwd = cwdMatches.Count > 0
                ? cwdMatches[^1].Groups[1].Value
                : cwd7Matches.Count > 0
                    ? Uri.UnescapeDataString(cwd7Matches[^1].Groups[1].Value).TrimStart('/').Replace('/', '\\')
                    : null;
        }

        var titleChanged = title is not null && title != _shellTitle;
        var cwdChanged = cwd is not null && cwd != _workingDirectory;

        if (!titleChanged && !cwdChanged)
        {
            return;
        }

        if (titleChanged)
        {
            _shellTitle = title;
        }

        if (cwdChanged)
        {
            _workingDirectory = cwd;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (titleChanged)
            {
                Raise(nameof(Title));
                OnTitleChanged(title!);
            }

            if (cwdChanged)
            {
                Raise(nameof(WorkingDirectory));
                Raise(nameof(Project));
                Raise(nameof(ProjectAndBranch));
                Raise(nameof(Detail));
                Raise(nameof(SidebarDetail));
                Raise(nameof(Tooltip));
            }

            Raise(nameof(StatusDetail));
        });
    }

    /// <summary>
    /// Shells habitually set the title to their own executable path, which makes a
    /// useless tab label. Prefer the profile name in that case.
    /// </summary>
    private bool IsMeaningfulTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.Equals(title, Profile.CommandLine?.Trim('"'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Raised once, the first time the shell process is observed to have exited.</summary>
    internal void NotifyExited()
    {
        // A tab the user closed is not news; the exit arrives on the dispatcher after Dispose.
        if (_exitNotified || _disposed)
        {
            return;
        }

        _exitNotified = true;

        // The shell is gone; further keystrokes would hit a dead pseudoconsole.
        SuppressInput();

        Agent.OnExit(Session.ExitCode, DateTimeOffset.Now);

        Raise(nameof(IsRunning));
        Raise(nameof(Title));
        RaiseAgentProperties();
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property ?? string.Empty));

    private static string? Expand(string? value) =>
        value is null ? null : Environment.ExpandEnvironmentVariables(value);

    private static string ResolveStartingDirectory(TerminalProfile profile)
    {
        var candidate = Expand(profile.StartingDirectory);

        return !string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)
            ? candidate
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
