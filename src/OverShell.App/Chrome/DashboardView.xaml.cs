using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OverShell.App.Chrome;

/// <summary>
/// The Dashboard view: one card per tab, bodies from screen text (DESIGN.md §12.5). A
/// click selects the tab (its card is outlined); the open button, or a double click,
/// goes to that tab in the terminal view.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    public IEnumerable? Tabs
    {
        get => Cards.ItemsSource;
        set => Cards.ItemsSource = value;
    }

    /// <summary>A card was clicked: make the tab active, stay on the dashboard.</summary>
    public event Action<TerminalTab>? TabSelected;

    /// <summary>The user wants to work in this tab: switch to the terminal view on it.</summary>
    public event Action<TerminalTab>? OpenRequested;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TerminalTab tab })
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            OpenRequested?.Invoke(tab);
        }
        else
        {
            TabSelected?.Invoke(tab);
        }

        e.Handled = true;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            OpenRequested?.Invoke(tab);
            e.Handled = true;
        }
    }
}
