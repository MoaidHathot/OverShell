using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OverShell.App.Terminal.ConPty;

/// <summary>
/// A local process behind a Windows pseudoconsole, driven directly through the ConPTY
/// API. Owns the pipes, the console host handle, the child process and one I/O thread.
/// <para>
/// Modelled on Windows Terminal's <c>ConptyConnection</c>: the child gets a private
/// environment block (<c>WT_SESSION</c>, <c>WT_PROFILE_ID</c>, profile overrides), the
/// host's reference handle is released right after launch so it exits when the last
/// client leaves, and when the child exits the pseudoconsole is closed, the output pipe
/// drained to the end, and only then is the exit reported — so no trailing output is lost.
/// </para>
/// </summary>
public sealed unsafe class ConPtySession : ITerminalSession
{
    private const int ReadBufferSize = 16 * 1024;
    private const int DrainTimeoutMs = 5_000;
    private const int GracefulExitTimeoutMs = 2_000;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly Guid _sessionId = Guid.NewGuid();

    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _input;
    private nint _hpc;
    private SafeProcessHandle? _process;
    private ProcessWaitHandle? _processWait;
    private RegisteredWaitHandle? _exitWait;
    private Thread? _ioThread;

    private int _columns;
    private int _rows;
    private bool _startRequested;
    private volatile bool _started;
    private volatile bool _inputClosed;
    private volatile bool _exited;
    private volatile bool _disposed;
    private int _exitSignalled;
    private int? _exitCode;

    public ConPtySession(SessionDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public SessionDescriptor Descriptor { get; }

    public bool HasStarted => _started;

    public bool IsRunning => _started && !_exited;

    public int? ExitCode => _exitCode;

    public event EventHandler? Started;

    public event EventHandler? Exited;

    public event EventHandler<string>? OutputReceived;

    public void Start(int columns, int rows)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_startRequested)
            {
                throw new InvalidOperationException("The session has already been started.");
            }

