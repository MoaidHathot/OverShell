using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using OverShell.App.Diagnostics;
using OverShell.Core;
using OverShell.Core.Notifications;
using OverShell.Core.Settings;

namespace OverShell.App.Notifications;

/// <summary>
/// Fans one attention event out to every enabled sink whose <c>when</c> allows it
/// (DESIGN.md §12.6). Sinks never throw into the UI: a failing command or a missing
/// sound is a trace line, not a dialog.
/// </summary>
internal sealed class NotificationPipeline : IDisposable
{
    private readonly List<(string Name, NotificationSinkConfig Config, Action<NotificationEvent> Send)> _sinks = [];
    private readonly Window _window;
    private readonly TraceLog _trace = TraceLog.Agents;
    private ToastHost? _toasts;
    private TaskbarBadge? _taskbar;

    public NotificationPipeline(Window window, FrameworkElement toastAnchor, Action<string> focusTab, NotificationSettings settings)
    {
        _window = window;

        foreach (var (name, config) in settings.Sinks)
        {
            if (config is null || !config.Enabled)
            {
                continue;
            }

            switch (config.Type.ToLowerInvariant())
            {
                case "overlay":
                    _toasts ??= new ToastHost(window, toastAnchor, focusTab);
                    _sinks.Add((name, config, e => _toasts.Show(e, AccentFor(e.Kind), config.DurationMs)));
                    break;

                case "taskbar":
                    _taskbar ??= new TaskbarBadge(window);
                    _sinks.Add((name, config, e => _taskbar.Flash()));
                    break;

                case "sound":
                    _sinks.Add((name, config, e => PlaySound(config, e)));
                    break;

                case "command":
                    if (string.IsNullOrWhiteSpace(config.Exe))
                    {
                        _trace.Write($"sink '{name}': type command needs \"exe\"");
                        break;
                    }

                    _sinks.Add((name, config, e => _ = RunCommandAsync(name, config, e)));
                    break;

                default:
                    _trace.Write($"sink '{name}': unknown type '{config.Type}'");
                    break;
            }
        }
    }

    /// <summary>Names of the sinks that are live, for diagnostics.</summary>
    public IEnumerable<string> ActiveSinks => _sinks.Select(s => s.Name);

    /// <summary>Toasts currently on screen, for diagnostics.</summary>
    public int VisibleToasts => _toasts?.Toasts.Count ?? 0;

    /// <summary>The toast layer's root element, for rendering in diagnostics.</summary>
    public FrameworkElement? ToastVisual => _toasts?.Content as FrameworkElement;

