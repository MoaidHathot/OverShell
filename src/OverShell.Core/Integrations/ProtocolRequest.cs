namespace OverShell.Core.Integrations;

/// <summary>What an <c>overshell://</c> URL asks for.</summary>
public enum ProtocolAction
{
    /// <summary><c>overshell://focus/&lt;tabId&gt;</c> — bring the window up on that tab (a toast was clicked).</summary>
    Focus,

    /// <summary><c>overshell://view/&lt;terminal|herd|dashboard|zen&gt;</c>.</summary>
    View,

    /// <summary><c>overshell://new?profile=&lt;id&gt;&amp;cwd=&lt;path&gt;</c> — open a tab.</summary>
    New,

    /// <summary><c>overshell://workspace/&lt;name&gt;</c> — open a workspace's tabs next to the open ones.</summary>
    Workspace,

    /// <summary><c>overshell://reopen</c> — the most recently closed tab.</summary>
    Reopen,

    /// <summary><c>overshell://history</c> — the session history picker; <c>overshell://history/&lt;archive stem&gt;</c> reopens that archive.</summary>
    History,

    /// <summary><c>overshell://reply/&lt;tabId&gt;?answer=approve|deny&amp;nonce=...</c> - a toast button answered a blocked agent (§12.17). The nonce is per run; without the right one the request only focuses the tab.</summary>
    Reply,

    /// <summary><c>overshell://</c> alone, or anything unknown: just show the window.</summary>
    Show,
}

/// <summary>A parsed <c>overshell://</c> request. Pure, so the second instance and the running one agree on it.</summary>
public sealed record ProtocolRequest(ProtocolAction Action, string? Target, IReadOnlyDictionary<string, string> Query)
{
    public const string Scheme = "overshell";

    public static bool IsProtocolUrl(string? text) =>
        text is not null && text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static ProtocolRequest? Parse(string? text)
    {
        if (!IsProtocolUrl(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // overshell://focus/abc → host "focus", path "/abc". overshell:focus/abc (no slashes) is
        // tolerated by treating the whole authority-less path the same way.
        var host = uri.Host;
        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(host) && path.Length > 0)
        {
            var slash = path.IndexOf('/');
            host = slash < 0 ? path : path[..slash];
            path = slash < 0 ? string.Empty : path[(slash + 1)..];
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            query[key] = value;
        }

        var target = path.Length == 0 ? null : Uri.UnescapeDataString(path);
        return host.ToLowerInvariant() switch
        {
            "focus" when target is not null => new ProtocolRequest(ProtocolAction.Focus, target, query),
            "view" when target is not null => new ProtocolRequest(ProtocolAction.View, target.ToLowerInvariant(), query),
            "new" => new ProtocolRequest(ProtocolAction.New, target, query),
            "workspace" when target is not null => new ProtocolRequest(ProtocolAction.Workspace, target, query),
            "reopen" => new ProtocolRequest(ProtocolAction.Reopen, null, query),
            "history" => new ProtocolRequest(ProtocolAction.History, target, query),
            "reply" when target is not null => new ProtocolRequest(ProtocolAction.Reply, target, query),
            _ => new ProtocolRequest(ProtocolAction.Show, null, query),
        };
    }

    public static string FocusUrl(string tabId) => $"{Scheme}://focus/{Uri.EscapeDataString(tabId)}";

    /// <summary>A toast button's answer for a tab, signed with the run's nonce so a stray URL cannot approve anything.</summary>
    public static string ReplyUrl(string tabId, bool approve, string nonce) => $"{Scheme}://reply/{Uri.EscapeDataString(tabId)}?answer={(approve ? "approve" : "deny")}&nonce={Uri.EscapeDataString(nonce)}";

    public static string WorkspaceUrl(string name) => $"{Scheme}://workspace/{Uri.EscapeDataString(name)}";

    public static string HistoryUrl(string? archiveStem = null) => archiveStem is null ? $"{Scheme}://history" : $"{Scheme}://history/{Uri.EscapeDataString(archiveStem)}";

    public const string ReopenUrl = Scheme + "://reopen";

    public const string NewUrl = Scheme + "://new";
}