            _startRequested = true;
            _columns = Math.Max(columns, 1);
            _rows = Math.Max(rows, 1);
        }

        // One dedicated thread per session: it launches, then blocks on the output pipe
        // for the lifetime of the child. Not a pool thread — reads block for minutes.
        _ioThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "OverShell ConPTY I/O",
        };
        _ioThread.Start();
    }

    public void WriteInput(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || _inputClosed || _disposed || !_started)
        {
            return;
        }

        var buffer = text.Length <= 512
            ? stackalloc byte[text.Length * 3]
            : new byte[Utf8.GetMaxByteCount(text.Length)];

        var count = Utf8.GetBytes(text, buffer);

        lock (_writeGate)
        {
            if (_input is null || _inputClosed)
            {
                return;
            }

            try
            {
                _input.Write(buffer[..count]);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The host went away under us; from here on input is simply dropped.
                _inputClosed = true;
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        if (columns < 1 || rows < 1)
        {
            return;
        }

        lock (_gate)
        {
            _columns = columns;
            _rows = rows;

            if (_hpc != 0)
            {
                _ = ConPtyNative.ResizePseudoConsole(_hpc, new ConPtyNative.Coord { X = (short)columns, Y = (short)rows });
            }
        }
    }

    /// <summary>
    /// Flag only. The input pipe stays open: closing it makes the console host tear the
    /// session down, and this call must be safe to make while the surface is still alive.
    /// </summary>
    public void CloseInput() => _inputClosed = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseInput();

        nint hpc;
        lock (_gate)
        {
            hpc = _hpc;
            _hpc = 0;
        }

        // Closing the pseudoconsole is the equivalent of closing a console window: the host
        // delivers CTRL_CLOSE_EVENT to the shell and exits once it has. This returns at once.
        if (hpc != 0)
        {
            ConPtyNative.ClosePseudoConsole(hpc);
        }

        _exitWait?.Unregister(null);
        _exitWait = null;

        // Everything that can block — waiting for the shell, draining the pipe — happens
        // off the caller's thread. If the app is exiting, the OS finishes the job.
        ThreadPool.QueueUserWorkItem(_ => FinishTeardown());
    }

    // ---------------------------------------------------------------- I/O thread

    private void Run()
    {
        try
        {
            Launch();
        }
        catch (Exception e)
        {
            Emit($"\r\n[error launching \"{Descriptor.CommandLine}\": {e.Message}]\r\n");
            MarkExited(null);
            return;
        }

        ReadToEnd();
        FinishAfterOutputDrained();
    }

    private void Launch()
    {
        int columns;
        int rows;
        lock (_gate)
        {
            columns = _columns;
            rows = _rows;
        }

        if (!ConPtyNative.CreatePipe(out var inputRead, out var inputWrite, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe (input)");
        }

        if (!ConPtyNative.CreatePipe(out var outputRead, out var outputWrite, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe (output)");
        }

        var size = new ConPtyNative.Coord { X = (short)Math.Min(columns, short.MaxValue), Y = (short)Math.Min(rows, short.MaxValue) };
        var hr = ConPtyNative.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hpc);
        if (hr < 0)
        {
            throw Marshal.GetExceptionForHR(hr) ?? new Win32Exception(hr, "CreatePseudoConsole");
        }

        // The host holds its own duplicates of the far ends; ours would only keep the pipes
        // from breaking when it exits.
        inputRead.Dispose();
        outputWrite.Dispose();

        _inputWrite = inputWrite;
        _outputRead = outputRead;

        SafeProcessHandle process;
        try
        {
            process = LaunchClient(hpc);
        }
        catch
        {
            ConPtyNative.ClosePseudoConsole(hpc);
            throw;
        }

        // Without this the host lingers after the shell exits for as long as we hold the
        // handle; with it, the output pipe breaks as soon as the last client detaches.
        try
        {
            _ = ConPtyNative.ReleasePseudoConsole(hpc);
        }
        catch (EntryPointNotFoundException)
        {
            // In-box kernel32 fallback on an older OS; the exit wait covers this case.
        }

        _process = process;
        _input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 0);

        lock (_gate)
        {
            _hpc = hpc;

            // A resize may have arrived while the host was starting.
            if (_columns != columns || _rows != rows)
            {
                _ = ConPtyNative.ResizePseudoConsole(hpc, new ConPtyNative.Coord { X = (short)_columns, Y = (short)_rows });
            }
        }

        _started = true;
        Started?.Invoke(this, EventArgs.Empty);

        _processWait = new ProcessWaitHandle(process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_processWait, OnClientExited, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private SafeProcessHandle LaunchClient(nint hpc)
    {
        nuint listSize = 0;
        _ = ConPtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref listSize);
        if (listSize == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList (size)");
        }

        var list = Marshal.AllocHGlobal((nint)listSize);
        var listInitialised = false;

        try
        {
            if (!ConPtyNative.InitializeProcThreadAttributeList(list, 1, 0, ref listSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList");
            }

            listInitialised = true;

            if (!ConPtyNative.UpdateProcThreadAttribute(list, 0, ConPtyNative.ProcThreadAttributePseudoConsole, hpc, (nuint)nint.Size, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute");
            }

            var startup = new ConPtyNative.StartupInfoExW
            {
                StartupInfo = { cb = (uint)sizeof(ConPtyNative.StartupInfoExW) },
                lpAttributeList = list,
            };

            // CreateProcessW may write into the command line buffer, so it needs its own copy.
            var commandLine = (Descriptor.CommandLine + '\0').ToCharArray();
            var environment = BuildEnvironmentBlock();
            var directory = string.IsNullOrWhiteSpace(Descriptor.WorkingDirectory) ? null : Descriptor.WorkingDirectory;

            ConPtyNative.ProcessInformation info;

            fixed (char* pCommandLine = commandLine)
            fixed (char* pEnvironment = environment)
            fixed (char* pDirectory = directory)
            {
                if (!ConPtyNative.CreateProcess(
                        null,
                        pCommandLine,
                        0,
                        0,
                        bInheritHandles: false,
                        ConPtyNative.ExtendedStartupInfoPresent | ConPtyNative.CreateUnicodeEnvironment,
                        pEnvironment,
                        pDirectory,
                        &startup,
                        &info))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            _ = ConPtyNative.CloseHandle(info.hThread);
            return new SafeProcessHandle(info.hProcess, ownsHandle: true);
        }
        finally
        {
            if (listInitialised)
            {
                ConPtyNative.DeleteProcThreadAttributeList(list);
            }

            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>
    /// OverShell's own environment plus what Windows Terminal would add, plus the
    /// descriptor's overrides. Sorted case-insensitively as <c>CreateProcess</c> requires.
    /// </summary>
    private string BuildEnvironmentBlock()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.Length > 0)
            {
                variables[key] = entry.Value as string ?? string.Empty;
            }
        }

        variables["WT_SESSION"] = _sessionId.ToString("D");

        if (!string.IsNullOrEmpty(Descriptor.ProfileId))
        {
            variables["WT_PROFILE_ID"] = Descriptor.ProfileId;
        }

        foreach (var (key, value) in Descriptor.Environment)
        {
            if (value is null)
            {
                variables.Remove(key);
            }
            else
            {
                variables[key] = value;
            }
        }

        var block = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private void ReadToEnd()
    {
        if (_outputRead is not { } handle)
        {
            return;
        }

        // Not disposed here: the handle is closed in FinishTeardown, after everything that
        // could still be holding it has stopped.
        var output = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        var decoder = Utf8.GetDecoder();
        var bytes = new byte[ReadBufferSize];
        var chars = new char[Utf8.GetMaxCharCount(ReadBufferSize)];

        while (true)
        {
            int read;
            try
            {
                read = output.Read(bytes, 0, bytes.Length);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                break;
            }

            // Zero is how a broken pipe reports itself: the host has exited.
            if (read == 0)
            {
                break;
            }

            var decoded = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            if (decoded > 0)
            {
                Emit(new string(chars, 0, decoded));
            }
        }
    }

    /// <summary>Runs on the I/O thread once the output pipe has broken.</summary>
    private void FinishAfterOutputDrained()
    {
        if (_disposed)
        {
            MarkExited(null);
            return;
        }

        int? code = null;
        if (_process is { IsInvalid: false } process)
        {
            // The pipe normally breaks a moment before the exit code is final.
            _ = ConPtyNative.WaitForSingleObject(process, DrainTimeoutMs);

            if (ConPtyNative.GetExitCodeProcess(process, out var raw))
            {
                if (raw == ConPtyNative.StillActive)
                {
                    // The host died but the shell did not: a client without a console is of no
                    // use to anyone. Match what closing a console window would have done.
                    _ = ConPtyNative.TerminateProcess(process, 1);
                }
                else
                {
                    code = unchecked((int)raw);
                }
            }
        }

        Emit(code is { } c
            ? $"\r\n[process exited with code {c}]\r\n"
            : "\r\n[session ended]\r\n");

        MarkExited(code);
    }

    /// <summary>
    /// Thread-pool callback when the shell exits. Closing the pseudoconsole here makes the
    /// output pipe break even when background children still hold the console open, which
    /// lets the I/O thread drain and report the exit in order.
    /// </summary>
    private void OnClientExited(object? state, bool timedOut)
    {
        if (_disposed)
        {
            return;
        }

        nint hpc;
        lock (_gate)
        {
            hpc = _hpc;
            _hpc = 0;
        }

        if (hpc != 0)
        {
            ConPtyNative.ClosePseudoConsole(hpc);
        }

        // If the host does not let go, do not leave the tab looking alive forever.
        if (_ioThread is { } io && !io.Join(DrainTimeoutMs))
        {
            int? code = null;
            if (_process is { IsInvalid: false } process && ConPtyNative.GetExitCodeProcess(process, out var raw) && raw != ConPtyNative.StillActive)
            {
                code = unchecked((int)raw);
            }

            MarkExited(code);
        }
    }

    private void MarkExited(int? code)
    {
        if (Interlocked.Exchange(ref _exitSignalled, 1) != 0)
        {
            return;
        }

        _exitCode = code;
        _exited = true;
        _inputClosed = true;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private void Emit(string chunk)
    {
        if (chunk.Length > 0)
        {
            OutputReceived?.Invoke(this, chunk);
        }
    }

    // ------------------------------------------------------------------ teardown

    private void FinishTeardown()
    {
        if (_process is { IsInvalid: false } process &&
            ConPtyNative.WaitForSingleObject(process, GracefulExitTimeoutMs) != ConPtyNative.WaitObject0)
        {
            _ = ConPtyNative.TerminateProcess(process, 1);
        }

        _ioThread?.Join(DrainTimeoutMs);
        MarkExited(_exitCode);

        lock (_writeGate)
        {
            _input?.Dispose();
            _input = null;
        }

        _inputWrite?.Dispose();
        _outputRead?.Dispose();
        _processWait?.Dispose();
        _process?.Dispose();
    }

    /// <summary>Lets the thread pool wait on a process handle without a <c>System.Diagnostics.Process</c>.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            // Not owned: the session keeps the process handle alive for longer than this.
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
