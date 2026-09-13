using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OverShell.Core.Integrations;

namespace OverShell.App;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(Path.GetTempPath(), "overshell-crash.log");

    private bool _errorShown;
    private SingleInstance? _instance;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint handle);

    private const uint AttachParentProcess = unchecked((uint)-1);
    private const int StdOutputHandle = -11;
    private const uint FileTypeChar = 0x0002;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record("AppDomain", args.ExceptionObject as Exception);

        base.OnStartup(e);

        // `OverShell integrations …` / `OverShell protocol …` are command-line tool runs, not a window.
        if (e.Args.Length > 0 && e.Args[0].Equals("integrations", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(RunIntegrationsCli(e.Args.Skip(1).ToArray()));
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("protocol", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(RunProtocolCli(e.Args.Skip(1).ToArray()));
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(RunSettingsCli(e.Args.Skip(1).ToArray()));
            return;
        }

        // One window per user: a second start hands its arguments (an overshell:// URL from a
        // toast click, typically) to the running one and leaves.
        _instance = new SingleInstance();
        if (_instance.TryHandOver(e.Args))
        {
            Shutdown(0);
            return;
        }

        // The skin goes on before the first window exists, so fonts and metrics resolve to
        // it too; later saves only recolour (Chrome/SkinLoader), which needs unfrozen brushes.
        Core.AppPaths.EnsureCreated();
        var unfrozen = Chrome.SkinLoader.PrepareThemeForLiveRecolour();
        Diagnostics.TraceLog.Agents.Write($"theme: {unfrozen} brush(es) made recolourable");
        var settings = Core.Settings.AppSettings.Load(File.Exists(Core.AppPaths.SettingsFile) ? Core.AppPaths.SettingsFile : null);
        if (Chrome.SkinLoader.Apply(settings.Skin) is { } skinProblem)
        {
            Diagnostics.TraceLog.Agents.Write($"skin: {skinProblem}");
        }

        var window = new MainWindow();
        MainWindow = window;

        _instance.Trace += line => Diagnostics.TraceLog.Agents.Write(line);
        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() => window.HandleArguments(args));
        _instance.Start();

        window.Loaded += (_, _) => window.HandleArguments(e.Args);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Where CLI output goes. A GUI process launched from a console has no console of its
    /// own, so the parent's is attached — but AttachConsole replaces the standard handles,
    /// which would lose a redirection like <c>OverShell integrations status &gt; out.txt</c>.
    /// So a redirected handle is used as-is, and the console is attached only otherwise.
    /// </summary>
    private static TextWriter OpenCliOutput()
    {
        var stdout = GetStdHandle(StdOutputHandle);
        if (stdout != 0 && stdout != -1 && GetFileType(stdout) != FileTypeChar)
        {
            var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stdout, ownsHandle: false), FileAccess.Write);
            return new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        }

        _ = AttachConsole(AttachParentProcess);
        return Console.Out;
    }

    /// <summary>
    /// <c>integrations status</c> · <c>integrations install &lt;opencode|copilot|claude|all&gt;</c> ·
    /// <c>integrations uninstall &lt;id|all&gt;</c> · <c>integrations show claude</c> (the hook
    /// entries, for adding by hand when settings.json cannot be rewritten).
    /// </summary>
    private static int RunIntegrationsCli(string[] args)
    {
        var out_ = OpenCliOutput();

        try
        {
            var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            var ids = args.Length > 1 && !args[1].Equals("all", StringComparison.OrdinalIgnoreCase)
                ? [args[1]]
                : IntegrationInstaller.Ids;

            switch (verb)
            {
                case "status":
                    out_.WriteLine();
                    foreach (var id in ids)
                    {
                        Print(out_, IntegrationInstaller.Status(id));
                    }

                    return 0;

                case "install":
                    out_.WriteLine();
                    var refused = false;
                    foreach (var id in ids)
                    {
                        var status = IntegrationInstaller.Install(id);
                        Print(out_, status);
                        refused |= !status.Installed;
                    }

                    out_.WriteLine("  Restart the harness inside an OverShell tab for the integration to load.");
                    return refused ? 1 : 0;

                case "uninstall":
                    out_.WriteLine();
                    foreach (var id in ids)
                    {
                        Print(out_, IntegrationInstaller.Uninstall(id));
                    }

                    return 0;

                case "show":
                    out_.WriteLine();
                    var which = args.Length > 1 ? args[1].ToLowerInvariant() : "claude";
                    if (which == "codex")
                    {
                        out_.WriteLine($"  Codex CLI: add to {IntegrationInstaller.CodexConfigPath()} (top level, before any [table]):");
                        out_.WriteLine();
                        out_.WriteLine("  " + IntegrationInstaller.CodexNotifyLine());
                        out_.WriteLine();
                        out_.WriteLine($"  and put the script at {IntegrationInstaller.CodexScriptPath()} (`integrations install codex` writes it).");
                        return 0;
                    }

                    out_.WriteLine("  Claude Code: add under \"hooks\" in ~/.claude/settings.json:");
                    out_.WriteLine();
                    var hooks = new System.Text.Json.Nodes.JsonObject();
                    foreach (var eventName in ClaudeHookTranslator.Events)
                    {
                        hooks[eventName] = new System.Text.Json.Nodes.JsonArray(IntegrationInstaller.ClaudeHookGroup(eventName));
                    }

                    out_.WriteLine(hooks.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    return 0;

                default:
                    out_.WriteLine();
                    out_.WriteLine("usage: OverShell integrations status");
                    out_.WriteLine("       OverShell integrations install   <opencode|copilot|claude|codex|all>");
                    out_.WriteLine("       OverShell integrations uninstall <opencode|copilot|claude|codex|all>");
                    out_.WriteLine("       OverShell integrations show      <claude|codex>");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            out_.WriteLine();
            out_.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            out_.Flush();
        }
    }

    /// <summary><c>protocol status</c> · <c>protocol register</c> · <c>protocol unregister</c> — the <c>overshell://</c> URL scheme, per user.</summary>
    private static int RunProtocolCli(string[] args)
    {
        var out_ = OpenCliOutput();
        var exe = Environment.ProcessPath ?? string.Empty;

        try
        {
            switch (args.Length > 0 ? args[0].ToLowerInvariant() : "status")
            {
                case "status":
                    out_.WriteLine();
                    out_.WriteLine($"  overshell://  {(ProtocolRegistration.RegisteredCommand() is { } cmd ? cmd : "not registered")}");
                    out_.WriteLine($"  this build    {ProtocolRegistration.ExpectedCommand(exe)}  {(ProtocolRegistration.IsRegistered(exe) ? "(current)" : string.Empty)}");
                    return 0;

                case "register":
                    out_.WriteLine();
                    out_.WriteLine(ProtocolRegistration.Register(exe) ? $"  registered overshell:// -> {exe}" : "  already registered for this executable");
                    return 0;

                case "unregister":
                    out_.WriteLine();
                    out_.WriteLine(ProtocolRegistration.Unregister() ? "  removed overshell://" : "  overshell:// was not registered");
                    return 0;

                default:
                    out_.WriteLine();
                    out_.WriteLine("usage: OverShell protocol status|register|unregister");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            out_.WriteLine();
            out_.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            out_.Flush();
        }
    }

    /// <summary>
    /// <c>settings path</c> — where configuration and state live and why · <c>settings init</c> —
    /// write starter <c>settings.jsonc</c> / <c>keybindings.jsonc</c> from the built-in defaults
    /// (never over an existing file) · <c>settings open</c> — Explorer on the configuration root.
    /// </summary>
    private static int RunSettingsCli(string[] args)
    {
        var out_ = OpenCliOutput();

        try
        {
            switch (args.Length > 0 ? args[0].ToLowerInvariant() : "path")
            {
                case "path":
                    out_.WriteLine();
                    out_.WriteLine($"  configuration  {Core.AppPaths.ConfigRoot}");
                    out_.WriteLine($"                 from {Describe(Core.AppPaths.Config)}");
                    out_.WriteLine($"  state          {Core.AppPaths.StateRoot}");
                    out_.WriteLine($"                 from {Describe(Core.AppPaths.State)}");
                    out_.WriteLine();
                    foreach (var (name, path) in new[]
                    {
                        ("settings.jsonc", Core.AppPaths.SettingsFile), ("keybindings.jsonc", Core.AppPaths.KeybindingsFile),
                        ("snippets.jsonc", Core.AppPaths.SnippetsFile), ("agents\\", Core.AppPaths.AgentsDir),
                        ("layouts\\", Core.AppPaths.LayoutsDir), ("skins\\", Core.AppPaths.SkinsDir),
                        ("session.json", Core.AppPaths.SessionFile), ("state.json", Core.AppPaths.StateFile),
                    })
                    {
                        var exists = path.EndsWith('\\') ? Directory.Exists(path) : File.Exists(path);
                        out_.WriteLine($"  {name,-18} {(exists ? "present" : "absent ")}  {path}");
                    }

                    out_.WriteLine();
                    out_.WriteLine("  Order: OVERSHELL_CONFIG_DIR, then $XDG_CONFIG_HOME\\overshell, then %APPDATA%\\OverShell");
                    out_.WriteLine("         (state: OVERSHELL_STATE_DIR, $XDG_STATE_HOME\\overshell, %LOCALAPPDATA%\\OverShell).");
                    return 0;

                case "init":
                    out_.WriteLine();
                    Core.AppPaths.EnsureCreated();
                    foreach (var (path, resource) in new[] { (Core.AppPaths.SettingsFile, "settings.jsonc"), (Core.AppPaths.KeybindingsFile, "keybindings.jsonc") })
                    {
                        if (File.Exists(path))
                        {
                            out_.WriteLine($"  kept     {path}");
                            continue;
                        }

                        var header = $"// Written by `OverShell settings init` from the built-in defaults. Every value here equals the{Environment.NewLine}" +
                                     $"// default, so this file changes nothing until you edit it; delete a line to fall back.{Environment.NewLine}";
                        File.WriteAllText(path, header + Core.EmbeddedResources.Read(resource), new System.Text.UTF8Encoding(false));
                        out_.WriteLine($"  written  {path}");
                    }

                    out_.WriteLine("  Saved changes apply live while OverShell runs.");
                    return 0;

                case "open":
                    Core.AppPaths.EnsureCreated();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Core.AppPaths.ConfigRoot) { UseShellExecute = true });
                    return 0;

                default:
                    out_.WriteLine();
                    out_.WriteLine("usage: OverShell settings path|init|open");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            out_.WriteLine();
            out_.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            out_.Flush();
        }

        static string Describe(Core.ResolvedRoot root) => root.Source switch
        {
            Core.PathSource.Override => $"{root.Variable} (explicit override)",
            Core.PathSource.Xdg => $"{root.Variable} + \\overshell",
            _ => root.Variable == "APPDATA"
                ? "%APPDATA%\\OverShell (default; set XDG_CONFIG_HOME or OVERSHELL_CONFIG_DIR to move it)"
                : "%LOCALAPPDATA%\\OverShell (default; set XDG_STATE_HOME or OVERSHELL_STATE_DIR to move it)",
        };
    }

    private static void Print(TextWriter out_, IntegrationStatus s) =>
        out_.WriteLine($"  {s.Id,-9} {(s.Installed ? s.Current ? "installed  " : "outdated   " : "absent     ")} {s.Path}{(s.HarnessFound ? string.Empty : "   (harness not on PATH)")}{(s.Note is null ? string.Empty : $"{Environment.NewLine}            {s.Note}")}");

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Record("Dispatcher", e.Exception);

        // Keep running: one bad tab shouldn't take the whole shell down.
        e.Handled = true;

        if (_errorShown)
        {
            return;
        }

        _errorShown = true;

        MessageBox.Show(
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n"
          + $"Details written to:\n{CrashLogPath}\n\n"
          + "Further errors this session will be logged silently.",
            "OverShell",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void Record(string origin, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                CrashLogPath,
                $"[{DateTime.Now:O}] {origin}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing sensible to do if even logging fails.
        }
    }
}
