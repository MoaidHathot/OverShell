using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.Core.Agents;
using OverShell.Core.Extensibility;
using OverShell.Core.Herd;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// The inbox (DESIGN.md §12.17): every tab waiting for you - blocked, errored, finished
/// unseen - oldest first, each with the line that asked, and a way to answer without
/// visiting the tab: <c>y</c> / <c>n</c> approve or deny (through the harness's integration
/// when it listens, else the rule file's keys), a reply box for free text, Enter to jump,
/// <c>A</c> to approve everything after a confirmation. An item leaves when its tab moves
/// on; the selection moves to the next. Built on <see cref="IShell"/> alone.
/// </summary>
public sealed class InboxExtension : IExtension
{
    private IShell _shell = null!;
    private InboxWindow? _window;

    public string Id => "inbox";

    public void Initialize(IShell shell)
    {
        _shell = shell;
        shell.Commands.Register("inbox.open", "Inbox: everything waiting for you", "Herd", Toggle,
            description: "Blocked and finished tabs oldest first, with the line that asked; y / n answer, type a reply, Enter jumps, A approves all");
        shell.Keys.AddDefaults(
        [
            new Keybinding("ctrl+shift+i", "inbox.open"),
            new Keybinding($"{HerdModeExtension.Leader} i", "inbox.open"),
        ]);

        shell.TabStateChanged += (_, _) => _window?.Refresh();
        shell.TabClosed += _ => _window?.Refresh();
        shell.Heartbeat += _ => _window?.Tick();
    }

    internal InboxWindow? Window => _window;

    internal void Toggle()
    {
        if (_window is { IsVisible: true })
        {
            _window.Close();
            return;
        }

        if (_shell.Ui.Host is not Window owner)
        {
            return;
        }

        _window = new InboxWindow(owner, _shell);
        _window.Closed += (_, _) =>
        {
            _window = null;
            if (_shell.ActiveTab is { } active)
            {
                _shell.Activate(active);
            }
        };
        _window.Show();
    }

    public void Dispose() => _window?.Close();

    /// <summary>What the inbox lists for one tab.</summary>
    public sealed record InboxItem(ITab Tab, string Kind, string Age, string Line, ReplyChannel Channel);

    /// <summary>The rows, oldest first, blocked before done - pure over the tabs, for the test and the window.</summary>
    public static IReadOnlyList<InboxItem> Build(IEnumerable<ITab> tabs, DateTimeOffset now)
    {
        var items = new List<InboxItem>();
        foreach (var tab in tabs.Where(t => t.NeedsAttention))
        {
            var line = tab.OpenRequest?.Title
                       ?? tab.Rules.Screen.MatchingLine(tab.ScreenRows, tab.State == AgentState.Done ? AgentState.Done : AgentState.Blocked)
                       ?? tab.Summary
                       ?? tab.Explain;
            var age = tab.AttentionSince is { } since ? Age(now - since) : string.Empty;
            var kind = tab.State switch
            {
                AgentState.Blocked => tab.OpenRequest?.Kind == "question" ? "question" : "needs you",
                AgentState.Error => "error",
                _ => "done",
            };
            items.Add(new InboxItem(tab, kind, age, line, tab.ReplyChannel));
        }

        return items
            .OrderBy(i => HerdOrdering.AttentionRank(i.Tab.State, i.Tab.Unread))
            .ThenBy(i => i.Tab.AttentionSince ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    private static string Age(TimeSpan span) => span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds} s" : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalHours} h";

    /// <summary>The inbox window: a list with the keyboard, a preview, a reply box.</summary>
    public sealed class InboxWindow : Window
    {
        private readonly IShell _shell;
        private readonly ListBox _list;
        private readonly TextBlock _preview;
        private readonly TextBox _reply;
        private readonly TextBlock _empty;
        private readonly TextBlock _hint;
        private IReadOnlyList<InboxItem> _items = [];
        private int _tick;
        private bool _asking;

        public InboxWindow(Window owner, IShell shell)
        {
            _shell = shell;
            Owner = owner;
            Title = "Inbox";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Width = 900;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Width = 440 };
            _list.SetResourceReference(ForegroundProperty, "Text.Primary");
            _list.SetResourceReference(FontFamilyProperty, "Font.Ui");
            _list.SelectionChanged += (_, _) => UpdatePreview();
            _list.PreviewKeyDown += List_PreviewKeyDown;
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

            _preview = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 0, 0, 0) };
            _preview.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            _preview.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            _preview.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");

            _empty = new TextBlock { Text = "Nothing is waiting for you", Margin = new Thickness(12), Visibility = Visibility.Collapsed };
            _empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");
            _empty.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");

