using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace T3.Voice;

public sealed record UsageSnapshot(long WorkingSetBytes, int ProcessCount)
{
    public double Megabytes => WorkingSetBytes / 1024d / 1024d;
    public bool OverBudget => WorkingSetBytes > 500L * 1024 * 1024;
}

public sealed class ResourceUsage
{
    private readonly object gate = new();
    private readonly HashSet<int> children = [];
    public void ProviderEvent(ProviderEvent message)
    {
        if (!int.TryParse(message.Text, out var id)) return;
        lock (gate)
        {
            if (message.Kind == "process") children.Add(id);
            if (message.Kind == "process-exit") children.Remove(id);
        }
    }
    public UsageSnapshot Read()
    {
        HashSet<int> selected;
        lock (gate) selected = children.ToHashSet();
        selected.Add(Environment.ProcessId);
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot != new IntPtr(-1))
        {
            try
            {
                var entries = new List<(int Id, int Parent)>();
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
                if (Process32First(snapshot, ref entry))
                    do { entries.Add(((int)entry.ProcessId, (int)entry.ParentProcessId)); } while (Process32Next(snapshot, ref entry));
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var candidate in entries)
                        if (selected.Contains(candidate.Parent) && selected.Add(candidate.Id)) changed = true;
                }
            }
            finally { CloseHandle(snapshot); }
        }
        long bytes = 0;
        var count = 0;
        foreach (var id in selected)
        {
            try { using var process = Process.GetProcessById(id); bytes += process.WorkingSet64; count++; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return new UsageSnapshot(bytes, count);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
}
