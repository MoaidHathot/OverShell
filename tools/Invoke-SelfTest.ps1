<#
.SYNOPSIS
Runs one OverShell self-test mode (OVERSHELL_SELFTEST=<mode>) without taking the desktop away from you.

.DESCRIPTION
By default the test instance runs on a private Win32 desktop (CreateDesktop + CreateProcess with
lpDesktop): nothing appears on the desktop you are working on, no window takes your focus, a tiling
window manager never sees the windows, and the instance's own hotkeys cannot clash with yours. Inside
that desktop the app's windows activate and focus among themselves exactly as on screen, so the
checks are real - except the ones that need the screen itself (the acrylic reads and the PNG
screenshots of the `transparency` and `find` modes), which the self-test marks SKIP there. Run with
-Visible for those, when you can spare the desktop for a minute.

The script waits for the run to finish (the self-test's "=== selftest end ===" line, or the process
leaving), prints the tally and every FAIL and SKIP line, then closes the window (WM_CLOSE on the
instance's desktop) and reports leftovers. Exit code 0 when nothing failed.

.EXAMPLE
tools\Invoke-SelfTest.ps1                      # the 86-check run, hidden
tools\Invoke-SelfTest.ps1 -Mode transparency -Visible
tools\Invoke-SelfTest.ps1 -Mode session1 -End leave; tools\Invoke-SelfTest.ps1 -Mode session2 -KeepState -TearOffTitles PowerShell
#>
[CmdletBinding()]
param(
    [string] $Mode = '1',
    [switch] $Visible,
    [ValidateSet('close', 'kill', 'leave')] [string] $End = 'close',
    [int] $MaxSeconds = 300,
    [string] $Exe = (Join-Path $PSScriptRoot '..\artifacts\bin\OverShell.App\debug_win-x64\OverShell.exe'),
    [string] $ConfigDir = (Join-Path ([IO.Path]::GetTempPath()) 'overshell-selftest-config'),
    [string] $StateDir = (Join-Path ([IO.Path]::GetTempPath()) 'overshell-selftest-state'),
    [switch] $KeepState,
    [hashtable] $Env = @{},
    [string[]] $Arguments = @(),
    [string[]] $TearOffTitles = @(),
    [string] $Desktop = 'overshell-selftest',
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'

# Window enumeration, messaging and process creation are desktop-bound, so the private-desktop
# work is a few lines of Win32 rather than cmdlets.
if (-not ('OverShellSelfTest.Desktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OverShellSelfTest
{
    public static class Desktop
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public int cb;
            public IntPtr lpReserved;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDesktop;
            public IntPtr lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr security);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenDesktopW(string name, int flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        private const uint GENERIC_ALL = 0x10000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint WM_CLOSE = 0x0010;

        /// <summary>Creates the desktop, or opens it when a previous run left it; the handle keeps it alive while no window is on it.</summary>
        public static IntPtr Create(string name)
        {
            var handle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
            if (handle == IntPtr.Zero) { throw new InvalidOperationException("CreateDesktop failed: " + Marshal.GetLastWin32Error()); }
            return handle;
        }

        public static void Close(IntPtr desktop) { if (desktop != IntPtr.Zero) { CloseDesktop(desktop); } }

        /// <summary>Starts exe on the named desktop (null: ours) with env overlaid on our environment; returns the pid.</summary>
        public static int Start(string desktopName, string exe, string[] args, IDictionary env)
        {
            var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) { merged[(string)e.Key] = (string)e.Value; }
            if (env != null) { foreach (DictionaryEntry e in env) { merged[(string)e.Key] = Convert.ToString(e.Value); } }
            var block = new StringBuilder();
            foreach (var kv in merged) { block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0'); }
            block.Append('\0');
            var envPtr = Marshal.StringToHGlobalUni(block.ToString());
            try
            {
                var cmd = new StringBuilder().Append('"').Append(exe).Append('"');
                foreach (var a in args ?? new string[0]) { cmd.Append(' ').Append(a.IndexOfAny(new[] { ' ', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""); }
                var si = new STARTUPINFOW { cb = Marshal.SizeOf(typeof(STARTUPINFOW)), lpDesktop = desktopName };
                PROCESS_INFORMATION pi;
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, envPtr, null, ref si, out pi))
                {
                    throw new InvalidOperationException("CreateProcess failed: " + Marshal.GetLastWin32Error());
                }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally { Marshal.FreeHGlobal(envPtr); }
        }

        /// <summary>Visible top-level windows of the process on the desktop (null: ours), "hwnd|title" per line.</summary>
        public static string[] Windows(string desktopName, int pid)
        {
            var lines = new List<string>();
            Enumerate(desktopName, hwnd =>
            {
                uint owner; GetWindowThreadProcessId(hwnd, out owner);
                if (owner != (uint)pid || !IsWindowVisible(hwnd)) { return; }
                var sb = new StringBuilder(256); GetWindowTextW(hwnd, sb, 256);
                lines.Add(string.Format("0x{0:X}|{1}", hwnd.ToInt64(), sb));
            });
            return lines.ToArray();
        }

        /// <summary>
        /// Posts WM_CLOSE to the process's visible OverShell windows, the tear-offs last resort only (a
        /// tear-off's close re-attaches its tab instead of ending the session); returns how many were asked.
        /// </summary>
        public static int CloseWindows(string desktopName, int pid, string[] tearOffTitles)
        {
            var targets = new List<IntPtr>();
            var all = new List<IntPtr>();
            Enumerate(desktopName, hwnd =>
            {
                uint owner; GetWindowThreadProcessId(hwnd, out owner);
                if (owner != (uint)pid || !IsWindowVisible(hwnd)) { return; }
                var sb = new StringBuilder(256); GetWindowTextW(hwnd, sb, 256);
                var title = sb.ToString();
                if (title.IndexOf("OverShell", StringComparison.Ordinal) < 0) { return; }
                all.Add(hwnd);
                foreach (var t in tearOffTitles ?? new string[0]) { if (title.StartsWith(t, StringComparison.Ordinal)) { return; } }
                targets.Add(hwnd);
            });
            // The main window's title can start like a tear-off's (both carry the active tab's
            // title): when the filter leaves nothing, close them all - the main window's close
            // ends the process, the tear-off's merely re-attaches first.
            if (targets.Count == 0) { targets = all; }
            var count = 0;
            foreach (var hwnd in targets) { if (PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) { count++; } }
            return count;
        }

        // Enumeration sees the calling thread's desktop, so another desktop is walked from a
        // throwaway thread attached to it; the handles found are usable from any thread.
        private static void Enumerate(string desktopName, Action<IntPtr> visit)
        {
            if (desktopName == null)
            {
                EnumWindows((hwnd, _) => { visit(hwnd); return true; }, IntPtr.Zero);
                return;
            }

            Exception failure = null;
            var thread = new Thread(() =>
            {
                var desktop = OpenDesktopW(desktopName, 0, false, GENERIC_ALL);
                if (desktop == IntPtr.Zero) { failure = new InvalidOperationException("OpenDesktop failed: " + Marshal.GetLastWin32Error()); return; }
                try
                {
                    if (!SetThreadDesktop(desktop)) { failure = new InvalidOperationException("SetThreadDesktop failed: " + Marshal.GetLastWin32Error()); return; }
                    EnumDesktopWindows(desktop, (hwnd, _) => { visit(hwnd); return true; }, IntPtr.Zero);
                }
                catch (Exception e) { failure = e; }
                finally { CloseDesktop(desktop); }
            });
            thread.Start();
            thread.Join();
            if (failure != null) { throw failure; }
        }
    }
}
'@
}

function Say([string] $text) { if (-not $Quiet) { Write-Output $text } }
function Ascii([string] $text) { $text -replace '[^\x20-\x7E]', '?' }

$Exe = [IO.Path]::GetFullPath($Exe)
if (-not (Test-Path -LiteralPath $Exe)) { throw "No OverShell.exe at $Exe - build first (dotnet build OverShell.slnx -c Debug -p:Platform=x64)" }
$temp = [IO.Path]::GetTempPath()
$log = Join-Path $temp 'overshell-selftest.log'
$agentsLog = Join-Path $temp 'overshell-agents.log'
$crashLog = Join-Path $temp 'overshell-crash.log'
Remove-Item -LiteralPath $log, $agentsLog, $crashLog -Force -ErrorAction SilentlyContinue
if (-not $KeepState) { Remove-Item -LiteralPath $StateDir -Recurse -Force -ErrorAction SilentlyContinue }

$environment = @{
    OVERSHELL_SELFTEST     = $Mode
    OVERSHELL_CONFIG_DIR   = $ConfigDir
    OVERSHELL_STATE_DIR    = $StateDir
    OVERSHELL_WT_SETTINGS  = (Join-Path $temp 'overshell-wt-scratch.json')
    OVERSHELL_TRACE_AGENTS = '1'
}
foreach ($key in $Env.Keys) { $environment[$key] = [string]$Env[$key] }

# What a mode needs from its launcher rather than from the app (the self-test checks it first):
# `env` proves the tab's environment comes from the registry, not from us, so we start the app
# with the user's registry PATH entries removed and a variable only this process has.
if ($Mode -eq 'env') {
    $userPath = [string][Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Environment', 'Path', '')
    $userEntries = @($userPath -split ';' | Where-Object { $_ } | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') })
    $environment['PATH'] = @($env:PATH -split ';' | Where-Object { $_ -and ($userEntries -notcontains $_.TrimEnd('\')) }) -join ';'
    $environment['OVERSHELL_SELFTEST_STALE'] = 'launcher'
}

$desktopName = if ($Visible) { $null } else { $Desktop }
$desktopHandle = [IntPtr]::Zero
if (-not $Visible) { $desktopHandle = [OverShellSelfTest.Desktop]::Create($Desktop) }
$started = Get-Date
$clock = [Diagnostics.Stopwatch]::StartNew()
$childPid = [OverShellSelfTest.Desktop]::Start($desktopName, $Exe, [string[]]$Arguments, $environment)
Say "selftest '$Mode': pid=$childPid on $(if ($Visible) { 'the input desktop' } else { "private desktop '$Desktop'" })"

function Alive([int] $id) { try { -not ([Diagnostics.Process]::GetProcessById($id)).HasExited } catch { $false } }

$finished = $false
while ($clock.Elapsed.TotalSeconds -lt $MaxSeconds) {
    Start-Sleep -Seconds 1
    if (-not (Alive $childPid)) { $finished = $true; break }
    if ((Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -Pattern '=== selftest end ===' -Quiet)) { $finished = $true; break }
}
$alive = Alive $childPid
Say "after $([int]$clock.Elapsed.TotalSeconds)s: $(if ($finished) { 'finished' } else { 'TIMEOUT' }), process $(if ($alive) { 'running' } else { 'exited' })"

$lines = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log } else { @() }
$tally = $lines | Select-String -Pattern '=== selftest tally: ' | Select-Object -Last 1
$failed = @($lines | Where-Object { $_ -match '^\S+\s+FAIL\s' -or $_ -match 'SELFTEST FAILED' })
$skipped = @($lines | Where-Object { $_ -match '^\S+\s+SKIP\s' })
$results = @($lines | Where-Object { $_ -match '=== selftest( \([^)]+\))? result: ' })
foreach ($r in $results) { Say ('  ' + (Ascii $r)) }
if ($tally) { Say ('  ' + (Ascii $tally.Line)) } else { Say "  (no tally line - $($lines.Count) log lines)" }
foreach ($f in $failed) { Write-Output ('  ' + (Ascii $f)) }
foreach ($s in $skipped) { Say ('  ' + (Ascii $s)) }

if ($alive) {
    switch ($End) {
        'close' {
            $asked = [OverShellSelfTest.Desktop]::CloseWindows($desktopName, $childPid, [string[]]$TearOffTitles)
            $process = [Diagnostics.Process]::GetProcessById($childPid)
            if (-not $process.WaitForExit(15000)) {
                Write-Output "  WARN: still running 15 s after WM_CLOSE (asked $asked windows); terminating"
                $process.Kill()
            }
            Say "  closed (asked $asked windows)"
        }
        'kill' { $process = [Diagnostics.Process]::GetProcessById($childPid); $process.Kill(); $process.WaitForExit(5000) | Out-Null; Say '  killed' }
        'leave' { Say "  left running: pid=$childPid" }
    }
}
if ($desktopHandle -ne [IntPtr]::Zero) { [OverShellSelfTest.Desktop]::Close($desktopHandle) }

if (Test-Path -LiteralPath $crashLog) {
    Write-Output '  --- CRASH LOG ---'
    Get-Content -LiteralPath $crashLog | Select-Object -First 20 | ForEach-Object { Write-Output ('  ' + (Ascii $_)) }
}
if ($End -ne 'leave') {
    $leftovers = @(Get-CimInstance Win32_Process -Filter "name='OverShell.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CreationDate -gt $started })
    if ($leftovers.Count -gt 0) { Write-Output "  WARN: $($leftovers.Count) OverShell process(es) started by this run still alive: $($leftovers.ProcessId -join ', ')" }
}

# A mode that ends by closing the window itself (session1, resilience) leaves no tally line;
# its result lines are the verdict then.
$verdict = if ($tally) { $tally.Line -match ' fail=0 ' } else { $results.Count -gt 0 -and @($results | Where-Object { $_ -match 'FAILED' }).Count -eq 0 }
$ok = $finished -and $failed.Count -eq 0 -and $verdict
exit $(if ($ok) { 0 } else { 1 })
