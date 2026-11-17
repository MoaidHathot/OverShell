using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using OverShell.Core.Extensibility;

namespace OverShell.App.Extensibility;

/// <summary>One opt-in DLL, with private dependencies and the host's shared API assemblies.</summary>
internal sealed class AssemblyExtension : IExtension
{
    private readonly ExtensionLoadContext _context;
    private readonly IExtension _extension;

    public AssemblyExtension(string id, string path, string typeName)
    {
        _context = new ExtensionLoadContext(path);
        try
        {
            var assembly = _context.LoadFromAssemblyPath(path);
            var type = assembly.GetType(typeName, throwOnError: true)!;
            if (!typeof(IExtension).IsAssignableFrom(type) || type.IsAbstract)
                throw new ArgumentException($"'{typeName}' must implement IExtension");
            _extension = (IExtension)Activator.CreateInstance(type)!;
            if (!string.Equals(id, _extension.Id, StringComparison.Ordinal))
            {
                try { _extension.Dispose(); }
                finally { throw new ArgumentException($"DLL id '{_extension.Id}' does not match configured id '{id}'"); }
            }
        }
        catch
        {
            _context.Unload();
            throw;
        }
    }

    public string Id => _extension.Id;
    public void Initialize(IShell shell) => _extension.Initialize(shell);
    public void Dispose()
    {
        try { _extension.Dispose(); }
        finally { _context.Unload(); }
    }

    private sealed class ExtensionLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            // Sharing these identities is what makes an external IExtension an IExtension here.
            foreach (var shared in new[] { typeof(IExtension).Assembly, typeof(Config.ProfileSettings).Assembly })
                if (name.Name == shared.GetName().Name) return shared;
            var dependency = _resolver.ResolveAssemblyToPath(name);
            if (dependency is null && name.Name is { } simple)
            {
                var sibling = Path.Combine(Path.GetDirectoryName(path)!, simple + ".dll");
                if (File.Exists(sibling)) dependency = sibling;
            }
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }

        protected override nint LoadUnmanagedDll(string name) =>
            _resolver.ResolveUnmanagedDllToPath(name) is { } library ? LoadUnmanagedDllFromPath(library) : 0;
    }
}
