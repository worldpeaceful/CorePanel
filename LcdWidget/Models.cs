using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LcdWidget;

public enum ReadingState { Normal, WarmingUp, DriverMissing, PermissionDenied, Disconnected, SensorMissing, Failed, Stale, Unknown }
public sealed record Reading(double? Value, string Status)
{
    public ReadingState State { get; init; } = Value.HasValue ? ReadingState.Normal : ReadingState.Unknown;
    public string Source { get; init; } = "";
    public DateTimeOffset? AttemptedAt { get; init; }
    public DateTimeOffset? SucceededAt { get; init; }
    public static Reading Missing(string reason, ReadingState state = ReadingState.Unknown) => new(null, reason) { State = state };
    public Reading Expire() => this with { Value = null, State = ReadingState.Stale, Status = "超过 5 秒无有效更新" };
    public static Reading Percent(double value, string source) => double.IsFinite(value)
        ? new(Math.Clamp(value, 0, 100), source) : Missing("无有效读数");
}
public sealed record Device(string Id, string Name, string Match = "", string StableId = "");
public sealed record HardwareDevice(string Id, string Name, string Category, ReadingState State, string Detail, DateTimeOffset SeenAt);
public sealed record Snapshot(DateTimeOffset Timestamp, Reading Cpu, Reading CpuTemperature,
    Reading Gpu, Reading GpuTemperature, Reading Memory, Reading Disk,
    string GpuId, string GpuName, string DiskId, string DiskName,
    IReadOnlyList<Device> Gpus, IReadOnlyList<Device> Disks)
{
    public IReadOnlyList<Metric> ExtraMetrics { get; init; } = Array.Empty<Metric>();
    public IReadOnlyList<Metric> Catalog { get; init; } = Array.Empty<Metric>();
    public IReadOnlyList<HardwareDevice> HardwareDevices { get; init; } = Array.Empty<HardwareDevice>();
    public IReadOnlyList<NetworkDevice> Networks { get; init; } = Array.Empty<NetworkDevice>();
    public string SelectedNetworkId { get; init; } = "";
    public bool PawnIoInstalled { get; init; }
    public string NetworkError { get; init; } = "";
    public static Snapshot Empty(string reason, ReadingState state = ReadingState.WarmingUp) => new(DateTimeOffset.UtcNow,
        Reading.Missing(reason, state), Reading.Missing(reason, state), Reading.Missing(reason, state),
        Reading.Missing(reason, state), Reading.Missing(reason, state), Reading.Missing(reason, state),
        "", "等待设备", "", "等待设备", Array.Empty<Device>(), Array.Empty<Device>());
    public Snapshot Expire() => this with {
        ExtraMetrics = ExtraMetrics.Select(m => m with { Reading = m.Reading.Expire() }).ToArray(),
        Catalog = Catalog.Select(m => m with { Reading = m.Reading.Expire() }).ToArray(),
        Networks = Networks.Select(n => n with { Download = n.Download.Expire(), Upload = n.Upload.Expire() }).ToArray(),
        Cpu = Cpu.Expire(), CpuTemperature = CpuTemperature.Expire(), Gpu = Gpu.Expire(), GpuTemperature = GpuTemperature.Expire(), Memory = Memory.Expire(), Disk = Disk.Expire() };
}
public sealed class Settings
{
    public int SettingsVersion { get; set; }
    public string NetworkId { get; set; } = "";
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Size { get; set; } = 480;
    public bool Topmost { get; set; }
    public string GpuId { get; set; } = "";
    public string DiskId { get; set; } = "";
    public string SkinId { get; set; } = "red";
    public string[] SlotMetrics { get; set; } = (string[])Metrics.Defaults.Clone();
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LcdWidget");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static Settings Load()
    {
        try { var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DirectoryPath, "settings.json"))) ?? new(); result.Migrate(); return result; }
        catch { return new(); }
    }
    public void Migrate()
    {
        SlotMetrics = Metrics.Normalize(SlotMetrics);
        if (SettingsVersion < 2 && SlotMetrics[6] == "network.activity") SlotMetrics[6] = "network.download";
        SlotMetrics = SlotMetrics.Select(NetworkSampler.MigrateMetricId).ToArray();
        SettingsVersion = 2;
    }
    public string? Save()
    {
        try {
            Directory.CreateDirectory(DirectoryPath);
            var target = Path.Combine(DirectoryPath, "settings.json");
            File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(this, JsonOptions));
            File.Move(target + ".tmp", target, true);
            return null;
        } catch (Exception e) { Log.Write("保存设置: " + e.Message); return e.Message; }
    }
}
public static class Log
{
    public static void Write(string message)
    {
        try {
            Directory.CreateDirectory(Settings.DirectoryPath);
            var path = Path.Combine(Settings.DirectoryPath, "widget.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        } catch { }
    }
}