    public void Publish(NotificationEvent e)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        foreach (var (name, config, send) in _sinks)
        {
            if (!config.When.Allows(e, now))
            {
                continue;
            }

            try
            {
                send(e);
                _trace.Write($"notify {e.Kind} tab={e.TabId} -> {name}");
            }
            catch (Exception ex)
            {
                _trace.Write($"sink '{name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>The user looked at a tab: its toasts are no longer news.</summary>
    public void Viewed(string tabId) => _toasts?.DismissFor(tabId);

    /// <summary>Attention counts changed: the taskbar badge shows how many tabs need the user.</summary>
    public void UpdateBadge(int needingAttention, bool anyBlocked, bool anyError) => _taskbar?.Update(needingAttention, anyBlocked, anyError);

    public void Dispose()
    {
        _toasts?.Close();
        _taskbar?.Clear();
    }

    private static Brush AccentFor(NotificationKind kind) => (Brush)Application.Current.FindResource(kind switch
    {
        NotificationKind.Blocked => "State.Blocked",
        NotificationKind.Error => "State.Error",
        NotificationKind.Exited => "State.Exited",
        _ => "State.Done",
    });

    private void PlaySound(NotificationSinkConfig config, NotificationEvent e)
    {
        var name = config.Sounds.GetValueOrDefault(e.Kind.ToString()) ?? config.Sound ?? "asterisk";
        switch (name.ToLowerInvariant())
        {
            case "asterisk": SystemSounds.Asterisk.Play(); break;
            case "exclamation": SystemSounds.Exclamation.Play(); break;
            case "hand": SystemSounds.Hand.Play(); break;
            case "question": SystemSounds.Question.Play(); break;
            case "beep": SystemSounds.Beep.Play(); break;
            case "none" or "": break;
            default:
                if (File.Exists(name))
                {
                    using var player = new SoundPlayer(name);
                    player.Play();
                }
                else
                {
                    _trace.Write($"sound '{name}' not found");
                }

                break;
        }
    }

    /// <summary>
    /// Spawns the configured executable with templated arguments, off the UI thread, and
    /// optionally the event as JSON on stdin. Never a shell in between, so an argument
    /// with spaces or quotes arrives intact.
    /// </summary>
    private async Task RunCommandAsync(string name, NotificationSinkConfig config, NotificationEvent e)
    {
        var values = e.TemplateValues();
        var info = new ProcessStartInfo(config.Exe!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = config.StdinJson,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in ArgTemplate.Expand(config.Args, values))
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                _trace.Write($"sink '{name}': could not start {config.Exe}");
                return;
            }

            if (config.StdinJson)
            {
                var json = Jsonc.Serialize(new
                {
                    kind = values["kind"],
                    tab = new { id = e.TabId, label = e.TabLabel, harness = e.Harness, harnessName = e.HarnessDisplayName, project = e.Project, cwd = e.WorkingDirectory },
                    message = e.Message,
                    detail = e.Detail,
                    at = e.At,
                });
                await process.StandardInput.WriteAsync(json).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            using var timeout = new CancellationTokenSource(config.TimeoutMs);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            _trace.Write($"sink '{name}': {config.Exe} exited {process.ExitCode}{(error.Length > 0 ? " stderr=" + error.Trim() : string.Empty)}");
        }
        catch (OperationCanceledException)
        {
            _trace.Write($"sink '{name}': {config.Exe} timed out after {config.TimeoutMs} ms");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _trace.Write($"sink '{name}': {config.Exe} failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Taskbar presence through WPF's <see cref="TaskbarItemInfo"/> (ITaskbarList3 under the
/// hood): an overlay badge with the number of tabs needing attention, the button tinted
/// amber while any is blocked, red while any errored, and a flash when the window is not
/// in the foreground.
/// </summary>
internal sealed class TaskbarBadge
{
    private const uint FlashwAll = 0x00000003;
    private const uint FlashwTimerNoFg = 0x0000000C;

    private readonly Window _window;
    private int _lastCount = -1;
    private bool _lastBlocked;
    private bool _lastError;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWInfo
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWInfo info);

    public TaskbarBadge(Window window)
    {
        _window = window;
        window.TaskbarItemInfo ??= new TaskbarItemInfo();
    }

    public void Update(int count, bool anyBlocked, bool anyError)
    {
        if (count == _lastCount && anyBlocked == _lastBlocked && anyError == _lastError)
        {
            return;
        }

        _lastCount = count;
        _lastBlocked = anyBlocked;
        _lastError = anyError;

        var info = _window.TaskbarItemInfo!;
        info.Overlay = count > 0 ? RenderBadge(count, anyBlocked || anyError) : null;
        info.Description = count > 0 ? $"{count} tab(s) need attention" : string.Empty;

        // Progress colour is the one taskbar signal visible from across the room.
        info.ProgressState = anyError ? TaskbarItemProgressState.Error : anyBlocked ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.None;
        info.ProgressValue = anyError || anyBlocked ? 1 : 0;
    }

    public void Flash()
    {
        if (_window.IsActive)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == 0)
        {
            return;
        }

        var info = new FlashWInfo { cbSize = (uint)Marshal.SizeOf<FlashWInfo>(), hwnd = hwnd, dwFlags = FlashwAll | FlashwTimerNoFg, uCount = 3, dwTimeout = 0 };
        _ = FlashWindowEx(ref info);
    }

    public void Clear()
    {
        if (_window.TaskbarItemInfo is { } info)
        {
            info.Overlay = null;
            info.ProgressState = TaskbarItemProgressState.None;
        }
    }

    /// <summary>A 16×16 disc with the count, drawn once per change; the shell scales it onto the button.</summary>
    private static ImageSource RenderBadge(int count, bool urgent)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var fill = (Brush)Application.Current.FindResource(urgent ? "State.Blocked" : "State.Done");
            dc.DrawEllipse(fill, null, new Point(8, 8), 8, 8);

            var text = new FormattedText(
                count > 9 ? "9+" : count.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                count > 9 ? 8 : 10,
                Brushes.Black,
                1.0);
            dc.DrawText(text, new Point(8 - (text.Width / 2), 8 - (text.Height / 2)));
        }

        var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
