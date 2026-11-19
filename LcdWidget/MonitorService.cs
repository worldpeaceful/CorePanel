using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;

namespace LcdWidget;

public sealed class MonitorService : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task? worker;
    private Snapshot latest = Snapshot.Empty("初始化中");
    private string gpuSelection = "", diskSelection = "", networkSelection = "";
    private readonly SnapshotTracker tracker = new();
    public void SelectNetwork(string id) => Volatile.Write(ref networkSelection, id);
    private string hardwareReport = "硬件尚未初始化";
    public bool IncludeHardwareReport { get; set; }
    private string libraryReport = "";
    public Snapshot Latest => Volatile.Read(ref latest);
    public void Select(string gpu, string disk) { Volatile.Write(ref gpuSelection, gpu); Volatile.Write(ref diskSelection, disk); }
    public static bool IsAdministrator {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    public void Start() { worker ??= Task.Run(Run); }
    private async Task Run()
    {
        Computer? computer = null;
        PdhQuery? pdh = null;
        var cpu = new CpuSampler();
        using var networkSampler = new NetworkSampler();
        List<Device> adapters = new();
        int tick = 0;
        DateTimeOffset lastSample = DateTimeOffset.MinValue;
        string hardwareError = "未发现可读温度传感器";
        try {
            // Enable supported hardware categories; sampling remains on the owning background thread.
            try {
                computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsMemoryEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = true, IsControllerEnabled = true, IsBatteryEnabled = true, IsPsuEnabled = true, IsPowerMonitorEnabled = true };
                computer.Open();
            } catch (Exception e) { hardwareError = e.Message; Log.Write("硬件初始化: " + e); }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do {
                try {
                    bool resumed = lastSample != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - lastSample > TimeSpan.FromSeconds(5);
                    bool reset = tick++ % 30 == 0 || resumed;
                    if (resumed) cpu = new CpuSampler();
                    lastSample = DateTimeOffset.UtcNow;
                    if (reset) {
                        pdh?.Dispose();
                        pdh = new PdhQuery(("gpu", @"\GPU Engine(*)\Utilization Percentage"), ("disk", @"\PhysicalDisk(*)\% Idle Time"));
                        adapters = DxgiDevices.Enumerate();
                    }
                    var cpuReading = cpu.Sample();
                    var memory = CpuSampler.Memory();
                    var collected = pdh?.Collect() == true;
                    var gpuRaw = collected && !reset ? pdh!.Read("gpu") : new Dictionary<string, double>();
                    var diskRaw = collected && !reset ? pdh!.Read("disk") : new Dictionary<string, double>();
                    var loads = GpuMath.Aggregate(gpuRaw);
                    var devices = new List<Device>(adapters);
                    // Ignore software/NPU counter instances which are not physical DXGI GPUs.
                    var gpuId = Volatile.Read(ref gpuSelection);
                    var gpu = devices.FirstOrDefault(d => d.Id == gpuId || d.StableId == gpuId && gpuId.Length > 0);
                    // Never silently switch an explicitly selected, disconnected device.
                    if (gpuId.Length == 0) gpu = devices.FirstOrDefault(d => loads.ContainsKey(d.Id)) ?? devices.FirstOrDefault();
                    var disks = diskRaw.Keys.Where(k => k != "_Total").OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                        .Select(k => new Device(k.Split(' ')[0], "磁盘 " + k, k)).ToList();
                    if (reset) disks = Latest.Disks.ToList();
                    var diskId = Volatile.Read(ref diskSelection);
                    var disk = disks.FirstOrDefault(d => d.Id == diskId);
                    string systemDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                    if (diskId.Length == 0) disk = disks.FirstOrDefault(d => d.Match.Split(' ').Contains(systemDrive, StringComparer.OrdinalIgnoreCase)) ?? disks.FirstOrDefault();
                    Reading cpuTemp = Reading.Missing(!LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled ? "未安装 PawnIO 温度驱动；见启动说明" : IsAdministrator ? hardwareError : "需管理员权限或传感器不受支持；右键可管理员重启");
                    Reading gpuTemp = Reading.Missing("所选 GPU 未提供可读核心温度");
                    var networks = networkSampler.Sample(Volatile.Read(ref networkSelection), resumed);
                    var hardwareDevices = new List<HardwareDevice>();
                    var extraMetrics = networks.SelectMany(NetworkSampler.DeviceMetrics).ToList();
                    hardwareDevices.AddRange(networks.Select(n => new HardwareDevice(n.Id, n.Name, "网络", n.Connected ? ReadingState.Normal : ReadingState.Disconnected, n.Connection, DateTimeOffset.UtcNow)));
                    if (computer != null) {
                        var hardware = computer.Hardware;
                        var updated = new HashSet<IHardware>();
                        var onlineNetworks = new HashSet<string>();
                        try {
                            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                                if (n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up) onlineNetworks.Add(n.Name);
                        } catch (System.Net.NetworkInformation.NetworkInformationException) { }
                        foreach (var h in hardware.SelectMany(Flatten)) {
                            try {
                                h.Update();

                                updated.Add(h);
                                hardwareDevices.Add(new HardwareDevice(h.Identifier.ToString(), h.Name, Category(h), h.Sensors.Length == 0 ? ReadingState.SensorMissing : ReadingState.Normal, h.Sensors.Length == 0 ? "设备已识别，未提供传感器" : "已枚举传感器", DateTimeOffset.UtcNow));
                                if (h.HardwareType != HardwareType.Cpu && !IsGpu(h)) {
                                    var category = Category(h);
                                    var metrics = h.Sensors.Select(s => Metrics.FromSensor(s, category, h.Name)).OfType<Metric>();
                                    if (category == "network") {
                                        bool online = onlineNetworks.Contains(h.Name);
                                        if (!online || resumed || tick == 1) metrics = metrics.Select(m => m with {
                                            Reading = Reading.Missing(!online ? "网卡未连接" : "网络速率等待下一次差分采样") });
                                    }
                                    if (category == "network") metrics = metrics.Select(m => m with { Group = "原始网络传感器" });
                                    extraMetrics.AddRange(metrics);
                                }
                                if (h.HardwareType == HardwareType.Cpu) {
                                    extraMetrics.AddRange(Sensors(h).Select(s => Metrics.FromSensor(s, false, h.Name)).OfType<Metric>());
                                    var temps = Sensors(h).Where(s => s.SensorType == SensorType.Temperature && ValidTemperature(s.Value)).ToArray();
                                    var sensor = temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                                        ?? temps.Where(s => IsCoreTemperature(s.Name)).OrderByDescending(s => s.Value).FirstOrDefault();
                                    if (sensor != null) cpuTemp = new Reading(sensor.Value, h.Name + " / " + sensor.Name);
                                }
                            } catch (Exception e) {
                                hardwareError = "硬件采集失败: " + e.Message;
                                hardwareDevices.Add(new HardwareDevice(h.Identifier.ToString(), h.Name, Category(h), e is UnauthorizedAccessException ? ReadingState.PermissionDenied : ReadingState.Failed, hardwareError, DateTimeOffset.UtcNow));
                            }
                        }
                        if (gpu != null) {
                            var matches = hardware.Where(IsGpu).Where(updated.Contains).Where(h => Matches(h, gpu)).ToArray();
                            if (matches.Length == 1 && devices.Count(d => d.Match == gpu.Match) == 1) {
                                extraMetrics.AddRange(Sensors(matches[0]).Select(s => Metrics.FromSensor(s, true, matches[0].Name)).OfType<Metric>());
                                var sensor = Sensors(matches[0]).FirstOrDefault(s => s.SensorType == SensorType.Temperature &&
                                    (s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase)) && ValidTemperature(s.Value));
                                if (sensor != null) gpuTemp = new Reading(sensor.Value, matches[0].Name + " / " + sensor.Name);
                            }
                        }
                        hardwareReport = string.Join("\n", hardware.Select(h => h.Name + " [" + h.HardwareType + "] " +
                            (h as GenericGpu)?.DeviceId + "\n" + string.Join("\n", Sensors(h).Where(s => s.SensorType == SensorType.Temperature).Select(s => s.Name + ": " + s.Value))));
                        if (IncludeHardwareReport && tick == 5) libraryReport = computer.GetReport();
                    }
                    if (cpuTemp.Value == null && !LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled) cpuTemp = cpuTemp with { State = ReadingState.DriverMissing };
                    if (gpuTemp.Value == null && gpu == null) gpuTemp = gpuTemp with { State = ReadingState.Disconnected };
                    var gpuLoad = gpu != null && loads.TryGetValue(gpu.Id, out double g) ? Reading.Percent(g, "所选 GPU 最忙引擎") : Reading.Missing(reset ? "正在采样" : "GPU 性能计数器不可用或设备已移除", reset ? ReadingState.WarmingUp : gpu == null ? ReadingState.Disconnected : ReadingState.Failed);
                    var diskLoad = disk != null && diskRaw.TryGetValue(disk.Match, out double idle) ? Reading.Percent(100 - idle, "物理磁盘活动时间 / 100 - 空闲率") : Reading.Missing(reset ? "正在采样" : "磁盘计数器不可用或设备已移除", reset ? ReadingState.WarmingUp : disk == null ? ReadingState.Disconnected : ReadingState.Failed);
                    // Warm-up/re-enumeration does not briefly erase previously valid PDH readings.
                    if (reset && Latest.Timestamp > DateTimeOffset.UtcNow.AddSeconds(-3)) {
                        if (Latest.GpuId == (gpu?.Id ?? gpuId)) gpuLoad = Latest.Gpu;
                        if (Latest.DiskId == (disk?.Id ?? diskId)) diskLoad = Latest.Disk;
                    }
                    Volatile.Write(ref latest, tracker.Track(new Snapshot(DateTimeOffset.UtcNow, cpuReading, cpuTemp, gpuLoad, gpuTemp, memory, diskLoad,
                        gpu?.Id ?? gpuId, gpu?.Name ?? "GPU 不可用", disk?.Id ?? diskId, disk?.Name ?? "磁盘不可用", devices, disks) { ExtraMetrics = extraMetrics, Networks = networks, SelectedNetworkId = networkSampler.Selected, NetworkError = networkSampler.Error, HardwareDevices = hardwareDevices, PawnIoInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled }));
                } catch (Exception e) { Log.Write("采集: " + e); Volatile.Write(ref latest, tracker.Track(Snapshot.Empty("采集失败: " + e.Message, ReadingState.Failed))); }
            } while (await timer.WaitForNextTickAsync(cancellation.Token));
        } catch (OperationCanceledException) { }
        catch (Exception e) { Log.Write("监测线程: " + e); }
        finally { pdh?.Dispose(); try { computer?.Close(); } catch (Exception e) { Log.Write("关闭硬件: " + e.Message); } }
    }
    private static IEnumerable<IHardware> Flatten(IHardware h) => new[] { h }.Concat(h.SubHardware.SelectMany(Flatten));
    private static string Category(IHardware h) => h.HardwareType.ToString() switch {
        "Network" => "network", "Storage" => "storage", "Memory" => "memory", "Battery" => "battery",
        "Motherboard" or "SuperIO" or "EmbeddedController" => "motherboard", "Psu" => "psu", _ => "other"
    };
    private static bool ValidTemperature(float? value) => value.HasValue && float.IsFinite(value.Value) && value > 0 && value < 150;
    public static bool IsCoreTemperature(string name) => !name.Contains("Distance", StringComparison.OrdinalIgnoreCase) &&
        (name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase) || name.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase) || name.Equals("Core Max", StringComparison.OrdinalIgnoreCase));
    private static bool IsGpu(IHardware h) => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;
    private static IEnumerable<ISensor> Sensors(IHardware h) => h.Sensors.Concat(h.SubHardware.SelectMany(Sensors));
    private static bool Matches(IHardware h, Device device)
    {
        string? id = (h as GenericGpu)?.DeviceId;
        if (!string.IsNullOrEmpty(id) && device.Match.Length > 0) return id.Contains(device.Match, StringComparison.OrdinalIgnoreCase);
        return string.Equals(h.Name.Trim(), device.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    public string DiagnosticJson() => System.Text.Json.JsonSerializer.Serialize(new { Application = "机核仪表", Version = "1.6.0", Administrator = IsAdministrator, Snapshot = DateTimeOffset.UtcNow - Latest.Timestamp > TimeSpan.FromSeconds(5) ? Latest.Expire() : Latest, HardwareSensors = hardwareReport, LibraryReport = IncludeHardwareReport ? libraryReport : null }, Settings.JsonOptions);
    public void Dispose()
    {
        cancellation.Cancel();
        // Cleanup occurs on the owning hardware thread. A stuck driver must not block application exit indefinitely.
        try { worker?.Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
    }
}
