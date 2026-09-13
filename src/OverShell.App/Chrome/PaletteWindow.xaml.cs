using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OverShell.Core.Search;

namespace OverShell.App.Chrome;

/// <summary>One row in the palette. <see cref="Invoke"/> runs after the window has closed.</summary>
public sealed class PaletteItem
{
    public required string Title { get; init; }

    public string? Detail { get; init; }

    /// <summary>Right-aligned hint: a key chord, or a tab's Alt+N.</summary>
    public string? Hint { get; init; }

    public string? Glyph { get; init; }

    public Brush? Dot { get; init; }

    /// <summary>Extra text the fuzzy search also matches on (working directory, harness, state).</summary>
    public string? Keywords { get; init; }

    /// <summary>Lower-case state name for the <c>@state</c> filter; null for commands.</summary>
    public string? State { get; init; }

    /// <summary>Project name for the <c>#project</c> filter; null for commands.</summary>
    public string? Project { get; init; }

    public required Action Invoke { get; init; }

    public Visibility DotVisibility => Dot is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVisibility => Dot is null && !string.IsNullOrEmpty(Glyph) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The command palette and tab switcher, and the one-line input box (rename): an owned,
/// activated window rather than a <c>Popup</c>, because the terminal's native child HWND
/// holds Win32 focus and a popup never takes it (DESIGN.md §12.7, spike 3). Closing hands
/// focus back through <see cref="Closed"/>, where the owner refocuses its terminal.
/// </summary>
public partial class PaletteWindow : Window
{
    private readonly Func<PaletteQuery, IReadOnlyList<PaletteItem>> _source;
    private readonly Action<string>? _accept;
    private bool _closing;

    private PaletteWindow(Window owner, Func<PaletteQuery, IReadOnlyList<PaletteItem>> source, Action<string>? accept)
    {
        InitializeComponent();
        Owner = owner;
        _source = source;
        _accept = accept;

        // Just below the tab strip, centred — where the eye already is.
        Left = owner.Left + ((owner.ActualWidth - Width) / 2);
        Top = owner.Top + 72;

        Deactivated += (_, _) => CloseOnce();
        Loaded += (_, _) => { Input.Focus(); Input.CaretIndex = Input.Text.Length; };
    }

    /// <summary>Opens the search palette. <paramref name="initial"/> seeds the box (<c>&gt;</c> for commands).</summary>
    public static PaletteWindow Show(Window owner, string initial, Func<PaletteQuery, IReadOnlyList<PaletteItem>> source)
    {
        var window = new PaletteWindow(owner, source, accept: null);
        window.Input.Text = initial;
        window.Refresh();
        window.Show();
        return window;
    }

    /// <summary>Opens a single-line prompt; Enter calls <paramref name="accept"/> with the text.</summary>
    public static PaletteWindow Prompt(Window owner, string prompt, string initial, Action<string> accept)
    {
        var window = new PaletteWindow(owner, _ => [], accept);
        window.TxtPrompt.Text = "\uE70F"; // pencil
        window.List.Visibility = Visibility.Collapsed;
        window.TxtEmpty.Text = prompt;
        window.TxtEmpty.Visibility = Visibility.Visible;
        window.Input.Text = initial;

        // SizeToContent measures once at Show; content changed after InitializeComponent
        // must be laid out first or the last line is clipped by its own margin.
        window.UpdateLayout();
        window.Show();
        window.Input.SelectAll();
        return window;
    }

    private void Refresh()
    {
        if (_accept is not null)
        {
            return;
        }

        var query = PaletteQuery.Parse(Input.Text);
        var items = _source(query);
        List.ItemsSource = items;
        List.SelectedIndex = items.Count > 0 ? 0 : -1;
        TxtEmpty.Text = query.CommandsMode ? "No matching command" : "No matching tab";
        TxtEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtPrompt.Text = query.CommandsMode ? "\uE756" : "\uE721"; // command prompt / search
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseOnce();
                break;

            case Key.Return:
                e.Handled = true;
                if (_accept is { } accept)
                {
                    var text = Input.Text;
                    CloseOnce(() => accept(text));
                }
                else if (List.SelectedItem is PaletteItem item)
                {
                    CloseOnce(item.Invoke);
                }

                break;

            case Key.Down:
                e.Handled = true;
                Move(1);
                break;

            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;

            case Key.PageDown:
                e.Handled = true;
                Move(8);
                break;

            case Key.PageUp:
                e.Handled = true;
                Move(-8);
                break;
        }
    }

    private void List_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is PaletteItem item)
        {
            CloseOnce(item.Invoke);
        }
    }

    private void Move(int delta)
    {
        if (List.Items.Count == 0)
        {
            return;
        }

        List.SelectedIndex = Math.Clamp(List.SelectedIndex + delta, 0, List.Items.Count - 1);
        List.ScrollIntoView(List.SelectedItem);
    }

    /// <summary>
    /// Close exactly once, then run the chosen action after the window is gone — so the
    /// action's own focus changes (a new tab, a rename) are not undone by the palette's
    /// closing focus traffic.
    /// </summary>
    private void CloseOnce(Action? then = null)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();

        if (then is not null)
        {
            Dispatcher.BeginInvoke(then, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// Closing deactivates the window, and Deactivated would close it again: a direct
    /// <see cref="Window.Close"/> from the owner must set the flag too.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }
}
