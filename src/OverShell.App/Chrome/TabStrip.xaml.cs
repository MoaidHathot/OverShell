using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverShell.Core.Layout;

namespace OverShell.App.Chrome;

/// <summary>
/// The tab list in any of its three shapes (DESIGN.md §12.5): a horizontal strip for the
/// caption bar or the bottom edge, a vertical list for a side panel, a narrow rail of
/// state dots. One instance; the window moves it between hosts when the layout changes
/// and sets <see cref="Mode"/>. Events rather than direct calls so the strip knows
/// nothing about how tabs are opened or closed.
/// </summary>
public partial class TabStrip : UserControl
{
    private TabsStyle _mode = TabsStyle.Strip;

    public TabStrip()
    {
        InitializeComponent();
        ApplyMode();
    }

    public event Action<TerminalTab>? TabSelected;

    public event Action<TerminalTab>? TabCloseRequested;

    /// <summary>Right-click on a tab; the window shows its menu anchored to the element.</summary>
    public event Action<TerminalTab, FrameworkElement>? TabMenuRequested;

    public event Action? NewTabRequested;

    /// <summary>The profiles button was clicked; the window shows its menu anchored to the button.</summary>
    public event Action<Button>? ProfilesRequested;

    public IEnumerable? Tabs
    {
        get => Items.ItemsSource;
        set => Items.ItemsSource = value;
    }

    public TabsStyle Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            ApplyMode();
        }
    }

    private void ApplyMode()
    {
        var horizontal = _mode == TabsStyle.Strip;

        Items.ItemTemplate = (DataTemplate)Resources[_mode switch
        {
            TabsStyle.List => "ListTemplate",
            TabsStyle.Rail => "RailTemplate",
            _ => "StripTemplate",
        }];

        Items.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel))
        {
            // A rail is a vertical stack too; only the strip lays out sideways.
        });
        ((FrameworkElementFactory)Items.ItemsPanel.VisualTree).SetValue(StackPanel.OrientationProperty, horizontal ? Orientation.Horizontal : Orientation.Vertical);

        Row.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        Actions.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        Actions.HorizontalAlignment = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        Actions.Margin = horizontal ? new Thickness(0) : new Thickness(0, 4, 0, 6);

        Scroller.HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        Scroller.VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Scroller.VerticalAlignment = horizontal ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Scroller.HorizontalAlignment = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Scroller.Padding = horizontal ? new Thickness(0) : new Thickness(0, 6, 0, 0);
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            TabSelected?.Invoke(tab);

            // Stop this bubbling to the title bar, which would start a window drag.
            e.Handled = true;
        }
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TerminalTab tab } element)
        {
            return;
        }

        switch (e.ChangedButton)
        {
            // Middle-click closes, matching every other tabbed app.
            case MouseButton.Middle:
                TabCloseRequested?.Invoke(tab);
                e.Handled = true;
                break;

            case MouseButton.Right:
                TabMenuRequested?.Invoke(tab, element);
                e.Handled = true;
                break;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            TabCloseRequested?.Invoke(tab);
            e.Handled = true;
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTabRequested?.Invoke();

    private void Profiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ProfilesRequested?.Invoke(button);
        }
    }
}
