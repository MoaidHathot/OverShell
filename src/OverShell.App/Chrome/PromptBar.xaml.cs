using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.IO;
using OverShell.App.Input;
using OverShell.Core.Input;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>Who a prompt goes to.</summary>
public enum PromptTarget
{
    Active,
    Agents,
    Blocked,
    Group,
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
    private readonly PromptHistory _history = new();
    private readonly ObservableCollection<PromptAttachment> _attachments = [];
    private readonly ClipboardWorkQueue _clipboard = new();
    private readonly TransferFileStore _files = new(Core.AppPaths.ClipboardDir);
    private bool _sending;
    private int _clipboardFailures;
    private IReadOnlyList<Snippet> _snippets = [];

    public PromptBar()
    {
        InitializeComponent();
        AttachmentItems.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) => AttachmentItems.Visibility = _attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AttachmentItems.Visibility = Visibility.Collapsed;
        Input.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,
            (_, e) => { e.Handled = true; PasteFromClipboard(); },
            (_, e) => { e.CanExecute = true; e.Handled = true; }));
        ClipboardReader = asFiles => ClipboardAccess.RetryAsync(() => Clipboard.GetDataObject() is { } data
            ? TerminalClipboard.Read(data, _files, asFiles, materializeImage: true)
            : TransferContent.Empty);
    }

    /// <summary>The window resolves targets and acknowledges delivery. A rejected draft stays editable.</summary>
    public Func<PromptDraft, PromptTarget, bool>? SendRequested { get; set; }

    internal Func<bool, Task<TransferContent>> ClipboardReader { get; set; }
    internal Task ClipboardIdle => _clipboard.Completion;
    public IReadOnlyList<PromptAttachment> Attachments => _attachments;
    public IReadOnlyList<PromptDraft> HistoryEntries => _history.Entries;
    public string TransferErrorText => TransferError.Text;

    public event Action? HideRequested;

    /// <summary>Asked while typing: where would this text go? Returns the echo line (\u2192 api, web), or empty for no address.</summary>
    public Func<string, string>? RouteDescriber { get; set; }

    /// <summary>The echo line shown under the box, for the self-test.</summary>
    public string RouteText => Route.Text;

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        var echo = RouteDescriber?.Invoke(Input.Text) ?? string.Empty;
        Route.Text = echo;
        Route.Visibility = echo.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public PromptTarget SelectedTarget
    {
        get => (Target.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "agents" => PromptTarget.Agents,
            "blocked" => PromptTarget.Blocked,
            "group" => PromptTarget.Group,
            "all" => PromptTarget.All,
            _ => PromptTarget.Active,
        };
        set
        {
            var tag = value switch
            {
                PromptTarget.Agents => "agents",
                PromptTarget.Blocked => "blocked",
                PromptTarget.Group => "group",
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
    public IReadOnlyList<string> History => _history.Entries.Select(d => d.Text).ToArray();

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

    public PromptDraft CaptureDraft() => new(Input.Text, _attachments.Select(a => a.Path));

    public void AttachFiles(IEnumerable<string> paths)
    {
        // Validate the entire incoming list before changing the draft.
        var files = paths.Select(path => Path.GetFullPath(path)).ToArray();
        foreach (var path in files)
        {
            if (!_attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                _attachments.Add(new PromptAttachment(path));
        }
    }

    internal void ApplyTransfer(TransferContent content)
    {
        if (content.ApplicationPaste) throw new InvalidOperationException("Prompt attachments must be materialized before delivery.");
        AttachFiles(content.Files);
        if (content.Text.Length > 0)
        {
            var start = Input.SelectionStart;
            Input.SelectedText = content.Text;
            Input.Select(start + content.Text.Length, 0);
        }

        TransferError.Text = string.Empty;
        TransferError.Visibility = Visibility.Collapsed;
    }

    public void PasteFromClipboard(bool asFiles = false) =>
        _clipboard.Enqueue(async () => ApplyTransfer(await ClipboardReader(asFiles)), error =>
        {
            _clipboardFailures++;
            ShowTransferError(error);
        });

    internal bool TryClipboardCommand(string command)
    {
        if (!Input.IsKeyboardFocusWithin) return false;
        switch (command.ToLowerInvariant())
        {
            case "clipboard.paste": PasteFromClipboard(); return true;
            case "clipboard.pastefiles": PasteFromClipboard(asFiles: true); return true;
            case "clipboard.copy":
            case "clipboard.copyifselection": ApplicationCommands.Copy.Execute(null, Input); return true;
            default: return false;
        }
    }

    /// <summary>Waits for an in-flight paste, sends one snapshot, and clears only an accepted draft.</summary>
    public void Send() => _ = SendAsync();

    public async Task SendAsync()
    {
        if (_sending) return;
        _sending = true;
        BtnSend.IsEnabled = false;
        var failuresBefore = _clipboardFailures;
        try
        {
            Task pending;
            do { pending = _clipboard.Completion; await pending; } while (pending != _clipboard.Completion);
            if (_clipboardFailures != failuresBefore) return;
            var draft = CaptureDraft();
            if (draft.IsEmpty) return;
            if (SendRequested?.Invoke(draft, SelectedTarget) != true) return;
            _history.Record(draft);
            Input.Clear();
            _attachments.Clear();
            TransferError.Text = string.Empty;
            TransferError.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) { ShowTransferError(e); }
        finally { _sending = false; BtnSend.IsEnabled = true; }
    }

    private void ShowTransferError(Exception error)
    {
        TransferError.Text = error.Message;
        TransferError.Visibility = Visibility.Visible;
    }

    internal void RecallHistory(bool previous)
    {
        var draft = previous ? _history.Previous(CaptureDraft()) : _history.Next();
        if (draft is null) return;
        Input.Text = draft.Text;
        _attachments.Clear();
        AttachFiles(draft.Files);
        Input.CaretIndex = Input.Text.Length;
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files", Filter = "All files (*.*)|*.*", Multiselect = true, CheckFileExists = true, DereferenceLinks = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AttachFiles(dialog.FileNames);
        Input.Focus();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PromptAttachment attachment }) RemoveAttachment(attachment);
    }

    public bool RemoveAttachment(PromptAttachment attachment) => _attachments.Remove(attachment);

    private void Transfer_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try { e.Effects = TerminalClipboard.CanRead(e.Data) ? CopyEffect(e.AllowedEffects) : DragDropEffects.None; }
        catch { e.Effects = DragDropEffects.None; }
    }

    private void Transfer_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = CopyEffect(e.AllowedEffects);
        if (e.Effects == DragDropEffects.None) return;
        try
        {
            var content = TerminalClipboard.Read(e.Data, _files, materializeImage: true);
            if (content.IsEmpty) { e.Effects = DragDropEffects.None; return; }
            var index = Input.GetCharacterIndexFromPoint(e.GetPosition(Input), snapToText: true);
            if (index >= 0) Input.Select(index, 0);
            ApplyTransfer(content);
            Input.Focus();
        }
        catch (Exception error) { e.Effects = DragDropEffects.None; ShowTransferError(error); }
    }

    private static DragDropEffects CopyEffect(DragDropEffects allowed) => allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.None;

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
            case Key.Up when _history.Entries.Count > 0 && Input.GetLineIndexFromCharacterIndex(Input.CaretIndex) == 0:
                e.Handled = true;
                RecallHistory(previous: true);
                break;

            case Key.Down when _history.IsBrowsing && Input.GetLineIndexFromCharacterIndex(Input.CaretIndex) == Input.LineCount - 1:
                e.Handled = true;
                RecallHistory(previous: false);
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

public sealed record PromptAttachment(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;
}
