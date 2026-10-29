using OverShell.Core.Tabs;

namespace OverShell.App;

public sealed partial class TerminalTab
{
    private string _labelPrefix = string.Empty;
    private (string Text, string Project, bool Manual)? _lastLabelInputs;

    /// <summary>Stable across restores/reopens; independent of the runtime integration address.</summary>
    public string NamingId { get; }

    internal event Action<TerminalTab>? LabelInputsChanged;

    private bool NamingAsAgent => IsAgent || _pendingResume is not null;

    private string? TerminalTaskTitle => TabLabels.FromTerminal(_shellTitle, Harness, Agent.Rules.DisplayName, Profile.Name, Project);

    /// <summary>A task/session name, not a transient notification or tool summary.</summary>
    public string? SessionTitle => !NamingAsAgent ? null : Agent.SessionTitleKnown ? Agent.SessionTitle : Agent.SessionTitleIsSeeded
        ? Agent.SessionTitle
        : Agent.SessionTitle ?? TerminalTaskTitle ??
          (Harness == "opencode" && Agent.AuthoritySource == "opencode" ? TabLabels.SessionName(Agent.Summary) : null);

    internal string BaseLabel => _userLabel ?? TabLabels.Primary(TabSettings.EffectiveLabelMode, NamingAsAgent,
        SessionTitle, NamingAsAgent ? Agent.SessionTitleKnown || Agent.SessionTitleIsSeeded ? null : TerminalTaskTitle : Title, Project, Agent.Rules.DisplayName, Profile.Name);

    public string Label => _labelPrefix + BaseLabel;

    public string? Alias { get; private set; }
    public string ShortcutHint { get; private set; } = string.Empty;
    public string DisplayLabel => (Floating ? "◇ " : string.Empty) + (Alias is not { } alias ? Label : TabSettings.AliasDisplay?.ToLowerInvariant() switch
    {
        "hidden" => Label,
        "primary" => alias,
        _ => $"@{alias} · {Label}",
    });

    internal void SetAlias(string? alias)
    {
        Alias = alias;
        Raise(nameof(Alias));
        Raise(nameof(DisplayLabel));
        Raise(nameof(Tooltip));
    }

    internal void SetShortcutHint(string hint)
    {
        ShortcutHint = hint;
        Raise(nameof(ShortcutHint));
        Raise(nameof(Tooltip));
    }

    internal void RefreshLabelInputs()
    {
        var inputs = (BaseLabel, Project, _userLabel is not null);
        if (_lastLabelInputs == inputs) return;
        _lastLabelInputs = inputs;
        LabelInputsChanged?.Invoke(this);
        Raise(nameof(Label));
        Raise(nameof(DisplayLabel));
        Raise(nameof(Tooltip));
    }

    internal void SetLabelPrefix(string prefix)
    {
        if (_labelPrefix == prefix) return;
        _labelPrefix = prefix;
        Raise(nameof(Label));
        Raise(nameof(DisplayLabel));
        Raise(nameof(Tooltip));
    }

    private string AgentDetail()
    {
        if (TabSettings.EffectiveLabelMode == TabLabelMode.Harness)
            return Join(StateText, Agent.Summary ?? Project);
        var context = TabSettings.EffectiveLabelMode == TabLabelMode.Project && _userLabel is null
            ? Branch is null ? string.Empty : Dirty == true ? Branch + "*" : Branch
            : ProjectAndBranch;
        var activity = TabLabels.Clean(Agent.Summary);
        if (activity.Equals(SessionTitle, StringComparison.OrdinalIgnoreCase) || activity.Equals(BaseLabel, StringComparison.OrdinalIgnoreCase)) activity = string.Empty;
        return Join(context, StateText, activity);
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
