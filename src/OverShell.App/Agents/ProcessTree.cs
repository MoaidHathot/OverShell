using System.Runtime.InteropServices;

namespace OverShell.App.Agents;

/// <summary>
/// Answers "what is running inside this tab?" by walking the process tree below the
/// shell's root process. One <c>CreateToolhelp32Snapshot</c> per probe — a couple of
/// milliseconds for a whole machine — rather than <see cref="System.Diagnostics.Process"/>
/// objects for every process on the box.
/// </summary>
internal static partial class ProcessTree
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const int MaxPath = 260;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string szExeFile;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>
    /// Image names (without extension, as they appear) of every process below
    /// <paramref name="rootPid"/>, nearest first. Empty when the root has no children or
    /// the snapshot failed. Safe to call from any thread.
    /// </summary>
    public static IReadOnlyList<string> Descendants(int rootPid)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == 0 || snapshot == -1)
        {
            return [];
        }

        try
        {
            var children = new Dictionary<uint, List<(uint Pid, string Image)>>();
            var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };

            if (!Process32FirstW(snapshot, ref entry))
            {
                return [];
            }

            do
            {
                if (!children.TryGetValue(entry.th32ParentProcessID, out var list))
                {
                    children[entry.th32ParentProcessID] = list = [];
                }

                list.Add((entry.th32ProcessID, entry.szExeFile));
            }
            while (Process32NextW(snapshot, ref entry));

            // Breadth-first so the shell's direct child comes before its grandchildren; a
            // visited set guards against pid reuse producing a cycle in a stale snapshot.
            var result = new List<string>();
            var queue = new Queue<uint>();
            var visited = new HashSet<uint> { (uint)rootPid };
            queue.Enqueue((uint)rootPid);

            while (queue.Count > 0 && result.Count < 64)
            {
                var pid = queue.Dequeue();
                if (!children.TryGetValue(pid, out var kids))
                {
                    continue;
                }

                foreach (var (childPid, image) in kids)
                {
                    if (!visited.Add(childPid))
                    {
                        continue;
                    }

                    var name = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
                    result.Add(name);
                    queue.Enqueue(childPid);
                }
            }

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
