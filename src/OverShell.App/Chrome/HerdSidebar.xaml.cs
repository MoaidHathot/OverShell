using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OverShell.Core.Agents;
using OverShell.Core.Herd;

namespace OverShell.App.Chrome;

/// <summary>One project's block in the sidebar. Rebuilt whenever the ordering changes; rows bind to the live tabs.</summary>
public sealed class HerdGroupView
{
    public required string Project { get; init; }

    public required Brush RollupBrush { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<TerminalTab> Tabs { get; init; }
}

/// <summary>
/// The Herd view's sidebar (DESIGN.md §12.5): every tab grouped by project, groups and
/// rows ordered by who needs you first, then who moved last. The window feeds it the
/// ordering from <see cref="HerdOrdering"/> on its heartbeat; rows update themselves
/// through the tabs' property changes.
/// </summary>
public partial class HerdSidebar : UserControl
{
    private string _signature = string.Empty;

    public HerdSidebar()
    {
        InitializeComponent();
        Groups.ItemsSource = Items;
    }

    public ObservableCollection<HerdGroupView> Items { get; } = [];

    public event Action<TerminalTab>? TabSelected;

    /// <summary>Right-click on a row; the window shows the tab menu anchored to it.</summary>
    public event Action<TerminalTab, FrameworkElement>? TabMenuRequested;

    /// <summary>
    /// Recomputes the grouping. Cheap when nothing moved: the ordering is compared as a
    /// string before any collection is touched, so the visual tree is left alone.
    /// </summary>
    public void Refresh(IReadOnlyList<TerminalTab> tabs)
    {
        var entries = tabs.Select((t, i) => new HerdEntry(t.Id, ProjectOf(t), t.State, t.Unread, t.IsAgent, t.Agent.LastActivity, i)).ToList();
        var groups = HerdOrdering.Group(entries);

        var signature = string.Join("|", groups.Select(g => $"{g.Project}:{g.Rollup}:{g.NeedingAttention}:{string.Join(",", g.Tabs.Select(t => t.TabId))}"));
        var agents = tabs.Count(t => t.IsAgent);
        var attention = groups.Sum(g => g.NeedingAttention);
        TxtSummary.Text = attention > 0 ? $"{agents} agent{(agents == 1 ? string.Empty : "s")} · {attention} need{(attention == 1 ? "s" : string.Empty)} you" : $"{agents} agent{(agents == 1 ? string.Empty : "s")}";

        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        var byId = tabs.ToDictionary(t => t.Id, StringComparer.Ordinal);

        Items.Clear();
        foreach (var group in groups)
        {
            Items.Add(new HerdGroupView
            {
                Project = group.Project,
                RollupBrush = BrushFor(group.Rollup, group.Tabs.Any(t => t.Unread && t.State == AgentState.Done)),
                Summary = group.NeedingAttention > 0 ? $"{group.NeedingAttention} need{(group.NeedingAttention == 1 ? "s" : string.Empty)} you" : $"{group.Tabs.Count}",
                Tabs = group.Tabs.Select(t => byId[t.TabId]).ToList(),
            });
        }
    }

    private static string ProjectOf(TerminalTab tab) => string.IsNullOrEmpty(tab.Project) ? "—" : tab.Project;

    private static Brush BrushFor(AgentState state, bool unreadDone) => (Brush)Application.Current.FindResource(state switch
    {
        AgentState.Blocked => "State.Blocked",
        AgentState.Error => "State.Error",
        AgentState.Done when unreadDone => "State.Done",
        AgentState.Working => "State.Working",
        AgentState.Exited => "State.Exited",
        _ => "State.Idle",
    });

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            TabSelected?.Invoke(tab);
            e.Handled = true;
        }
    }

    private void Row_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab } element)
        {
            TabMenuRequested?.Invoke(tab, element);
            e.Handled = true;
        }
    }
}
