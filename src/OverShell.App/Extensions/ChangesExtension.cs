using System.Diagnostics;
using System.IO;
using OverShell.Core.Agents;
using OverShell.Core.Extensibility;
using OverShell.Core.Git;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// Changes while you were away (DESIGN.md §12.19). When an agent's turn ends in a tab you
/// are not looking at, its repository's <c>git status</c> is compared with the one taken
/// when you last looked: the files that changed in between are counted on the tab's item
/// and listed by <c>tab.changes</c> (herd mode <c>D</c>); Enter opens a file. Count and list
/// ride in <see cref="ITab.Properties"/> (<c>git.changes</c>, <c>git.changes.files</c>), so a
/// restart keeps both. Looking at the tab clears the mark - the list stays readable until
/// the next turn starts a fresh one - and takes the next baseline. Needs git on PATH; costs
/// one <c>git status</c> per finished turn, nothing while agents work.
/// </summary>
public sealed class ChangesExtension : IExtension
{
    internal const string CountKey = "git.changes";
    internal const string FilesKey = "git.changes.files";

    private IShell _shell = null!;
    private readonly bool _gitAvailable = Core.Integrations.IntegrationInstaller.OnPath("git");
    private readonly Dictionary<string, IReadOnlyList<GitChange>> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<GitChange>> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "git.changes";

    /// <summary>The files changed while away for a tab, for the picker and the self-test; empty when none.</summary>
    public IReadOnlyList<GitChange> ChangesFor(ITab tab) => _changes.TryGetValue(tab.Id, out var list) ? list : [];

    /// <summary>What the tab's item shows: the count from the property, 0 when none.</summary>
    public static int Count(ITab tab) => tab.Properties.TryGetValue(CountKey, out var v) && int.TryParse(v, out var n) ? n : 0;

    public void Initialize(IShell shell)
    {
        _shell = shell;
        if (!_gitAvailable)
        {
            shell.Trace("git.changes: git not on PATH; changes while away off");
        }

        shell.Commands.Register("tab.changes", "Changes while you were away", "Agents", () => _ = ShowAsync(), () => shell.TargetTab is not null,
            "The files an agent changed in its repository since you last looked at the tab; Enter opens one");
        shell.Keys.AddDefaults([new Keybinding($"{HerdModeExtension.Leader} D", "tab.changes")]);

        shell.TabOpened += tab =>
        {
            Restore(tab);
            _ = BaselineAsync(tab);
        };
        shell.TabClosed += tab =>
        {
            _baselines.Remove(tab.Id);
            _changes.Remove(tab.Id);
            _seen.Remove(tab.Id);
        };
        shell.ActiveTabChanged += tab =>
        {
            if (tab is not null)
            {
                Clear(tab);
                _ = BaselineAsync(tab);
            }
        };
        shell.TabStateChanged += OnStateChanged;

        foreach (var tab in shell.Tabs)
        {
            Restore(tab);
            _ = BaselineAsync(tab);
        }
    }

    /// <summary>A restored tab brings its list back with its count (both ride in the session file), so the mark still means something.</summary>
    private void Restore(ITab tab)
    {
        if (_changes.ContainsKey(tab.Id) || !tab.Properties.TryGetValue(FilesKey, out var files) || files.Length == 0)
        {
            return;
        }

        var list = new List<GitChange>();
        foreach (var line in files.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab0 = line.IndexOf('\t');
            if (tab0 == 2)
            {
                list.Add(new GitChange(line[..2], line[3..]));
            }
        }

        if (list.Count > 0)
        {
            _changes[tab.Id] = list;
        }
    }

    private void Remember(ITab tab, IReadOnlyList<GitChange> list)
    {
        _changes[tab.Id] = list;
        tab.Properties[CountKey] = list.Count.ToString();
        tab.Properties[FilesKey] = string.Join('\n', list.Select(c => $"{c.Status}\t{c.Path}"));
        _shell.TabPropertiesChanged(tab);
    }

    private void OnStateChanged(ITab tab, AgentTransition transition)
    {
        // A turn ended: Working -> anything that is not Working, in a tab not being looked at.
        // Done is the usual (finished unseen); Blocked and Idle count too, since files may have
        // changed before the question. Never for the active tab - you are looking at it.
        if (transition.From != AgentState.Working || transition.To == AgentState.Working || tab.IsActive || !tab.IsAgent)
        {
            return;
        }

        _ = CompareAsync(tab);
    }

