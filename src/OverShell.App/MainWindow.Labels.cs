using OverShell.Core.Tabs;

namespace OverShell.App;

public partial class MainWindow
{
    private bool _refreshingLabels;

    private void RefreshTabLabels()
    {
        if (_refreshingLabels) return;
        _refreshingLabels = true;
        try
        {
            var prefixes = TabLabels.Prefixes(Tabs.Select(t =>
                new TabLabelEntry(t.Id, t.NamingId, t.BaseLabel, t.Project, t.UserLabel is not null)).ToArray());
            foreach (var tab in Tabs) tab.SetLabelPrefix(prefixes[tab.Id]);
        }
        finally { _refreshingLabels = false; }
    }

    internal void RenameTab(TerminalTab tab, string? label)
    {
        tab.UserLabel = label;
        // The session already stores labels per tab. A folder-wide cache made new tabs
        // inherit an unrelated session's name; the session is now the sole authority.
        SaveSession(force: true);
    }
}
