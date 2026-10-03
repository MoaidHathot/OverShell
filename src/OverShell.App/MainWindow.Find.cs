using OverShell.App.Chrome;
using OverShell.App.Terminal.Search;

namespace OverShell.App;

/// <summary>
/// Find in the buffer (DESIGN.md §12.14): <c>terminal.find</c> opens the find bar of the
/// window the chord came from - the main window's, or a tear-off's - over the tab in front
/// of the user. Searching runs through the terminal's UI Automation text provider
/// (<see cref="TerminalSearch"/>), the one channel that reaches scrollback.
/// </summary>
public partial class MainWindow
{
    private readonly TerminalSearch _terminalSearch = new();
    private FindBarController _findBar = null!;

    /// <summary>The main window's find bar, for the self-test.</summary>
    internal FindBar FindBarView => Find;

    /// <summary>The main window's find controller, for diagnostics and the self-test.</summary>
    internal FindBarController FindController => _findBar;

    /// <summary>Shared by every window's bar so a terminal's automation element is resolved once.</summary>
    internal TerminalSearch Search => _terminalSearch;

    private void InitializeFind()
    {
        _findBar = new FindBarController(Find, this, _terminalSearch);

        _commands.Register(
            "terminal.find",
            "Find in this tab",
            "Terminal",
            OpenFind,
            () => TargetTab is not null,
            "Search the buffer, scrollback included - Enter walks up through older text, Shift+Enter down");
        _commands.Register(
            "terminal.findUp",
            "Find: older match (up)",
            "Terminal",
            () => StepFind(-1),
            () => FindControllerFor(TargetTab)?.Session?.Count > 0);
        _commands.Register(
            "terminal.findDown",
            "Find: newer match (down)",
            "Terminal",
            () => StepFind(1),
            () => FindControllerFor(TargetTab)?.Session?.Count > 0);
    }

    private void OpenFind()
    {
        if (TargetTab is not { } tab)
        {
            return;
        }

        if (tab.Detached)
        {
            _tearOffs.FirstOrDefault(w => ReferenceEquals(w.Tab, tab))?.Find.Open(tab);
            return;
        }

        _findBar.Open(tab);
    }

    private void StepFind(int direction) => FindControllerFor(TargetTab)?.Session?.Step(direction);

    /// <summary>The controller whose bar can show <paramref name="tab"/>: its tear-off's, or the main window's.</summary>
    private FindBarController? FindControllerFor(TerminalTab? tab)
    {
        if (tab is null)
        {
            return null;
        }

        return tab.Detached
            ? _tearOffs.FirstOrDefault(w => ReferenceEquals(w.Tab, tab))?.Find
            : _findBar;
    }
}
