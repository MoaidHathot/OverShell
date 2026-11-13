namespace OverShell.Core.Tabs;

/// <summary>
/// The shell's location and a reported session's location have different owners. In
/// particular, an agent's tool processes do not move its selected session or the shell.
/// All observations are applied on the host's UI thread; probes retain a revision across await.
/// </summary>
public sealed class TabDirectory(string? startingDirectory)
{
    private string? _shellDirectory = startingDirectory;
    private string? _sessionDirectory;
    private string? _sessionId;

    public string? Current => _sessionDirectory ?? _shellDirectory;
    public string? IntegrationSource { get; private set; }
    public bool HasSessionDirectory => _sessionDirectory is not null;
    public bool AnnouncesDirectory { get; private set; }
    public long Revision { get; private set; }

    public void Announce(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        AnnouncesDirectory = true;
        _shellDirectory = directory;
        Revision++;
    }

    /// <summary>Status-only reports retain the directory; a new session/source cannot inherit it.</summary>
    public void Report(string source, string? sessionId, string? directory)
    {
        if (!string.Equals(source, IntegrationSource, StringComparison.OrdinalIgnoreCase) || sessionId != _sessionId)
            _sessionDirectory = null;
        IntegrationSource = source;
        _sessionId = sessionId;
        if (!string.IsNullOrWhiteSpace(directory)) _sessionDirectory = directory;
        Revision++;
    }

    public void Release(string? source = null)
    {
        if (IntegrationSource is null || source is not null && !string.Equals(source, IntegrationSource, StringComparison.OrdinalIgnoreCase)) return;
        IntegrationSource = null;
        _sessionId = null;
        _sessionDirectory = null;
        Revision++;
    }

    public bool Probe(string directory, bool aboutRootShell, long revision)
    {
        if (revision != Revision || HasSessionDirectory || aboutRootShell && AnnouncesDirectory || string.IsNullOrWhiteSpace(directory)) return false;
        _shellDirectory = directory;
        Revision++;
        return true;
    }
}
