using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OverShell.Core.Layout;

namespace OverShell.App.Chrome;

/// <summary>
/// The tab list in any of its three shapes (DESIGN.md §12.5): a horizontal strip for the
/// caption bar or the bottom edge, a vertical list for a side panel, a narrow rail of
/// state dots. One instance; the window moves it between hosts when the layout changes
/// and sets <see cref="Mode"/>. Events rather than direct calls so the strip knows
/// nothing about how tabs are opened or closed.
/// <para>
/// Tabs are shown through a live-grouping view over the window's collection: a tab's
/// <see cref="TerminalTab.Group"/> puts it under a header; the window keeps the source
/// order equal to the grouped order so <c>Alt+N</c> means what the eye sees. Dragging a
/// tab past its neighbours reorders live — no ghost, no drop target; the item simply
/// moves when the pointer crosses the next one.
/// </para>
/// </summary>
public partial class TabStrip : UserControl
{
    private const double DragThreshold = 6;

    private TabsStyle _mode = TabsStyle.Strip;
    private ListCollectionView? _view;
    private TerminalTab? _pressed;
    private Point _pressedAt;
    private bool _dragging;

    public TabStrip()
    {
        InitializeComponent();
        ApplyMode();
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        LostMouseCapture += (_, _) => EndDrag();
    }

    public event Action<TerminalTab>? TabSelected;

    public event Action<TerminalTab>? TabCloseRequested;

    /// <summary>Right-click on a tab; the window shows its menu anchored to the element.</summary>
    public event Action<TerminalTab, FrameworkElement>? TabMenuRequested;

    /// <summary>The user dragged a tab onto another position: move it there in the collection.</summary>
    public event Action<TerminalTab, int>? TabMoveRequested;

    public event Action? NewTabRequested;

    /// <summary>The profiles button was clicked; the window shows its menu anchored to the button.</summary>
    public event Action<Button>? ProfilesRequested;

    public IEnumerable? Tabs
    {
        get => _view?.SourceCollection;
        set
        {
            if (value is null)
            {
                _view = null;
                Items.ItemsSource = null;
                return;
            }

            // Live grouping: a change to a tab's Group property regroups without a Refresh().
            _view = new ListCollectionView((IList)value)
            {
                IsLiveGrouping = true,
            };
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TerminalTab.Group)));
            _view.LiveGroupingProperties.Add(nameof(TerminalTab.Group));
            Items.ItemsSource = _view;
        }
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

    /// <summary>True while a tab is being dragged — diagnostics.</summary>
    public bool IsDragging => _dragging;

    private void ApplyMode()
    {
        var horizontal = _mode == TabsStyle.Strip;

        Items.ItemTemplate = (DataTemplate)Resources[_mode switch
        {
            TabsStyle.List => "ListTemplate",
            TabsStyle.Rail => "RailTemplate",
            _ => "StripTemplate",
        }];

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, horizontal ? Orientation.Horizontal : Orientation.Vertical);
        Items.ItemsPanel = new ItemsPanelTemplate(panel);

        Items.GroupStyle.Clear();
        Items.GroupStyle.Add((GroupStyle)Resources[horizontal ? "StripGroupStyle" : "ListGroupStyle"]);

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

    // ------------------------------------------------------------- mouse

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            TabSelected?.Invoke(tab);

            // A press may become a drag; remember where it started.
            _pressed = tab;
            _pressedAt = e.GetPosition(this);
            _dragging = false;

            // Stop this bubbling to the title bar, which would start a window drag.
            e.Handled = true;
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_dragging)
        {
            var delta = position - _pressedAt;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            CaptureMouse();
        }

        var target = IndexAt(position);
        if (target is { } index && _view is not null)
        {
            var current = _view.IndexOf(_pressed);
            if (current >= 0 && index != current)
            {
                TabMoveRequested?.Invoke(_pressed, index);
            }
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _pressed = null;
        if (_dragging)
        {
            _dragging = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
    }

    /// <summary>The view index of the item whose bounds contain the pointer along the strip's axis, or null.</summary>
    private int? IndexAt(Point position)
    {
        if (_view is null)
        {
            return null;
        }

        var horizontal = _mode == TabsStyle.Strip;
        for (var i = 0; i < _view.Count; i++)
        {
            if (Items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container || !container.IsVisible)
            {
                continue;
            }

            var origin = container.TransformToAncestor(this).Transform(new Point(0, 0));
            var start = horizontal ? origin.X : origin.Y;
            var end = start + (horizontal ? container.ActualWidth : container.ActualHeight);
            var p = horizontal ? position.X : position.Y;
            if (p >= start && p < end)
            {
                return i;
            }
        }

        return null;
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
