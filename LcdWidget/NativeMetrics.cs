using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LcdWidget;

// English PDH paths work on both Chinese and English Windows installations.
public sealed class PdhQuery : IDisposable
{
    private IntPtr query;
    private readonly Dictionary<string, IntPtr> counters = new();
    public string Error { get; private set; } = "";
    public PdhQuery(params (string Key, string Path)[] paths)
    {
        uint result = PdhOpenQuery(null, IntPtr.Zero, out query);
        if (result != 0) { Error = $"PDH 0x{result:X8}"; return; }
        foreach (var (key, path) in paths) {
            result = PdhAddEnglishCounter(query, path, IntPtr.Zero, out var counter);
            if (result == 0) counters[key] = counter;
            else Error += $" {key}:0x{result:X8}";
        }
    }
    public bool Collect() => query != IntPtr.Zero && PdhCollectQueryData(query) == 0;
    public Dictionary<string, double> Read(string key)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!counters.TryGetValue(key, out var counter)) return result;
        uint bytes = 0, count = 0;
        const uint format = 0x200 | 0x8000; // double, do not cap per-process values before engine aggregation
        if (PdhGetFormattedCounterArray(counter, format, ref bytes, ref count, IntPtr.Zero) != 0x800007D2 || bytes == 0) return result;
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try {
            if (PdhGetFormattedCounterArray(counter, format, ref bytes, ref count, buffer) != 0) return result;
            int stride = Marshal.SizeOf<PdhItem>();
            for (int i = 0; i < count; i++) {
                var item = Marshal.PtrToStructure<PdhItem>(buffer + i * stride);
                var name = Marshal.PtrToStringUni(item.Name);
                if (name != null && item.Status <= 1 && double.IsFinite(item.Value)) result[name] = item.Value;
            }
        } finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }
    public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PdhItem {
        [FieldOffset(0)] public IntPtr Name;
        [FieldOffset(8)] public uint Status;
        [FieldOffset(16)] public double Value;
    }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

public sealed class CpuSampler
{
    private ulong idle, kernel, user;
    private bool primed;
    public Reading Sample()
    {
        if (!GetSystemTimes(out ulong i, out ulong k, out ulong u)) { primed = false; return Reading.Missing("GetSystemTimes 失败", ReadingState.Failed); }
        ulong total = k + u;
        ulong previous = kernel + user;
        var result = primed && total > previous && i >= idle && i - idle <= total - previous
            ? Reading.Percent(100.0 * (1 - (double)(i - idle) / (total - previous)), "系统 CPU 时间差")
            : Reading.Missing("正在采样", ReadingState.WarmingUp);
        (idle, kernel, user, primed) = (i, k, u, true);
        return result;
    }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static Reading Memory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0
            ? Reading.Percent(100.0 * (1 - (double)status.AvailablePhysical / status.TotalPhysical), $"物理内存 {(status.TotalPhysical - status.AvailablePhysical) / 1073741824.0:F1} / {status.TotalPhysical / 1073741824.0:F1} GB")
            : Reading.Missing("内存读取失败", ReadingState.Failed);
    }
}

public static class GpuMath
{
    private static readonly Regex Pattern = new(@"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static Dictionary<string, double> Aggregate(Dictionary<string, double> counters)
    {
        var engines = new Dictionary<(string Adapter, string Engine), double>();
        foreach (var item in counters) {
            var match = Pattern.Match(item.Key);
            if (!match.Success || !double.IsFinite(item.Value)) continue;
            var key = ("luid_" + match.Groups[1].Value.ToLowerInvariant() + "_phys_" + match.Groups[2].Value, match.Groups[3].Value);
            engines[key] = engines.GetValueOrDefault(key) + Math.Max(0, item.Value);
        }
        return engines.GroupBy(x => x.Key.Adapter).ToDictionary(g => g.Key, g => Math.Clamp(g.Max(x => x.Value), 0, 100));
    }
}

public static class DxgiDevices
{
    // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1. Native vtable keeps this dependency-free.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapter(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr self, out AdapterDescription description);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Vendor, Device, SubSystem, Revision;
        public UIntPtr DedicatedVideo, DedicatedSystem, SharedSystem;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid guid, out IntPtr factory);
    public static List<Device> Enumerate()
    {
        var result = new List<Device>();
        var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref guid, out var factory) != 0) return result;
        try {
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapter>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            for (uint i = 0; enumerate(factory, i, out var adapter) == 0; i++) {
                try {
                    var describe = Marshal.GetDelegateForFunctionPointer<GetDescription>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    if (describe(adapter, out var d) == 0 && (d.Flags & 2) == 0) {
                        string pci = $"VEN_{d.Vendor:X4}&DEV_{d.Device:X4}&SUBSYS_{d.SubSystem:X8}";
                        result.Add(new Device($"luid_0x{unchecked((uint)d.LuidHigh):x8}_0x{d.LuidLow:x8}_phys_0", d.Description,
                            pci, pci + ":" + result.Count(x => x.Match == pci)));
                    }
                } finally { Marshal.Release(adapter); }
            }
        } finally { Marshal.Release(factory); }
        return result;
    }
}
