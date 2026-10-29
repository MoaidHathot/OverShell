using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OverShell.App.Chrome;
using OverShell.Core.Commands;
using OverShell.Core.Tabs;

namespace OverShell.App;

public partial class MainWindow
{
    private readonly NavigationHistory _navigationHistory = new();
    private bool _attentionNavigation;
    internal INavigationService Navigation { get; private set; } = null!;

    private void InitializeNavigation()
    {
        Navigation = new WindowNavigation(this);
        _commands.CaptureContext = () => new CommandContext(TargetTab?.Id, CommandOwner() is TearOffWindow tear ? tear.Tab.Id : null);
        _commands.Register("tab.last", "Switch to the last used tab", "Tabs", () => Navigation.Last(),
            description: "Bounces between the last visited tabs, including detached terminals");
        _commands.Register("navigation.return", "Return to interrupted work", "Tabs", () => Navigation.Return(),
            description: "Return to the original tab and view before the attention chain");
    }

    private void VisitTab(TerminalTab? tab)
    {
        if (_movingPresentation) return;
        if (tab is not null) _navigationHistory.Visit(tab.Id, _attentionNavigation);
    }

    private sealed class WindowNavigation(MainWindow window) : INavigationService
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<NavigationPoint, WeakReference<System.Windows.Controls.Primitives.TextBoxBase>> _textFocus = new();
        public bool CanReturn => window._navigationHistory.Origin is not null || window.ActiveTerminalWindow() is TearOffWindow { IsFloating: true };

        public NavigationPoint Capture()
        {
            var owner = window.ActiveTerminalWindow() ?? window;
            if (window._floats.FirstOrDefault(e => ReferenceEquals(e.Window, owner)) is { } floating) return floating.Origin;
            var tab = owner is TearOffWindow secondary ? secondary.Tab : window.ActiveTab;
            var focus = (owner is TearOffWindow t ? t.PromptBar.IsKeyboardFocusWithin : window.Prompt.IsKeyboardFocusWithin) ? "prompt"
                : (owner is TearOffWindow w ? w.Find.InputHasFocus : window.Find.IsKeyboardFocusWithin) ? "find" : null;
            var point = new NavigationPoint(tab?.Id, window.ViewId, owner is TearOffWindow tear ? tear.Tab.Id : null,
                focus, window._shell?.Cursor?.Id);
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase text)
                _textFocus.Add(point, new(text));
            return point;
        }

        public void BeginAttention() => window._navigationHistory.Begin(Capture());

        public bool Return()
        {
            if (window.ActiveTerminalWindow() is TearOffWindow { IsFloating: true } floating)
            {
                window.ReleaseFloat(floating.Tab, returnFocus: true);
                return true;
            }
            var point = window._navigationHistory.TakeReturn(window.TargetTab?.Id, id => window._tabsById.ContainsKey(id));
            if (point is not null) return Restore(point);
            window.ShowStatusMessage("No interrupted work to return to");
            return true;
        }

        public bool Last()
        {
            var id = window._navigationHistory.Last(window.TargetTab?.Id, id => window._tabsById.ContainsKey(id));
            if (id is not null) window.ActiveTab = window._tabsById[id];
            return true;
        }

        public bool Restore(NavigationPoint point)
        {
            var id = point.TabId;
            if (id is null || !window._tabsById.TryGetValue(id, out var tab))
            {
                id = window._navigationHistory.Last(null, key => window._tabsById.ContainsKey(key));
                if (id is null) return false;
                tab = window._tabsById[id];
                point = point with { Focus = null, CursorId = null, HostId = null };
            }
            window._attentionNavigation = true;
            try
            {
                window.ActiveTab = tab;
                if (!tab.Detached) window.ShowViewById(point.ViewId);
            }
            finally { window._attentionNavigation = false; }
            var owner = tab.Detached ? (Window?)window._tearOffs.FirstOrDefault(w => w.Tab == tab) : window;
            owner?.Activate();
            window._shell?.SetCursor(point.CursorId is { } cursor ? window._shell.Find(cursor) : null);
            window.Dispatcher.BeginInvoke(() =>
            {
                if (owner?.IsActive != true || !window.Tabs.Contains(tab)) return;
                if (_textFocus.TryGetValue(point, out var saved) && saved.TryGetTarget(out var text) && text.IsVisible && Window.GetWindow(text) == owner) text.Focus();
                else if (point.Focus == "prompt") (owner is TearOffWindow promptOwner ? promptOwner.PromptBar : window.Prompt).FocusInput();
                else if (point.Focus == "find") { if (owner is TearOffWindow findOwner) findOwner.Find.FocusInput(); else window.Find.FocusInput(); }
                else if (window.DashboardVisible && !tab.Detached) window.Focus();
                else tab.Surface.Focus();
            }, DispatcherPriority.ApplicationIdle);
            return true;
        }
    }
}
