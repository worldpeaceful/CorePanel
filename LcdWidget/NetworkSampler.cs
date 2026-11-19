using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace LcdWidget;

public sealed record NetworkDevice(string Id, string Name, bool Physical, bool Connected, bool DefaultRoute,
    ulong ReceiveLinkSpeed, ulong TransmitLinkSpeed, Reading Download, Reading Upload)
{
    public string Kind => Physical ? "物理" : "虚拟 / 隧道";
    public string Connection => Connected ? "已连接" : "未连接";
    public string Link => $"↓ {ReceiveLinkSpeed / 1_000_000d:0.#} / ↑ {TransmitLinkSpeed / 1_000_000d:0.#} Mbps";
    public string DownText => Metrics.Format(Metrics.ForDisplay(new("", "", "B/s", Download))) + " " + Metrics.ForDisplay(new("", "", "B/s", Download)).Unit;
    public string UpText => Metrics.Format(Metrics.ForDisplay(new("", "", "B/s", Upload))) + " " + Metrics.ForDisplay(new("", "", "B/s", Upload)).Unit;
}

public sealed class NetworkSampler : IDisposable
{
    // Windows x64 SDK layouts: MIB_IF_ROW2 (1352 bytes), MIB_IPFORWARD_ROW2 (104 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 1352, CharSet = CharSet.Unicode)]
    private struct NativeIfRow {
        [FieldOffset(0)] public ulong Luid;
        [FieldOffset(8)] public uint Index;
        [FieldOffset(12)] public Guid Id;
        [FieldOffset(1128)] public uint Type;
        [FieldOffset(1152)] public byte Flags;
        [FieldOffset(1156)] public uint OperStatus;
        [FieldOffset(1192)] public ulong TxSpeed;
        [FieldOffset(1200)] public ulong RxSpeed;
        [FieldOffset(1208)] public ulong Rx;
        [FieldOffset(1280)] public ulong Tx;
    }
    internal sealed record IfRow(Guid Id, uint Index, string Alias, uint Type, byte Flags, uint OperStatus, ulong TxSpeed, ulong RxSpeed, ulong Rx, ulong Tx);
    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
    private readonly Dictionary<string, (ulong Rx, ulong Tx, double Time)> previous = new();
    private HashSet<uint> defaultRoutes = new();
    private int changed = 1;
    private double enumerated;
    private string selected = "", requested = "";
    private readonly Func<List<IfRow>> readInterfaces;
    private readonly Func<HashSet<uint>> readRoutes;
    private readonly Func<double> monotonicSeconds;
    public string Selected => selected;
    public string Error { get; private set; } = "";
    public NetworkSampler() : this(ReadInterfaces, ReadRoutes, () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) { }
    internal NetworkSampler(Func<List<IfRow>> interfaces, Func<HashSet<uint>> routes, Func<double> seconds)
    {
        readInterfaces = interfaces; readRoutes = routes; monotonicSeconds = seconds;
        NetworkChange.NetworkAddressChanged += Changed; NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
    }
    private void Changed(object? sender, EventArgs e) => Interlocked.Exchange(ref changed, 1);
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Interlocked.Exchange(ref changed, 1);
    internal static List<IfRow> ReadInterfaces()
    {
        uint error = GetIfTable2(out var table);
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        try {
            var list = new List<IfRow>(); int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++) {
                var address = IntPtr.Add(table, 8 + i * 1352);
                var native = Marshal.PtrToStructure<NativeIfRow>(address);
                var row = new IfRow(native.Id, native.Index, Marshal.PtrToStringUni(IntPtr.Add(address, 28)) ?? "未命名网卡", native.Type, native.Flags, native.OperStatus, native.TxSpeed, native.RxSpeed, native.Rx, native.Tx);
                // Suppress filter-stack replicas and loopback, retain actual virtual adapters for explicit selection.
                if (row.Id != Guid.Empty && row.Type != 24 && (row.Flags & 2) == 0) list.Add(row);
            }
            return list;
        } finally { FreeMibTable(table); }
    }
    private static HashSet<uint> ReadRoutes()
    {
        uint error = GetIpForwardTable2(0, out var table);
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        try {
            var set = new HashSet<uint>(); int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++) {
                var row = IntPtr.Add(table, 8 + i * 104);
                if (Marshal.ReadByte(row, 40) == 0) set.Add(unchecked((uint)Marshal.ReadInt32(row, 8)));
            }
            return set;
        } finally { FreeMibTable(table); }
    }
    public static string Choose(IReadOnlyList<NetworkDevice> devices, string requested, string current)
    {
        if (requested.Length > 0) return requested;
        if (devices.Any(n => n.Id == current && n.Physical && n.Connected)) return current;
        return devices.Where(n => n.Physical && n.Connected).OrderByDescending(n => n.DefaultRoute)
            .ThenBy(n => n.Id, StringComparer.Ordinal).FirstOrDefault()?.Id ?? "";
    }
    public static double? Rate(ulong current, ulong old, double elapsed) => current >= old && elapsed > 0 && elapsed <= 5 ? (current - old) / elapsed : null;
    public IReadOnlyList<NetworkDevice> Sample(string selection, bool reset)
    {
        try {
            double now = monotonicSeconds();
            bool refresh = Interlocked.Exchange(ref changed, 0) != 0 || now - enumerated >= 30 || reset;
            Error = "";
            if (refresh) {
                try { defaultRoutes = readRoutes(); } catch (Exception e) { defaultRoutes.Clear(); Error = "默认路由读取失败：" + e.Message; }
                enumerated = now;
            }
            if (reset) previous.Clear();
            var list = new List<NetworkDevice>();
            foreach (var row in readInterfaces()) {
                string id = row.Id.ToString("D"); bool connected = row.OperStatus == 1;
                Reading down = Reading.Missing(connected ? "等待网络差分采样" : "网卡未连接", connected ? ReadingState.WarmingUp : ReadingState.Disconnected), up = down;
                if (connected) {
                    if (previous.TryGetValue(id, out var old)) {
                        double? d = Rate(row.Rx, old.Rx, now - old.Time), u = Rate(row.Tx, old.Tx, now - old.Time);
                        if (d.HasValue && u.HasValue) {
                            down = new(d, row.Alias + " / Windows IP Helper 字节计数差"); up = new(u, down.Status);
                        }
                    }
                    previous[id] = (row.Rx, row.Tx, now);
                } else previous.Remove(id);
                list.Add(new(id, row.Alias, (row.Flags & 1) != 0 && row.Type is not (23 or 131), connected,
                    defaultRoutes.Contains(row.Index), row.RxSpeed, row.TxSpeed, down, up));
            }
            foreach (var id in previous.Keys.Except(list.Select(n => n.Id)).ToArray()) previous.Remove(id);
            string next = Choose(list, selection, selected);
            if (next != selected || selection != requested) {
                list = list.Select(n => n.Id != next || !n.Connected ? n : n with { Download = Reading.Missing("切换网卡后等待采样", ReadingState.WarmingUp), Upload = Reading.Missing("切换网卡后等待采样", ReadingState.WarmingUp) }).ToList();
            }
            selected = next; requested = selection;
            return list;
        } catch (Exception e) { previous.Clear(); Error = "网络采集失败：" + e.Message; return Array.Empty<NetworkDevice>(); }
    }
    public static IEnumerable<Metric> DeviceMetrics(NetworkDevice n)
    {
        string root = "network.nic/" + n.Id + "/";
        yield return Create(root + "download", "下载速度", "B/s", n.Download, n);
        yield return Create(root + "upload", "上传速度", "B/s", n.Upload, n);
        yield return Create(root + "total", "收发合计", "B/s", Combined(n), n);
        yield return Create(root + "load", "网络利用率", "%", Utilization(n), n);
    }
    private static Metric Create(string id, string name, string unit, Reading r, NetworkDevice n) => new(id, name, unit, r) { Group = "网络", Device = n.Name, DeviceId = n.Id, Source = "Windows IP Helper" };
    private static Reading Combined(NetworkDevice n) => n.Download.Value.HasValue && n.Upload.Value.HasValue
        ? new(n.Download.Value + n.Upload.Value, n.Name + " / 下载+上传") : n.Download.Value == null ? n.Download : n.Upload;
    private static Reading Utilization(NetworkDevice n) {
        if (n.Download.Value == null || n.Upload.Value == null) return Combined(n);
        if (n.ReceiveLinkSpeed == 0 || n.TransmitLinkSpeed == 0) return Reading.Missing("未提供链路速率", ReadingState.SensorMissing);
        return Reading.Percent(Math.Max(n.Download.Value.Value * 8 / n.ReceiveLinkSpeed, n.Upload.Value.Value * 8 / n.TransmitLinkSpeed) * 100, n.Name + " / 收发方向占各自链路速率的较大比例");
    }
    public static IEnumerable<Metric> SelectedMetrics(Snapshot s) {
        var device = s.Networks.FirstOrDefault(n => n.Id == s.SelectedNetworkId);
        foreach (var (suffix, name, unit) in new[] { ("download", "网络下载", "B/s"), ("upload", "网络上传", "B/s"), ("activity", "网络收发", "B/s"), ("load", "网络利用率", "%") }) {
            Reading r = device == null ? Reading.Missing(s.NetworkError.Length > 0 ? s.NetworkError : "没有符合条件的网卡或指定网卡已移除", s.NetworkError.Length > 0 ? ReadingState.Failed : ReadingState.Disconnected)
                : suffix switch { "download" => device.Download, "upload" => device.Upload, "load" => Utilization(device), _ => Combined(device) };
            yield return new Metric("network." + suffix, name, unit, r) { Device = device?.Name ?? "未选定网卡", DeviceId = s.SelectedNetworkId, Source = "Windows IP Helper" };
        }
    }
    public static string MigrateMetricId(string id) {
        var m = Regex.Match(Uri.UnescapeDataString(id), @"^sensor\.network/nic/([^/]+)/(throughput|load)/(\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success || !Guid.TryParse(m.Groups[1].Value, out var guid)) return id;
        string? suffix = (m.Groups[2].Value.ToLowerInvariant(), m.Groups[3].Value) switch { ("throughput", "7") => "upload", ("throughput", "8") => "download", ("load", "1") => "load", _ => null };
        return suffix == null ? id : "network.nic/" + guid.ToString("D") + "/" + suffix;
    }
    public void Dispose() { NetworkChange.NetworkAddressChanged -= Changed; NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged; }
}
