using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>Who a prompt goes to.</summary>
public enum PromptTarget
{
    Active,
    Agents,
    Blocked,
    All,
}

/// <summary>
/// The prompt bar (DESIGN.md §12.5, P2): type once, send to the active tab or to every
/// agent — we own <c>WriteInput</c>, so the text lands as if typed, bracketed when the
/// application asked for it. Lives inside the main window, so an ordinary WPF TextBox can
/// take the keyboard; the terminal gets it back when the bar hides.
/// </summary>
public partial class PromptBar : UserControl
{
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private IReadOnlyList<Snippet> _snippets = [];

    public PromptBar()
    {
        InitializeComponent();
    }

    /// <summary>Text to send and where. The window resolves the target to tabs.</summary>
    public event Action<string, PromptTarget>? SendRequested;

    public event Action? HideRequested;

    public PromptTarget SelectedTarget
    {
        get => (Target.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "agents" => PromptTarget.Agents,
            "blocked" => PromptTarget.Blocked,
            "all" => PromptTarget.All,
            _ => PromptTarget.Active,
        };
        set
        {
            var tag = value switch
            {
                PromptTarget.Agents => "agents",
                PromptTarget.Blocked => "blocked",
                PromptTarget.All => "all",
                _ => "active",
            };
            Target.SelectedItem = Target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        }
    }

    public string Text
    {
        get => Input.Text;
        set => Input.Text = value;
    }

    /// <summary>Prompts sent this session, oldest first — for Up/Down in the box and for diagnostics.</summary>
    public IReadOnlyList<string> History => _history;

    public void SetSnippets(IReadOnlyList<Snippet> snippets)
    {
        _snippets = snippets;
        BtnSnippets.IsEnabled = snippets.Count > 0;
        BtnSnippets.ToolTip = snippets.Count > 0 ? $"{snippets.Count} snippet(s) from snippets.jsonc" : "No snippets — add some to snippets.jsonc";
    }

    public void FocusInput()
    {
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
    }

    /// <summary>Sends what is in the box (if anything) and remembers it.</summary>
    public void Send()
    {
        var text = Input.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_history.Count == 0 || _history[^1] != text)
        {
            _history.Add(text);
            if (_history.Count > 100)
            {
                _history.RemoveAt(0);
            }
        }

        _historyIndex = -1;
        Input.Clear();
        SendRequested?.Invoke(text, SelectedTarget);
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Return when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                e.Handled = true;
                Send();
                break;

            case Key.Escape:
                e.Handled = true;
                HideRequested?.Invoke();
                break;

            // History only when the caret cannot move within the text, so multi-line editing keeps its arrows.
            case Key.Up when _history.Count > 0 && Input.GetLineIndexFromCharacterIndex(Input.CaretIndex) == 0:
                e.Handled = true;
                _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
                Input.Text = _history[_historyIndex];
                Input.CaretIndex = Input.Text.Length;
                break;

            case Key.Down when _historyIndex >= 0 && Input.GetLineIndexFromCharacterIndex(Input.CaretIndex) == Input.LineCount - 1:
                e.Handled = true;
                _historyIndex++;
                if (_historyIndex >= _history.Count)
                {
                    _historyIndex = -1;
                    Input.Clear();
                }
                else
                {
                    Input.Text = _history[_historyIndex];
                    Input.CaretIndex = Input.Text.Length;
                }

                break;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void Snippets_Click(object sender, RoutedEventArgs e)
    {
        if (_snippets.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("ShellContextMenu"),
            PlacementTarget = BtnSnippets,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        var itemStyle = (Style)FindResource("ShellMenuItem");

        foreach (var snippet in _snippets)
        {
            var item = new MenuItem
            {
                Header = snippet.Name,
                Style = itemStyle,
                ToolTip = snippet.Description ?? snippet.Text,
                DataContext = new MenuAccent((System.Windows.Media.Brush)FindResource("Accent.Base")),
            };
            var captured = snippet;
            item.Click += (_, _) =>
            {
                Input.Text = captured.Text;
                FocusInput();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Binding shim so the shared menu item template can show a dot.</summary>
    private sealed record MenuAccent(System.Windows.Media.Brush Accent);
}