    private static string? RootOf(ITab tab) => GitRepository.FindRoot(tab.WorkingDirectory);

    internal async Task BaselineAsync(ITab tab)
    {
        if (!_gitAvailable || RootOf(tab) is not { } root)
        {
            return;
        }

        var status = await GitStatus.RunAsync(root).ConfigureAwait(true);
        if (status is not null && _shell.Find(tab.Id) is not null)
        {
            _baselines[tab.Id] = status;
        }
    }

    internal async Task CompareAsync(ITab tab)
    {
        if (!_gitAvailable || RootOf(tab) is not { } root || !_busy.Add(tab.Id))
        {
            return;
        }

        try
        {
            var now = await GitStatus.RunAsync(root).ConfigureAwait(true);
            if (now is null || _shell.Find(tab.Id) is null)
            {
                return;
            }

            if (!_baselines.TryGetValue(tab.Id, out var before))
            {
                // No baseline yet (git was slow at open): this is it; nothing to compare against.
                _baselines[tab.Id] = now;
                return;
            }

            var changed = GitStatus.Compare(before, now);
            // The baseline moves on, so the next turn reports only its own changes; the list shown
            // accumulates across turns until the tab is looked at, and starts afresh with the first
            // turn after that - what you read on arrival stays readable until then.
            _baselines[tab.Id] = now;
            if (changed.Count == 0)
            {
                return;
            }

            var merged = new Dictionary<string, GitChange>(StringComparer.Ordinal);
            foreach (var change in (_seen.Remove(tab.Id) ? [] : ChangesFor(tab)).Concat(changed))
            {
                merged[change.Path] = change;
            }

            Remember(tab, merged.Values.OrderBy(c => c.Path, StringComparer.Ordinal).ToList());
            _shell.Trace($"[{tab.Id}] changes while away: {_changes[tab.Id].Count} file(s) ({string.Join(", ", changed.Take(5).Select(c => $"{c.Describe()} {c.Path}"))}{(changed.Count > 5 ? ", ..." : string.Empty)})");
        }
        finally
        {
            _busy.Remove(tab.Id);
        }
    }

    /// <summary>Looking at the tab: the mark goes, the list stays readable (tab.changes on arrival) until the next turn replaces it.</summary>
    private void Clear(ITab tab)
    {
        if (_changes.ContainsKey(tab.Id))
        {
            _seen.Add(tab.Id);
        }

        var had = tab.Properties.Remove(CountKey);
        had |= tab.Properties.Remove(FilesKey);
        if (had)
        {
            _shell.TabPropertiesChanged(tab);
        }
    }

    private async Task ShowAsync()
    {
        if (_shell.TargetTab is not { } tab)
        {
            return;
        }

        var changes = ChangesFor(tab);
        if (changes.Count == 0)
        {
            _shell.Ui.Status(_gitAvailable
                ? $"{tab.Label}: no changes since you last looked{(RootOf(tab) is null ? " (not in a git repository)" : string.Empty)}"
                : "Changes while away needs git on PATH");
            return;
        }

        var root = RootOf(tab) ?? tab.WorkingDirectory ?? string.Empty;
        var items = changes.Select(c => new PickItem(c.Path, c.Describe(), c.Status.Trim(), GlyphFor(c)) { Tag = c }).ToList();
        var picked = await _shell.Ui.PickAsync("\uE70F", $"{tab.Label}: {changes.Count} file(s) changed while you were away - Enter opens", items);
        if (picked?.Tag is GitChange chosen)
        {
            var path = Path.Combine(root, chosen.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                _shell.Ui.Status($"{chosen.Path}: {(chosen.Describe() == "deleted" ? "deleted" : "not there any more")}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _shell.Ui.Status($"{chosen.Path}: could not open ({e.Message})");
            }
        }
    }

    private static string GlyphFor(GitChange change) => change.Describe() switch
    {
        "added" or "untracked" => "\uE710",
        "deleted" => "\uE74D",
        "renamed" => "\uE8AC",
        "conflict" => "\uE7BA",
        "clean" => "\uE73E",
        _ => "\uE70F",
    };

    public void Dispose()
    {
    }
}