            _reply = new TextBox { AcceptsReturn = false, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
            _reply.SetResourceReference(BackgroundProperty, "Surface.Base");
            _reply.SetResourceReference(ForegroundProperty, "Text.Primary");
            _reply.SetResourceReference(TextBox.CaretBrushProperty, "Text.Primary");
            _reply.SetResourceReference(BorderBrushProperty, "Surface.Border");
            _reply.SetResourceReference(FontFamilyProperty, "Font.Ui");
            _reply.PreviewKeyDown += Reply_PreviewKeyDown;

            _hint = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            _hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");
            _hint.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            _hint.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");

            var header = new TextBlock { Text = "Inbox", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            header.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Body");

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new Grid();
            left.Children.Add(_list);
            left.Children.Add(_empty);
            Grid.SetColumn(left, 0);
            Grid.SetColumn(_preview, 1);
            body.Children.Add(left);
            body.Children.Add(_preview);

            // The reply box with a placeholder that says what it is for.
            var placeholder = new TextBlock { Text = "Reply to the selected agent\u2026  (i or Tab to type here, Enter sends)", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");
            placeholder.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            placeholder.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Small");
            _reply.TextChanged += (_, _) => placeholder.Visibility = _reply.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var replyHost = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            _reply.Margin = default;
            replyHost.Children.Add(_reply);
            replyHost.Children.Add(placeholder);

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(_hint, Dock.Bottom);
            DockPanel.SetDock(replyHost, Dock.Bottom);
            layout.Children.Add(header);
            layout.Children.Add(_hint);
            layout.Children.Add(replyHost);
            layout.Children.Add(body);
            var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14), Child = layout };
            frame.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
            frame.SetResourceReference(Border.BorderBrushProperty, "Surface.Border");
            Content = frame;

            // Click-away closes - but not when the deactivation is our own question (AskAsync opens
            // an owned window over us), which comes back here when it ends.
            Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (!_asking && !IsActive && !OwnedWindows.OfType<Window>().Any(w => w.IsActive))
                {
                    Close();
                }
            }, DispatcherPriority.Background);
            Loaded += (_, _) =>
            {
                Refresh();
                _list.Focus();
            };
        }

        public IReadOnlyList<InboxItem> Items => _items;

        public int SelectedIndex => _list.SelectedIndex;

        public string ReplyText
        {
            get => _reply.Text;
            set => _reply.Text = value;
        }

        public bool ReplyHasFocus => _reply.IsKeyboardFocused;

        /// <summary>Rebuilds the rows; keeps the selection on the same tab, or the next one when it left.</summary>
        public void Refresh()
        {
            var selectedTab = _list.SelectedItem is ListBoxItem { Tag: InboxItem s } ? s.Tab : null;
            var previousIndex = _list.SelectedIndex;
            _items = Build(_shell.Tabs, DateTimeOffset.Now);
            _list.Items.Clear();
            foreach (var item in _items)
            {
                _list.Items.Add(Row(item));
            }

            _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var index = _items.ToList().FindIndex(i => ReferenceEquals(i.Tab, selectedTab));
            if (index < 0 && _items.Count > 0)
            {
                index = Math.Clamp(previousIndex, 0, _items.Count - 1);
            }

            _list.SelectedIndex = index;
            UpdatePreview();
        }

        /// <summary>Every second: ages and the preview.</summary>
        public void Tick()
        {
            if (++_tick % 2 == 0)
            {
                Refresh();
            }
            else
            {
                UpdatePreview();
            }
        }

        private InboxItem? Selected => _list.SelectedItem is ListBoxItem { Tag: InboxItem item } ? item : null;

        private ListBoxItem Row(InboxItem item)
        {
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(Border.BackgroundProperty, item.Tab.State switch { AgentState.Blocked => "State.Blocked", AgentState.Error => "State.Error", _ => "State.Done" });

            var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            title.Inlines.Add(new System.Windows.Documents.Run(item.Tab.Label) { FontWeight = FontWeights.SemiBold });
            title.Inlines.Add(new System.Windows.Documents.Run($"  {item.Kind} \u00B7 {item.Age}"));
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            title.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Body");

            var line = new TextBlock { Text = item.Line, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            line.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");

            var channel = new TextBlock { Text = item.Channel switch { ReplyChannel.Integration => "answers go through the integration", ReplyChannel.Keys => "y / n typed into the tab", ReplyChannel.TypedText => "replies are typed into the tab", _ => string.Empty }, Margin = new Thickness(0, 2, 0, 0) };
            channel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");
            channel.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");

            var text = new StackPanel { Width = 380 };
            text.Children.Add(title);
            text.Children.Add(line);
            text.Children.Add(channel);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 4) };
            row.Children.Add(dot);
            row.Children.Add(text);
            return new ListBoxItem { Content = row, Tag = item, Padding = new Thickness(6, 4, 6, 4) };
        }

        private void UpdatePreview()
        {
            var item = Selected;
            if (item is null)
            {
                _preview.Text = string.Empty;
                _hint.Text = _items.Count == 0 ? "Esc closes" : string.Empty;
                return;
            }

            item.Tab.RequestScreen();
            _preview.Text = string.Join('\n', item.Tab.ScreenRows.TakeLast(22));
            _hint.Text = item.Channel switch
            {
                ReplyChannel.Integration => "y approve \u00B7 n deny \u00B7 type a reply + Enter (through the integration) \u00B7 Enter jump \u00B7 A approve all \u00B7 Esc",
                ReplyChannel.Keys => "y approve \u00B7 n deny (typed) \u00B7 type a reply + Enter \u00B7 Enter jump \u00B7 A approve all \u00B7 Esc",
                _ => "type a reply + Enter \u00B7 Enter jump \u00B7 Esc  (no one-key answers for this tab)",
            };
        }

        private void List_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    Close();
                    break;
                case Key.Return when Selected is { } jump:
                    e.Handled = true;
                    var tab = jump.Tab;
                    Close();
                    _shell.Activate(tab);
                    break;
                case Key.J:
                    e.Handled = true;
                    Move(1);
                    break;
                case Key.K:
                    e.Handled = true;
                    Move(-1);
                    break;
                case Key.Y when !shift:
                    e.Handled = true;
                    Answer(approve: true);
                    break;
                case Key.N when !shift:
                    e.Handled = true;
                    Answer(approve: false);
                    break;
                case Key.A when shift:
                    e.Handled = true;
                    _ = ApproveAllAsync();
                    break;
                case Key.I:
                case Key.OemSemicolon when shift: // ':'
                    e.Handled = true;
                    _reply.Focus();
                    break;
                case Key.Tab:
                    e.Handled = true;
                    _reply.Focus();
                    break;
            }
        }

        private void Reply_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    if (_reply.Text.Length > 0)
                    {
                        _reply.Clear();
                    }
                    else
                    {
                        _list.Focus();
                    }

                    break;
                case Key.Return:
                    e.Handled = true;
                    SendReply();
                    break;
                case Key.Tab:
                    e.Handled = true;
                    _list.Focus();
                    break;
            }
        }

        private void Move(int delta)
        {
            if (_list.Items.Count == 0)
            {
                return;
            }

            _list.SelectedIndex = Math.Clamp((_list.SelectedIndex < 0 ? 0 : _list.SelectedIndex) + delta, 0, _list.Items.Count - 1);
            _list.ScrollIntoView(_list.SelectedItem);
        }

        /// <summary>y / n on the selected item; stays in the inbox, the item leaves when the tab moves on.</summary>
        internal void Answer(bool approve)
        {
            if (Selected is not { } item)
            {
                return;
            }

            if (item.Tab.State == AgentState.Done)
            {
                // Nothing to answer: y on a finished tab acknowledges it by jumping there.
                var tab = item.Tab;
                Close();
                _shell.Activate(tab);
                return;
            }

            if (!item.Tab.Answer(approve))
            {
                _shell.Ui.Status($"{item.Tab.Label}: no one-key answer for this harness - type a reply, or Enter to go there");
                return;
            }

            _shell.Ui.Status($"{(approve ? "Approved" : "Denied")} in {item.Tab.Label}");
        }

        /// <summary>Free text to the selected item: a question's answer or a prompt.</summary>
        internal void SendReply()
        {
            var text = _reply.Text.Trim();
            if (text.Length == 0 || Selected is not { } item)
            {
                return;
            }

            item.Tab.Reply(text);
            _reply.Clear();
            _shell.Ui.Status($"Sent to {item.Tab.Label}");
            _list.Focus();
        }

        /// <summary>Approve every blocked item that can be approved, after asking.</summary>
        internal async Task ApproveAllAsync()
        {
            var candidates = _items.Where(i => i.Tab.State == AgentState.Blocked && i.Channel is ReplyChannel.Integration or ReplyChannel.Keys).ToList();
            if (candidates.Count == 0)
            {
                _shell.Ui.Status("Nothing here can be approved with one key");
                return;
            }

            var names = string.Join(", ", candidates.Select(c => c.Tab.Label));
            _asking = true;
            int answer;
            try
            {
                answer = await _shell.Ui.AskAsync($"Approve {candidates.Count} waiting {(candidates.Count == 1 ? "agent" : "agents")}? ({names})", ["Approve all", "Keep waiting"]);
            }
            finally
            {
                _asking = false;
                Activate();
                _list.Focus();
            }

            if (answer != 0)
            {
                return;
            }

            var approved = 0;
            foreach (var item in candidates)
            {
                if (item.Tab.Answer(approve: true))
                {
                    approved++;
                }
            }

            _shell.Ui.Status($"Approved {approved} of {candidates.Count}");
        }
    }
}
