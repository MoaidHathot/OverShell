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

    /// <summary>The built-in extensions, in load order.</summary>
    public static IReadOnlyList<Func<IExtension>> BuiltIns { get; } =
    [
        () => new Extensions.HerdModeExtension(),
        () => new Extensions.MruSwitcherExtension(),
    ];

    public IReadOnlyList<IExtension> Loaded => _loaded;

    public void Load(IShell shell, IEnumerable<Func<IExtension>> factories)
    {
        foreach (var factory in factories)
        {
            IExtension? extension = null;
            try
            {
                extension = factory();
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
                extension?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var extension in _loaded)
        {
            try
            {
                extension.Dispose();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Diagnostics.TraceLog.Agents.Write($"extension {extension.Id} failed to dispose: {e.Message}");
            }
        }

        _loaded.Clear();
    }
}
