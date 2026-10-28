using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>
/// The Dashboard view: one card per tab, with faithful read-only terminal previews. A
/// click selects the tab (its card is outlined); the open button, or a double click,
/// goes to that tab in the terminal view.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        Cards.ItemContainerGenerator.ItemsChanged += (_, _) => InvalidatePreviews();
    }

    public static readonly DependencyProperty PreviewSettingsProperty = DependencyProperty.Register(nameof(PreviewSettings), typeof(DashboardSettings), typeof(DashboardView), new PropertyMetadata(new DashboardSettings()));
    public DashboardSettings PreviewSettings { get => (DashboardSettings)GetValue(PreviewSettingsProperty); set => SetValue(PreviewSettingsProperty, value); }

    internal void InvalidatePreviews() => InvalidatePreviews(Cards);
    private static void InvalidatePreviews(DependencyObject root)
    {
        if (root is TerminalPreviewControl preview) preview.InvalidatePlacement();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) InvalidatePreviews(VisualTreeHelper.GetChild(root, i));
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

    public event Action<TerminalTab, FrameworkElement>? HoverRequested;
    public event Action<TerminalTab>? HoverEnded;

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab } card) HoverRequested?.Invoke(tab, card);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab }) HoverEnded?.Invoke(tab);
    }

    internal void EnsureVisible(TerminalTab tab)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Cards.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement element) element.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

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
