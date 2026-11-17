using System.IO;
using System.Text.Json.Nodes;
using OverShell.Core;
using OverShell.Core.Extensibility;

namespace OverShell.App.Extensibility;

/// <summary>
/// Loads the extensions and runs their lifecycle (DESIGN.md §12.16). The built-in ones are
/// listed here; <c>settings.extensions.&lt;id&gt;.enabled: false</c> keeps one out. A failing
/// extension is reported and skipped - a feature built on top must never take the
/// terminal down with it.
/// </summary>
internal sealed class ExtensionHost : IDisposable
{
    private readonly List<IExtension> _loaded = [];
    private readonly HashSet<string> _builtInIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (IExtension Extension, string Identity)> _configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _stopping = new(StringComparer.OrdinalIgnoreCase);
    private IShell? _shell;
    private string _configDirectory = string.Empty;
    private string _stateDirectory = string.Empty;

    /// <summary>The built-in extensions, in load order.</summary>
    public static IReadOnlyList<Func<IExtension>> BuiltIns { get; } =
    [
        () => new Extensions.HerdModeExtension(),
        () => new Extensions.TabHintsExtension(),
        () => new Extensions.MruSwitcherExtension(),
        () => new Extensions.SummonExtension(),
        () => new Extensions.KeyNavExtension(),
        () => new Extensions.InboxExtension(),
        () => new Extensions.TriageExtension(),
        () => new Extensions.SpawnExtension(),
        () => new Extensions.HerdLogExtension(),
        () => new Extensions.ChangesExtension(),
    ];

    public IReadOnlyList<IExtension> Loaded => _loaded.Concat(_configured.Values.Select(item => item.Extension)).ToArray();

    public void Load(IShell shell, IEnumerable<Func<IExtension>> factories)
    {
        foreach (var factory in factories)
        {
            IExtension? extension = null;
            try
            {
                extension = factory();
                _builtInIds.Add(extension.Id);
                if (!shell.Settings.ExtensionEnabled(extension.Id))
                {
                    shell.Trace($"extension {extension.Id}: disabled in settings");
                    extension.Dispose();
                    continue;
                }

                extension.Initialize(shell);
                _loaded.Add(extension);
                shell.Trace($"extension {extension.Id}: loaded");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                shell.Trace($"extension {extension?.Id ?? factory.Method.Name} failed to load: {e.GetType().Name}: {e.Message}");
                if (extension is not null) DisposeExtension(extension, shell.Trace);
            }
        }
    }

    /// <summary>Configured commands and DLLs share ids/settings with built-ins; none are discovered or run implicitly.</summary>
    public void LoadConfigured(IShell shell, string configDirectory, string stateDirectory)
    {
        _shell = shell;
        _configDirectory = configDirectory;
        _stateDirectory = stateDirectory;
        shell.SettingsChanged += ReloadConfigured;
        ReloadConfigured();
    }

    private void ReloadConfigured()
    {
        var shell = _shell!;
        var desired = new Dictionary<string, (JsonObject Settings, string Identity)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, node) in shell.Settings.Extensions)
        {
            if (node is not JsonObject settings || !shell.Settings.ExtensionEnabled(id)) continue;
            var process = settings["process"];
            var assembly = settings["assembly"];
            if (process is null && assembly is null) continue;
            if (process is not null && assembly is not null || _builtInIds.Contains(id))
            {
                shell.Trace($"extension {id}: choose one host (process or assembly), with an id distinct from built-ins");
                continue;
            }
            // A command owns launch-setting changes itself, so stop/start is serialized.
            desired[id] = (settings, process is not null ? "process" : "assembly:" + assembly!.ToJsonString());
        }
        foreach (var (id, loaded) in _configured.ToArray())
        {
            if (desired.TryGetValue(id, out var item) && item.Identity == loaded.Identity) continue;
            DisposeExtension(loaded.Extension, shell.Trace);
            if (loaded.Extension is CommandExtension command) _stopping[id] = command.Completion;
            _configured.Remove(id);
        }
        foreach (var (id, item) in desired)
        {
            if (_configured.ContainsKey(id)) continue;
            IExtension? extension = null;
            try
            {
                if (item.Identity == "process")
                    extension = new CommandExtension(id, _configDirectory, _stateDirectory, _stopping.GetValueOrDefault(id));
                else
                {
                    var assembly = item.Settings["assembly"]!.AsObject();
                    var path = assembly["path"]?.GetValue<string>() ?? throw new ArgumentException("assembly.path is required");
                    path = Path.GetFullPath(ProcessExtensionSettings.ExpandPath(path, _configDirectory, _stateDirectory), _configDirectory);
                    var type = assembly["type"]?.GetValue<string>() ?? throw new ArgumentException("assembly.type is required");
                    extension = new AssemblyExtension(id, path, type);
                }
                extension.Initialize(shell);
                _configured.Add(id, (extension, item.Identity));
                _stopping.Remove(id);
                shell.Trace($"extension {id}: loaded from configuration");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                shell.Trace($"extension {id}: failed to load: {e.GetType().Name}: {e.Message}");
                if (extension is not null) DisposeExtension(extension, shell.Trace);
            }
        }
    }

    public void Dispose()
    {
        if (_shell is not null) _shell.SettingsChanged -= ReloadConfigured;
        var commands = _configured.Values.Select(item => item.Extension).OfType<CommandExtension>().ToArray();
        foreach (var extension in Loaded)
        {
            DisposeExtension(extension, Diagnostics.TraceLog.Agents.Write);
        }
        // Workers never await the dispatcher. Give commands a short chance to clear their
        // published state before the process exits; the job objects cover a crash/timeout.
        try { Task.WhenAll(commands.Select(command => command.Completion).Concat(_stopping.Values)).Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException e) { Diagnostics.TraceLog.Agents.Write($"extension shutdown: {e.GetBaseException().Message}"); }
        _configured.Clear();
        _stopping.Clear();
        _loaded.Clear();
    }

    private static void DisposeExtension(IExtension extension, Action<string> trace)
    {
        try { extension.Dispose(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            trace($"extension {extension.Id} failed to dispose: {e.Message}");
        }
    }
}
