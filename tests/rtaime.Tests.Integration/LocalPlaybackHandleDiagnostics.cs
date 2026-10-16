// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace rtaime.Tests.Integration;

// Test-only, bounded metadata query. Never closes, duplicates or queries names of
// observed handles. Handles may disappear between snapshot and type lookup.
internal static class LocalPlaybackHandleDiagnostics
{
    internal static object Capture()
    {
        using var process = Process.GetCurrentProcess();
        return new {
            processThreads = process.Threads.Count,
            managedPoolThreads = ThreadPool.ThreadCount,
            finalizationPending = GC.GetGCMemoryInfo().FinalizationPendingCount,
            handleTypes = ReadTypes()
        };
    }

    private static object ReadTypes()
    {
        if (!OperatingSystem.IsWindows()) return new { error = "Windows required" };
        const int maximumBytes = 4 * 1024 * 1024;
        var bytes = 64 * 1024;
        while (bytes <= maximumBytes)
        {
            var snapshot = Marshal.AllocHGlobal(bytes);
            try
            {
                var status = NtQueryInformationProcess(new IntPtr(-1), 51, snapshot, bytes, out var required);
                if (status == unchecked((int)0xC0000004) || status == unchecked((int)0xC0000023))
                {
                    bytes = checked(Math.Max(bytes * 2, required));
                    continue;
                }
                if (status < 0) return new { error = $"NtQueryInformationProcess: 0x{status:X8}" };
                var header = IntPtr.Size * 2;
                var stride = IntPtr.Size * 3 + 16;
                var count = Marshal.ReadIntPtr(snapshot).ToInt64();
                if (count < 0 || count > (bytes - header) / stride)
                    return new { error = "Invalid bounded handle snapshot extent" };
                var totals = new Dictionary<uint, int>();
                var names = new Dictionary<uint, string>();
                var typeBuffer = Marshal.AllocHGlobal(4096);
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        var entry = IntPtr.Add(snapshot, checked(header + (int)index * stride));
                        var type = unchecked((uint)Marshal.ReadInt32(entry, IntPtr.Size * 3 + 4));
                        if (!totals.ContainsKey(type) && totals.Count >= 32) continue;
                        totals[type] = totals.GetValueOrDefault(type) + 1;
                        if (names.ContainsKey(type)) continue;
                        var handle = Marshal.ReadIntPtr(entry);
                        if (NtQueryObject(handle, 2, typeBuffer, 4096, out _) < 0) continue;
                        var length = (ushort)Marshal.ReadInt16(typeBuffer);
                        var pointer = Marshal.ReadIntPtr(typeBuffer, IntPtr.Size);
                        var offset = pointer.ToInt64() - typeBuffer.ToInt64();
                        if (length <= 128 && offset >= 0 && offset + length <= 4096)
                            names[type] = Marshal.PtrToStringUni(pointer, length / 2) ?? "Unknown";
                    }
                    return totals.OrderBy(pair => pair.Key).Select(pair => new {
                        typeIndex = pair.Key, name = names.GetValueOrDefault(pair.Key, "Unknown"), count = pair.Value
                    }).ToArray();
                }
                finally { Marshal.FreeHGlobal(typeBuffer); }
            }
            finally { Marshal.FreeHGlobal(snapshot); }
        }
        return new { error = "Handle snapshot exceeds 4 MiB diagnostic bound" };
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
