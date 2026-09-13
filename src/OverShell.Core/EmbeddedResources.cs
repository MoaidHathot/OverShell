using System.Reflection;

namespace OverShell.Core;

/// <summary>Bundled defaults shipped inside the assembly under <c>Resources/</c>.</summary>
public static class EmbeddedResources
{
    private static readonly Assembly Assembly = typeof(EmbeddedResources).Assembly;

    /// <summary>Reads a resource by its path below <c>Resources/</c>, e.g. <c>agents/opencode.jsonc</c>.</summary>
    public static string Read(string relativePath)
    {
        var name = ResourceName(relativePath);
        using var stream = Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource '{relativePath}' ({name}) is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static bool Exists(string relativePath) =>
        Assembly.GetManifestResourceInfo(ResourceName(relativePath)) is not null;

    /// <summary>Names of all resources below a folder, e.g. <c>agents/</c> → <c>agents/opencode.jsonc</c>…</summary>
    public static IEnumerable<string> List(string folder)
    {
        var prefix = ResourceName(folder.TrimEnd('/') + "/");
        foreach (var name in Assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return folder.TrimEnd('/') + "/" + name[prefix.Length..];
            }
        }
    }

    // MSBuild turns Resources/agents/opencode.jsonc into OverShell.Core.Resources.agents.opencode.jsonc.
    private static string ResourceName(string relativePath) =>
        "OverShell.Core.Resources." + relativePath.Replace('\\', '/').Replace('/', '.');
}
