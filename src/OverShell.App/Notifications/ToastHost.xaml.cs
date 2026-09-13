using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.Core.Notifications;

namespace OverShell.App.Notifications;

/// <summary>One in-window toast. Clicking it focuses the tab it is about.</summary>
public sealed class Toast
{
    public required string TabId { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    public string? Detail { get; init; }

    public required Brush Accent { get; init; }

    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;

    internal DispatcherTimer? Timer { get; set; }
}

/// <summary>
/// The in-app toast layer: an owned, non-activating window pinned to the top-right corner
/// of the terminal area. A separate HWND because nothing WPF draws inside the main window
/// can appear over the native terminal (airspace); non-activating so a toast arriving
/// while the user types never steals a keystroke. Follows the owner as it moves.
/// </summary>
public partial class ToastHost : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int MaxVisible = 4;

    private readonly Window _owner;
    private readonly FrameworkElement _anchor;
    private readonly Action<string> _focusTab;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(nint hwnd, int index, int value);

    public ToastHost(Window owner, FrameworkElement anchor, Action<string> focusTab)
    {
        InitializeComponent();
        Owner = owner;
        _owner = owner;
        _anchor = anchor;
        _focusTab = focusTab;
        Items.ItemsSource = Toasts;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _ = SetWindowLongW(hwnd, GwlExStyle, GetWindowLongW(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };

        owner.LocationChanged += (_, _) => Reposition();
        owner.SizeChanged += (_, _) => Reposition();
        owner.StateChanged += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    public ObservableCollection<Toast> Toasts { get; } = [];

    /// <summary>Shows a toast for <paramref name="durationMs"/>; the oldest goes when there are too many.</summary>
    public void Show(NotificationEvent e, Brush accent, int durationMs)
    {
        var values = e.TemplateValues();
        var toast = new Toast
        {
            TabId = e.TabId,
            Title = values["title"],
            Message = e.Message,
            Detail = e.Detail ?? e.WorkingDirectory,
            Accent = accent,
        };

        while (Toasts.Count >= MaxVisible)
        {
            Remove(Toasts[0]);
        }

        toast.Timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(1000, durationMs)) };
        toast.Timer.Tick += (_, _) => Remove(toast);
        toast.Timer.Start();

        Toasts.Add(toast);
        Reposition();

        if (!IsVisible)
        {
            base.Show();
        }
    }

    /// <summary>Removes every toast about a tab — it was looked at, or closed.</summary>
    public void DismissFor(string tabId)
    {
        foreach (var toast in Toasts.Where(t => t.TabId == tabId).ToArray())
        {
            Remove(toast);
        }
    }

    private void Remove(Toast toast)
    {
        toast.Timer?.Stop();
        Toasts.Remove(toast);
        if (Toasts.Count == 0 && IsVisible)
        {
            Hide();
        }
    }

    private void Reposition()
    {
        if (Toasts.Count == 0)
        {
            return;
        }

        if (_owner.WindowState == WindowState.Minimized || !_owner.IsVisible || !_anchor.IsLoaded)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        // The anchor's top-right in screen pixels, back to DIPs for Left/Top.
        var source = PresentationSource.FromVisual(_anchor);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var topRight = _anchor.PointToScreen(new Point(_anchor.ActualWidth, 0));
        var dip = source.CompositionTarget.TransformFromDevice.Transform(topRight);
        Left = dip.X - Width;
        Top = dip.Y;

        if (!IsVisible)
        {
            base.Show();
        }
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Toast toast })
        {
            Remove(toast);
            _focusTab(toast.TabId);
            e.Handled = true;
        }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Toast toast })
        {
            Remove(toast);
            e.Handled = true;
        }
    }
}
