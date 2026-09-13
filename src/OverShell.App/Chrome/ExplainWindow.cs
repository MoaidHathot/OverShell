using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OverShell.App.Chrome;

/// <summary>
/// "Why is this tab in this state?" — the evidence trail for one tab (DESIGN.md §12.3):
/// harness and how it was found, authority, the current explanation, session and resume
/// command, what runs below the shell, and the last transitions with their reasons.
/// An owned, activated window like the palette (§12.7, spike 3); refreshes while open.
/// </summary>
public sealed class ExplainWindow : Window
{
    private readonly TerminalTab _tab;
    private readonly TextBlock _body;
    private readonly DispatcherTimer _timer;
    private bool _closing;

    public ExplainWindow(Window owner, TerminalTab tab)
    {
        _tab = tab;
        Owner = owner;
        Title = "OverShell explain";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = owner.Left + ((owner.ActualWidth - Width) / 2);
        Top = owner.Top + 72;

        _body = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("Text.Primary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 14),
        };

        var header = new TextBlock
        {
            Text = "Explain  ·  Esc to close",
            FontFamily = (FontFamily)FindResource("Font.Ui"),
            FontSize = (double)FindResource("Font.Size.Small"),
            Foreground = (Brush)FindResource("Text.Disabled"),
            Margin = new Thickness(16, 10, 16, 0),
        };

        Content = new Border
        {
            Background = (Brush)FindResource("Surface.Raised"),
            BorderBrush = (Brush)FindResource("Surface.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
            Child = new StackPanel { Children = { header, _body } },
        };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();

        Deactivated += (_, _) => CloseOnce();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseOnce();
            }
        };
        Loaded += (_, _) => { Focus(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();

        Refresh();
    }

    public string Text => _body.Text;

    private void Refresh()
    {
        var tab = _tab;
        var agent = tab.Agent;
        var sb = new StringBuilder();

        sb.AppendLine($"{tab.Label}  [{tab.Id}]  {tab.Profile.Name}");
        sb.AppendLine($"state      {tab.State}{(tab.Unread ? "  (unseen)" : string.Empty)}   since {tab.ActivityText}");
        sb.AppendLine($"why        {agent.Explain}");
        sb.AppendLine($"authority  {agent.Authority}{(agent.AuthoritySource is null ? string.Empty : " — " + agent.AuthoritySource)}");
        sb.AppendLine($"harness    {(tab.IsAgent ? $"{tab.Harness ?? "generic"} ({agent.Rules.DisplayName}), rules '{agent.Rules.Id}'" : "none (shell)")}");
        if (agent.SessionId is not null)
        {
            sb.AppendLine($"session    {agent.SessionId}");
        }

        if (tab.ResumeCommand is not null)
        {
            sb.AppendLine($"resume     {tab.ResumeCommand}");
        }

        if (agent.Summary is not null)
        {
            sb.AppendLine($"summary    {agent.Summary}");
        }

        sb.AppendLine($"where      {tab.ProjectAndBranch}   {tab.WorkingDirectory}");
        if (tab.LastProcessImages.Count > 0)
        {
            sb.AppendLine($"processes  {string.Join(", ", tab.LastProcessImages)}");
        }

        if (tab.Group is not null)
        {
            sb.AppendLine($"group      {tab.Group}");
        }

        var history = tab.History;
        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("transitions (newest first)");
            foreach (var t in history.Reverse().Take(20))
            {
                sb.AppendLine($"  {t.At:HH:mm:ss}  {t.From,-8} → {t.To,-8} {t.Reason}");
            }
        }

        var text = sb.ToString().TrimEnd();
        if (_body.Text != text)
        {
            _body.Text = text;
        }
    }

    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }
}
