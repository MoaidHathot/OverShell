using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OverShell.App.Chrome;

/// <summary>The four-way view switch in the caption bar, with the attention badge (DESIGN.md §12.5).</summary>
public partial class ViewSwitch : UserControl
{
    private string _current = "terminal";

    public ViewSwitch()
    {
        InitializeComponent();
        Highlight();
    }

    /// <summary>The user clicked a view; the window applies it.</summary>
    public event Action<string>? ViewRequested;

    public string Current
    {
        get => _current;
        set
        {
            if (_current == value)
            {
                return;
            }

            _current = value;
            Highlight();
        }
    }

    /// <summary>Tabs needing the user; 0 hides the badge. Amber when any is blocked or errored, green when only finished-unseen.</summary>
    public void SetAttention(int count, bool urgent)
    {
        Badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtBadge.Text = count > 9 ? "9+" : count.ToString();
        Badge.Background = (Brush)FindResource(urgent ? "State.Blocked" : "State.Done");
    }

    private void Highlight()
    {
        foreach (var button in new[] { BtnTerminal, BtnHerd, BtnDashboard, BtnZen })
        {
            var active = string.Equals((string)button.Tag, _current, StringComparison.OrdinalIgnoreCase);
            button.Background = active ? (Brush)FindResource("Surface.Raised") : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(active ? "Text.Primary" : "Text.Secondary");
        }
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewRequested?.Invoke(id);
        }
    }
}
