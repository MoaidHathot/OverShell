using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OverShell.App.Chrome;

/// <summary>
/// The find bar (DESIGN.md §12.14): a search box over the active tab's buffer, scrollback
/// included. Like the prompt bar it lives inside the window so an ordinary TextBox can take
/// the keyboard; the terminal gets it back when the bar closes. Enter and F3 walk upwards -
/// older text, the way Windows Terminal's own search does, because what you are looking for
/// usually just scrolled past - Shift+Enter and Shift+F3 downwards.
/// </summary>
public partial class FindBar : UserControl
{
    public FindBar()
    {
        InitializeComponent();
        Placeholder.Visibility = Visibility.Visible;
    }

    /// <summary>The needle or the case rule changed.</summary>
    public event Action<string, bool>? QueryChanged;

    /// <summary>-1 for the older match (up), +1 for the newer (down).</summary>
    public event Action<int>? StepRequested;

    public event Action? CloseRequested;

    public string Text
    {
        get => Input.Text;
        set => Input.Text = value;
    }

    public bool IsMatchCase => MatchCase.IsChecked == true;

    /// <summary>Text beside the box: the position among the matches, or that there are none.</summary>
    public string StatusText
    {
        get => Status.Text;
        set => Status.Text = value;
    }

    public void FocusInput()
    {
        Input.Focus();
        Input.SelectAll();
    }

    /// <summary>Whether stepping makes sense right now; the arrows grey out otherwise.</summary>
    public void SetCanStep(bool canStep)
    {
        BtnUp.IsEnabled = canStep;
        BtnDown.IsEnabled = canStep;
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueryChanged?.Invoke(Input.Text, IsMatchCase);
    }

    private void MatchCase_Changed(object sender, RoutedEventArgs e) => QueryChanged?.Invoke(Input.Text, IsMatchCase);

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Return:
            case Key.F3:
                e.Handled = true;
                StepRequested?.Invoke(shift ? 1 : -1);
                break;
            case Key.Escape:
                e.Handled = true;
                CloseRequested?.Invoke();
                break;
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e) => StepRequested?.Invoke(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => StepRequested?.Invoke(1);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
