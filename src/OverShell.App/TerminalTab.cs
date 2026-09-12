using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using OverShell.Config;

namespace OverShell.App;

/// <summary>
/// One terminal tab: a profile, its live PTY session, and the control that renders it.
/// <para>
/// Each tab keeps its own <see cref="EasyTerminalControl"/> rather than sharing one and
/// swapping connections. The scrollback buffer lives in the control, not the PTY, so
/// sharing a control would discard history every time you switched tabs.
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

    /// <summary>Curated hues so tabs are told apart at a glance without looking noisy.</summary>
    private readonly StringBuilder _tail = new();
    private readonly Lock _tailGate = new();
    private readonly Dispatcher _dispatcher;

    private string? _shellTitle;
    private string? _workingDirectory;
    private bool _isActive;
    private bool _revealed;
    private bool _inputSuppressed;
    private bool _exitNotified;
    private bool _disposed;
    private DispatcherTimer? _revealTimeout;

    public TerminalTab(TerminalProfile profile, ColorScheme scheme, Dispatcher dispatcher)
    {
        Profile = profile;
        Scheme = scheme;
        _dispatcher = dispatcher;

        Accent = TabAccent.For(profile.Id);
        Background = TerminalThemeMapper.ToBrush(scheme.Background);

        var startingDirectory = ResolveStartingDirectory(profile);

        // Seed the directory so the status bar is useful before the shell emits OSC 7/9;9.
        _workingDirectory = startingDirectory;

        View = new EasyTerminalControl
        {
            StartupCommandLine = Expand(profile.CommandLine) ?? "powershell.exe",
            WorkingDirectory = startingDirectory,
            FontFamilyWhenSettingTheme = new FontFamily(profile.FontFace ?? "Cascadia Mono, Consolas"),
            FontSizeWhenSettingTheme = (int)Math.Round(profile.FontSize ?? 12),
            Theme = scheme.ToTerminalTheme(profile.CursorShape),
            Visibility = Visibility.Hidden,

            // Breathing room so glyphs never touch the window chrome. The host grid
            // paints the same background, so this reads as terminal padding.
            Margin = new Thickness(10, 6, 2, 6),
        };

        if (View.ConPTYTerm is { } pty)
        {
            pty.InterceptOutputToUITerminal = OnOutput;
            pty.TermReady += (_, _) => _dispatcher.BeginInvoke(() =>
            {
                Raise(nameof(IsRunning));
                MarkRevealed();
            });
        }

        // Safety net: if the pseudoconsole never signals ready — a bad commandline, a
        // shell that dies instantly — reveal anyway rather than leaving a blank pane.
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

    public EasyTerminalControl View { get; }

    public Brush Accent { get; }

    public Brush Background { get; }

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

    /// <summary>True once the pseudoconsole has actually been started.</summary>
    public bool HasStarted => View.ConPTYTerm?.TermProcIsStarted == true;

    /// <summary>
    /// True while the shell process is alive. Deliberately false before
    /// <see cref="HasStarted"/> — the PTY starts on a background thread, so callers must
    /// check <see cref="HasStarted"/> before treating this as "the shell died".
    /// </summary>
    public bool IsRunning => HasStarted && View.ConPTYTerm?.Process?.HasExited == false;

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
                _dispatcher.BeginInvoke(() => View.Focus(), DispatcherPriority.Input);
            }
        }
    }

    /// <summary>
    /// Shows the control only once it has something to draw.
    /// <para>
    /// A freshly created terminal owns a child HWND whose swapchain has not presented a
    /// frame yet. That surface composites as fully transparent, so revealing it
    /// immediately flashes the desktop through the window before the first paint lands.
    /// While the control stays hidden the host grid — painted with the profile's
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

    /// <summary>
    /// Writes text to the shell as if typed. Note this goes through the raw
    /// <c>WriteToTerm</c>, which — unlike the control's own input path — does not consult
    /// the read-only flag, so it needs its own guard.
    /// </summary>
    public void SendText(string text)
    {
        if (_inputSuppressed || _disposed || !HasStarted)
        {
            return;
        }

        try
        {
            View.ConPTYTerm?.WriteToTerm(text);
        }
        catch (InvalidOperationException)
        {
            // The session ended between the guard and the write.
            SuppressInput();
        }
    }

    public string SelectedText() => View.Terminal?.GetSelectedText() ?? string.Empty;

    public (int Columns, int Rows) Grid =>
        View.Terminal is { } t ? (t.Columns, t.Rows) : (0, 0);

    /// <summary>
    /// Stops the terminal control from writing any further input into the pseudoconsole.
    /// <para>
    /// The control keeps its <c>Connection</c> reference and keeps delivering focus and
    /// key messages after a session ends or is torn down. <c>TermPTY.WriteToTerm</c>
    /// throws once its writers are gone, and <c>ITerminalConnection.WriteInput</c> only
    /// checks the read-only flag — so setting it is the one supported way to make those
    /// late writes harmless.
    /// </para>
    /// </summary>
    public void SuppressInput()
    {
        if (_inputSuppressed)
        {
            return;
        }

        _inputSuppressed = true;

        // updateCursor: false — hiding the cursor would emit VT back through the very
        // path we are trying to shut down.
        try
        {
            View.ConPTYTerm?.SetReadOnly(true, updateCursor: false);
        }
        catch
        {
            // Nothing useful to do; we are already on a teardown path.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _revealTimeout?.Stop();
        _revealTimeout = null;

        // Order matters: the writers must stop being reachable before they are closed.
        SuppressInput();

        var pty = View.ConPTYTerm;
        try { pty?.CloseStdinToApp(); } catch { /* tearing down */ }
        try { pty?.StopExternalTermOnly(); } catch { /* tearing down */ }
    }

    // ------------------------------------------------------------ internals

    /// <summary>Runs on the PTY read thread — keep it cheap and marshal anything UI-facing.</summary>
    private void OnOutput(ref Span<char> chunk)
    {
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
            if (titleChanged)
            {
                Raise(nameof(Title));
            }

            if (cwdChanged)
            {
                Raise(nameof(WorkingDirectory));
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
        if (_exitNotified)
        {
            return;
        }

        _exitNotified = true;

        // The shell is gone; further keystrokes would hit a dead pseudoconsole.
        SuppressInput();

        Raise(nameof(IsRunning));
        Raise(nameof(Title));
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
