using OverShell.Core.Integrations;
using Xunit;

namespace OverShell.Tests;

public class OpenCodeV2InstallerTests
{
    [Fact]
    public void Installs_discoverable_v2_entrypoints_without_editing_configuration() => InDirectory(root =>
    {
        var configuration = "{\n // user's settings\n \"plugins\": [\"their-plugin\"]\n}\n";
        File.WriteAllText(Path.Combine(root, "cli.json"), configuration);
        var status = IntegrationInstaller.InstallOpenCodeAt(root);
        Assert.True(status.Installed && status.Current, status.Note);
        Assert.Contains("@opencode/plugin/tui", File.ReadAllText(Path.Combine(root, "plugins", "overshell-v2", "tui.ts")));
        Assert.Contains("Plugin.define", File.ReadAllText(Path.Combine(root, "plugins", "overshell-v2", "index.ts")));
        Assert.False(File.Exists(Path.Combine(root, "plugins", "overshell.ts")));
        Assert.Equal(configuration, File.ReadAllText(Path.Combine(root, "cli.json")));
        Assert.True(IntegrationInstaller.InstallOpenCodeAt(root).Current);
        Assert.False(IntegrationInstaller.UninstallOpenCodeAt(root).Installed);
        Assert.Equal(configuration, File.ReadAllText(Path.Combine(root, "cli.json")));
    });

    [Fact]
    public void Migration_removes_the_owned_v1_entrypoint_after_installing_v2() => InDirectory(root =>
    {
        var plugins = Directory.CreateDirectory(Path.Combine(root, "plugins")).FullName;
        var legacy = Path.Combine(plugins, "overshell.ts");
        File.WriteAllText(legacy, IntegrationInstaller.OpenCodePluginContent());
        Assert.True(IntegrationInstaller.InstallOpenCodeAt(root).Current);
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(plugins, "overshell-v2", "tui.ts")));
    });

    [Fact]
    public void User_owned_plugin_files_are_kept() => InDirectory(root =>
    {
        var directory = Path.Combine(root, "plugins", "overshell-v2");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "tui.ts");
        File.WriteAllText(file, "// user's plugin\nexport default {}\n");
        var result = IntegrationInstaller.InstallOpenCodeAt(root);
        Assert.False(result.Installed);
        Assert.Contains("kept", result.Note);
        IntegrationInstaller.UninstallOpenCodeAt(root);
        Assert.StartsWith("// user's plugin", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(root, "plugins", "overshell.ts")));
    });

    private static void InDirectory(Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("overshell-opencode-v2");
        try { test(directory.FullName); }
        finally { directory.Delete(true); }
    }
}
