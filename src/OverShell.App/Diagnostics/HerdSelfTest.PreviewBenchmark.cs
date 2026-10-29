using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OverShell.Core;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static string? _previewBenchSettings;
    private static string? _previewBenchWt;

    private static void SeedPreviewBenchmark()
    {
        _previewBenchSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;
        _previewBenchWt = Environment.GetEnvironmentVariable("OVERSHELL_WT_SETTINGS");
        var fixture = Path.Combine(AppPaths.StateRoot, "preview-terminal.json");
        File.WriteAllText(fixture, """
            { "defaultProfile": "Preview fixture", "profiles": { "list": [
              { "guid": "410d8f09-b412-42dc-a1cc-70f7be758cc2", "name": "Preview fixture", "commandline": "powershell.exe -NoLogo -NoProfile", "startingDirectory": "%USERPROFILE%" }
            ] } }
            """);
        Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", fixture);
        File.WriteAllText(AppPaths.SettingsFile, Jsonc.Serialize(new
        {
            protocol = new { register = false },
            session = new { restore = false, confirmCloseWithAgents = false },
            detection = new { injectShellIntegration = false, cwdFromProcess = false },
            extensions = new Dictionary<string, object> { ["summon"] = new { enabled = false } },
            window = new { terminalOpacity = Environment.GetEnvironmentVariable("OVERSHELL_PREVIEW_OPAQUE") == "1" ? 1.0 : 0.85 },
            dashboard = new { previewMode = Environment.GetEnvironmentVariable("OVERSHELL_PREVIEW_MODE") ?? "auto", hoverPreview = Mode != "previewbench" },
        }));
    }

    private static async Task RunPreviewBenchmarkAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (previewbench) start ===");
        var count = int.TryParse(Environment.GetEnvironmentVariable("OVERSHELL_PREVIEW_TABS"), out var requested) ? Math.Clamp(requested, 2, 32) : 8;
        var stage = Environment.GetEnvironmentVariable("OVERSHELL_PREVIEW_STAGE") ?? "baseline";
        var tabs = new List<TerminalTab> { first };
        var phases = new List<PreviewBenchmarkSample>();
        var snapshots = 0;
        using var gpu = new PreviewGpuCounters(Environment.ProcessId);
        try
        {
            window.Width = 1180;
            window.Height = 760;
            for (var i = 1; i < count; i++) tabs.Add(window.AddTab(first.Profile, activate: false));
            if (!await TransferUntilAsync(() => tabs.All(t => t.IsRunning && t.TerminalHwnd != 0))) throw new InvalidOperationException("Benchmark shells did not start");
            await Task.Delay(1500);
            foreach (var tab in tabs)
            {
                tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TerminalTab.ScreenRows)) snapshots++; };
                tab.SendText(PreviewFixtureCommand(frames: 1));
            }
            await Task.Delay(1200);
            var grids = tabs.Select(t => t.Grid).ToArray();
            phases.Add(await Sample("terminal-idle", 5));
            window.Commands.TryExecute("view.dashboard");
            await Task.Delay(1200);
            phases.Add(await Sample("dashboard-idle", 5));
            foreach (var tab in tabs) tab.SendText(PreviewFixtureCommand(frames: 220));
            await Task.Delay(600);
            phases.Add(await Sample("dashboard-busy", 8));
            window.Commands.TryExecute("view.terminal");
            await Task.Delay(400);
            phases.Add(await Sample("hidden-dashboard-busy", 8));
            var sameGrids = tabs.Select(t => t.Grid).SequenceEqual(grids);
            Log($"  {(sameGrids ? "PASS" : "FAIL")}  dashboard transitions preserve all {tabs.Count} source grids");
            var report = new
            {
                stage, version = App.InformationalVersion, tabs = count, desktop = OnInputDesktop ? "input" : "private",
                composed = Terminal.TerminalFactory.Composition.Enabled, mode = Environment.GetEnvironmentVariable("OVERSHELL_PREVIEW_MODE") ?? "auto",
                nativeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Microsoft.Terminal.Control.dll")))),
                liveLeases = Terminal.WindowsTerminal.NativeTerminalPreview.ActiveLeases,
                styledCaptures = Terminal.WindowsTerminal.NativeTerminalPreview.SnapshotCount,
                geometryUpdates = Terminal.WindowsTerminal.NativeTerminalPreview.GeometryUpdates,
                processorCount = Environment.ProcessorCount, grid = new { columns = grids[0].Columns, rows = grids[0].Rows }, sameGrids, phases,
            };
            var path = Path.Combine(AppPaths.StateRoot, "preview-benchmark.json");
            File.WriteAllText(path, Jsonc.Serialize(report));
            Log($"  benchmark report: {path}");
        }
        finally
        {
            foreach (var tab in tabs.Skip(1).ToArray()) if (window.Tabs.Contains(tab)) window.CloseTab(tab);
            if (_previewBenchSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, _previewBenchSettings);
            Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", _previewBenchWt);
        }
        Log("=== selftest (previewbench) result: completed ===");

        async Task<PreviewBenchmarkSample> Sample(string phase, int seconds)
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocations = GC.GetTotalAllocatedBytes();
            var reads = snapshots;
            var nativeSnapshots = Terminal.WindowsTerminal.NativeTerminalPreview.SnapshotCount;
            var drawings = Chrome.StyledPreviewSource.CompletedDrawings;
            var gpuValues = new List<double>();
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < seconds; i++)
            {
                await Task.Delay(1000);
                if (await Task.Run(gpu.Sample) is { } value) gpuValues.Add(value);
            }
            process.Refresh();
            var elapsed = clock.Elapsed.TotalMilliseconds;
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var sample = new PreviewBenchmarkSample(phase, elapsed, cpuMs, 100 * cpuMs / elapsed,
                GC.GetTotalAllocatedBytes() - allocations, process.PrivateMemorySize64, process.WorkingSet64,
                process.HandleCount, GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1), snapshots - reads,
                gpuValues.Count == 0 ? null : gpuValues.Average(), Terminal.WindowsTerminal.NativeTerminalPreview.ActiveLeases,
                Chrome.StyledPreviewSource.ActiveSources, Terminal.WindowsTerminal.NativeTerminalPreview.SnapshotCount - nativeSnapshots,
                Chrome.StyledPreviewSource.CompletedDrawings - drawings);
            Log($"  MEASURE {phase}: cpu={sample.OneCorePercent:F2}% allocated={sample.AllocatedBytes / 1024d:F0} KiB private={sample.PrivateBytes / 1048576d:F1} MiB handles={sample.Handles} snapshots={sample.Snapshots} gpu={(sample.GpuEnginePercent is { } g ? g.ToString("F2") + "%" : "unavailable")}");
            return sample;
        }
    }

    private static string PreviewFixtureCommand(int frames) =>
        "& { $e=[string][char]27; for($i=0;$i -lt " + frames + ";$i++){ [Console]::Write($e+'[2J'+$e+'[H'+$e+'[38;2;255;80;80mRED '+$e+'[38;2;80;220;120mGREEN '+$e+'[38;2;80;160;255mBLUE'+$e+'[0m'+\"`r`n\"+" +
        "$e+'[1mBold'+$e+'[22m '+$e+'[3mItalic'+$e+'[23m '+$e+'[4mUnderline'+$e+'[24m'+\"`r`n\"+" +
        "$e+'[48;2;30;80;140m BACKGROUND '+$e+'[0m'+\"`r`n\"+'Unicode: '+[char]0x250c+[char]0x2500+[char]0x2510+' '+[char]0x4e2d+[char]0x6587+\"`r`n\"+'Frame '+$i+\"`r`n\"); Start-Sleep -Milliseconds 100 } }\r";

    private sealed record PreviewBenchmarkSample(string Phase, double WallMilliseconds, double CpuMilliseconds, double OneCorePercent,
        long AllocatedBytes, long PrivateBytes, long WorkingSetBytes, int Handles, uint GdiObjects, uint UserObjects, int Snapshots, double? GpuEnginePercent,
        int LivePreviews, int StyledSources, long StyledCaptures, long StyledDrawings);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    /// <summary>Optional Windows GPU-engine counters. Failure is evidence of unavailability, never a zero measurement.</summary>
    private sealed class PreviewGpuCounters : IDisposable
    {
        private nint _query;
        private nint _counter;
        private readonly string _pid;

        public PreviewGpuCounters(int pid)
        {
            _pid = $"pid_{pid}_";
            if (PdhOpenQueryW(null, 0, out _query) != 0 || PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter) != 0)
                Dispose();
            if (_query != 0) _ = PdhCollectQueryData(_query);
        }

        public double? Sample()
        {
            if (_query == 0 || PdhCollectQueryData(_query) != 0) return null;
            uint size = 0, count = 0;
            const uint format = 0x200 | 0x8000; // double, uncapped
            _ = PdhGetFormattedCounterArrayW(_counter, format, ref size, ref count, 0);
            if (size == 0 || size > 16 * 1024 * 1024) return null;
            var memory = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, format, ref size, ref count, memory) != 0) return null;
                double? total = null;
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<GpuCounterItem>(memory + i * Marshal.SizeOf<GpuCounterItem>());
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (item.Status <= 1 && name?.Contains(_pid, StringComparison.OrdinalIgnoreCase) == true && double.IsFinite(item.Value))
                        total = (total ?? 0) + item.Value;
                }
                return total;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        public void Dispose() { if (_query != 0) _ = PdhCloseQuery(_query); _query = 0; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct GpuCounterItem
        {
            [FieldOffset(0)] public nint Name;
            [FieldOffset(8)] public uint Status;
            [FieldOffset(16)] public double Value;
        }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, nuint data, out nint query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint data, out nint counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, ref uint count, nint items);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    }
}
