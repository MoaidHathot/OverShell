using System.IO;
using OverShell.Core;
using OverShell.Core.Extensibility;
using OverShell.Core.Herd;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// The herd log (DESIGN.md §12.19): every tab opened or closed, every state change, every
/// call for attention, written as it happens to <c>state\logs\&lt;stamp&gt;.jsonl</c> - one
/// file per run, the newest few dozen kept. <c>herd.log</c> (herd mode <c>L</c>) shows this
/// run's entries newest first in a picker; choosing one goes to its tab. Built on the API's
/// events alone, so what it records is exactly what an extension can see.
/// </summary>
public sealed class HerdLogExtension : IExtension
{
    private IShell _shell = null!;
    private HerdLog? _log;
    private readonly List<HerdLogEntry> _entries = [];

    public string Id => "herd.log";

    public sealed class LogSettings
    {
        /// <summary>How many run files to keep under logs\; older ones are deleted at start.</summary>
        public int Keep { get; init; } = 30;
    }

    /// <summary>The file this run writes; for diagnostics.</summary>
    public string? Path => _log?.Path;

    /// <summary>This run's entries so far, oldest first; for diagnostics.</summary>
    public IReadOnlyList<HerdLogEntry> Entries => _entries;

    public void Initialize(IShell shell)
    {
        _shell = shell;
        var settings = shell.ExtensionSettings<LogSettings>(Id);
        try
        {
            HerdLog.Prune(AppPaths.LogsDir, Math.Max(1, settings.Keep) - 1);
            _log = new HerdLog(System.IO.Path.Combine(AppPaths.LogsDir, HerdLog.FileName(DateTimeOffset.Now)));
            shell.Trace($"herd log: {_log.Path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            shell.Trace($"herd log: could not open a file under {AppPaths.LogsDir}: {e.Message} - the log is in memory only this run");
        }

        shell.Commands.Register("herd.log", "Herd log: what happened", "Agents", () => _ = ShowAsync(), () => true,
            "Every state change and call for attention this run, newest first; Enter goes to the tab. The file is under the state folder's logs\\");
        shell.Keys.AddDefaults([new Keybinding($"{HerdModeExtension.Leader} L", "herd.log")]);

        shell.TabOpened += tab => Record(new HerdLogEntry(DateTimeOffset.Now, "opened", tab.Id, tab.Label, tab.Harness, null, null, tab.WorkingDirectory));
        shell.TabClosed += tab => Record(new HerdLogEntry(DateTimeOffset.Now, "closed", tab.Id, tab.Label, tab.Harness, tab.State.ToString(), null, null));
        shell.TabStateChanged += (tab, t) => Record(new HerdLogEntry(t.At, "state", tab.Id, tab.Label, tab.Harness, t.From.ToString(), t.To.ToString(), t.Reason));
        shell.TabAttention += (tab, a) => Record(new HerdLogEntry(a.At, "attention", tab.Id, tab.Label, tab.Harness, null, a.State.ToString(), a.Message ?? a.Reason));
        shell.Ready += () => Record(new HerdLogEntry(DateTimeOffset.Now, "run", "-", null, null, null, null, $"{shell.Tabs.Count} tab(s), view {shell.ViewId}"));
    }

    private void Record(HerdLogEntry entry)
    {
        _entries.Add(entry);
        _log?.Append(entry);
    }

    private async Task ShowAsync()
    {
        var items = new List<PickItem>();
        foreach (var entry in Enumerable.Reverse(_entries).Take(300))
        {
            var age = DateTimeOffset.Now - entry.At;
            var hint = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago" : $"{age.TotalHours:F1} h ago";
            var glyph = entry.Kind switch { "attention" => "\uE7BA", "state" => "\uE916", "opened" => "\uE710", "closed" => "\uE711", _ => "\uE7C4" };
            items.Add(new PickItem(entry.Describe(), entry.Detail, hint, glyph) { Tag = entry });
        }

        if (items.Count == 0)
        {
            _shell.Ui.Status("Herd log: nothing has happened yet this run");
            return;
        }

        var picked = await _shell.Ui.PickAsync("\uE7C4", $"Herd log - {_entries.Count} event(s) this run{(_log is null ? string.Empty : $"; file {_log.Path}")}", items);
        if (picked?.Tag is HerdLogEntry chosen)
        {
            if (_shell.Find(chosen.Tab) is { } tab)
            {
                _shell.Activate(tab);
            }
            else if (chosen.Kind != "run")
            {
                _shell.Ui.Status($"{chosen.Label ?? chosen.Tab}: that tab is closed");
            }
        }
    }

    public void Dispose()
    {
        _log?.Dispose();
    }
}
