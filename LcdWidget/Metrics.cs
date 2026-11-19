using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace LcdWidget;

public sealed record Metric(string Id, string Name, string Unit, Reading Reading)
{
    public string Group { get; init; } = "基本指标";
    public string Device { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string Source { get; init; } = "";
    public string OriginalName { get; init; } = "";
}
public static class Metrics
{
    // Append new slots so existing disk/custom selections keep their original indexes.
    public static readonly string[] Defaults = { "cpu.load", "cpu.temp", "gpu.load", "gpu.temp", "memory.load", "disk.load", "network.download", "network.upload" };
    public static readonly string[] Positions = { "第 1 行 · 左", "第 1 行 · 右", "第 2 行 · 左", "第 2 行 · 右", "第 3 行 · 左", "第 4 行 · 左", "第 3 行 · 右", "第 4 行 · 右" };
    public static readonly int[] ScreenOrder = { 0, 1, 2, 3, 4, 6, 5, 7 };
    public static string[] Normalize(string[]? ids) => Enumerable.Range(0, Defaults.Length)
        .Select(i => ids != null && i < ids.Length && !string.IsNullOrWhiteSpace(ids[i]) ? ids[i] : Defaults[i]).ToArray();
    public static IEnumerable<Metric> All(Snapshot s) => s.Catalog.Count > 0 ? s.Catalog : Raw(s);
    public static IEnumerable<Metric> Raw(Snapshot s) => new[] {
        new Metric("cpu.load", "CPU 使用率", "%", s.Cpu), new Metric("cpu.temp", "CPU 温度", "°C", s.CpuTemperature),
        new Metric("gpu.load", "GPU 使用率", "%", s.Gpu), new Metric("gpu.temp", "GPU 温度", "°C", s.GpuTemperature),
        new Metric("memory.load", "内存使用率", "%", s.Memory), new Metric("disk.load", "磁盘活动率", "%", s.Disk),
    }.Select(m => m with {
        DeviceId = m.Id.StartsWith("gpu.") ? s.GpuId : m.Id == "disk.load" ? s.DiskId : m.Id.StartsWith("cpu.") ? "cpu" : "memory",
        Device = m.Id.StartsWith("gpu.") ? s.GpuName : m.Id == "disk.load" ? s.DiskName : m.Id.StartsWith("cpu.") ? "CPU" : "系统内存",
        Source = m.Id.EndsWith("temp") ? "LibreHardwareMonitor" : m.Id == "cpu.load" ? "GetSystemTimes" : m.Id == "memory.load" ? "GlobalMemoryStatusEx" : "Windows PDH"
    }).Concat(NetworkSampler.SelectedMetrics(s)).Concat(s.ExtraMetrics);
    public static Metric Resolve(Snapshot s, string id) => All(s).FirstOrDefault(m => m.Id == id)
        ?? new Metric(id, "指标暂不可用", "", Reading.Missing("未发现所选指标：" + id, ReadingState.SensorMissing));
    public static Metric? FromSensor(ISensor sensor, bool gpu, string device)
        => FromSensor(sensor, gpu ? "gpu" : "cpu", device);
    public static Metric? FromSensor(ISensor sensor, string category, string device)
    {
        bool gpu = category == "gpu";
        string unit; double divisor = 1;
        switch (sensor.SensorType) {
            case SensorType.Temperature: unit = "°C"; break;
            case SensorType.Load: unit = "%"; break;
            case SensorType.Power: unit = "W"; break;
            case SensorType.Clock: unit = "GHz"; divisor = 1000; break;
            case SensorType.Voltage: unit = "V"; break;
            case SensorType.Data: unit = "GB"; break;
            case SensorType.SmallData: unit = "GB"; divisor = 1024; break;
            case SensorType.Throughput: unit = "B/s"; break;
            case SensorType.Fan: unit = "RPM"; break;
            case SensorType.Flow: unit = "L/h"; break;
            case SensorType.Control: case SensorType.Level: unit = "%"; break;
            case SensorType.Current: unit = "A"; break;
            case SensorType.Factor: unit = "×"; break;
            case SensorType.Energy: unit = "mWh"; break;
            case SensorType.TimeSpan: unit = "s"; break;
            default: return null;
        }
        string id = gpu ? $"sensor.gpu/{sensor.SensorType}/{sensor.Index}/{sensor.Name}" : "sensor." + category + sensor.Identifier;
        double? value = sensor.Value;
        var reading = value.HasValue && double.IsFinite(value.Value) && (value >= 0 || sensor.SensorType == SensorType.Temperature)
            ? new Reading(value / divisor, device + " / " + sensor.Name + " / LibreHardwareMonitor")
            : Reading.Missing(device + " / " + sensor.Name + "：无有效读数，可能需要管理员权限或驱动支持");
        string name = sensor.Name switch {
            "Upload Speed" => "上传速度", "Download Speed" => "下载速度", "Network Utilization" => "网络利用率",
            "Data Uploaded" => "累计上传", "Data Downloaded" => "累计下载", "Warning Temperature" => "警告温度阈值",
            "Critical Temperature" => "临界温度阈值", "Used Space" => "容量使用率", "Free Space" => "剩余容量", "Total Space" => "总容量",
            "Read Rate" => "读取速度", "Write Rate" => "写入速度", _ => sensor.Name
        };
        name = (name, sensor.SensorType) switch {
            ("CPU Package", SensorType.Power) => "CPU 封装功耗", ("CPU Package", SensorType.Temperature) => "CPU 封装温度",
            ("Core Max", SensorType.Temperature) => "最高核心温度", ("Core Average", SensorType.Temperature) => "平均核心温度",
            ("GPU Core", SensorType.Clock) => "核心频率", ("GPU Core", SensorType.Temperature) => "核心温度",
            ("GPU Core", SensorType.Load) => "核心使用率", ("GPU Core", SensorType.Voltage) => "核心电压",
            ("GPU Power", SensorType.Power) => "GPU 功耗", ("Memory Used", _) => "已用内存", ("Memory Available", _) => "可用内存", _ => name };
        string displayName = category is "cpu" or "gpu" ? category.ToUpperInvariant() + " · " + name : name + " · " + device;
        return new Metric(id, displayName +
            (gpu && sensor.SensorType == SensorType.Load ? " #" + sensor.Index : ""), unit, reading) {
            Device = device,
            DeviceId = sensor.Hardware.Identifier.ToString(), Source = "LibreHardwareMonitor", OriginalName = sensor.Name,
            Group = category switch { "cpu" => "CPU 传感器", "gpu" => "所选 GPU 传感器", "network" => "网络", "storage" => "存储", "motherboard" => "主板", "memory" => "内存", "battery" => "电池", "psu" => "电源", _ => "控制器及其他" }
        };
    }
    public static string StateText(ReadingState state) => state switch {
        ReadingState.Normal => "正常", ReadingState.WarmingUp => "预热", ReadingState.DriverMissing => "驱动缺失", ReadingState.PermissionDenied => "权限不足",
        ReadingState.Disconnected => "设备断开", ReadingState.SensorMissing => "未提供传感器", ReadingState.Failed => "采集失败", ReadingState.Stale => "过期", _ => "原因未确定"
    };
    public static string Suggestion(ReadingState state) => state switch {
        ReadingState.DriverMissing => "安装官方 PawnIO 驱动后，以管理员重启。", ReadingState.PermissionDenied => "使用当前账户以管理员重启后重试。",
        ReadingState.Disconnected => "检查连接或重新选择设备。", ReadingState.SensorMissing => "设备或驱动未提供此指标，可选择其他指标。",
        ReadingState.Stale or ReadingState.Failed => "检查日志并导出诊断；确认驱动响应后重新启动应用。", ReadingState.WarmingUp => "等待下一次采样。",
        ReadingState.Unknown => "原因尚未确定，可检查权限、驱动和硬件支持；不保证提权后可用。", _ => "最近一次读取成功；时间不代表硬件内部更新时间。"
    };
    public static Metric NetworkActivity(IEnumerable<Metric> metrics)
    {
        // Pick one busiest adapter rather than double-counting VPN/virtual and physical adapters.
        var busiest = metrics.Where(m => m.Group == "网络" && m.Unit == "B/s" && m.Id.Contains("/throughput/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.Id.Substring(0, m.Id.LastIndexOf("/throughput/", StringComparison.OrdinalIgnoreCase)))
            .Where(g => g.Count() >= 2 && g.All(m => m.Reading.Value.HasValue))
            .Select(g => new { Value = g.Sum(m => m.Reading.Value!.Value), Device = g.First().Device.Length > 0 ? g.First().Device : g.First().Name.Split(" · ")[0] })
            .OrderByDescending(g => g.Value).FirstOrDefault();
        return new Metric("network.activity", "网络收发", "B/s", busiest == null ? Reading.Missing("等待有效网卡收发采样")
            : new Reading(busiest.Value, busiest.Device + " / 当前最忙网卡上传+下载速率；不跨网卡累加 / LibreHardwareMonitor"));
    }
    public static Metric ForDisplay(Metric metric)
    {
        if (metric.Unit != "B/s" || !metric.Reading.Value.HasValue) return metric;
        double value = metric.Reading.Value.Value;
        string unit = "B/s";
        foreach (var next in new[] { "KB/s", "MB/s", "GB/s" }) {
            if (value < 1024) break;
            value /= 1024; unit = next;
        }
        return metric with { Unit = unit, Reading = metric.Reading with { Value = value } };
    }
    public static string Format(Metric metric)
    {
        var v = metric.Reading.Value;
        if (!v.HasValue || !double.IsFinite(v.Value) || v < -999 || v > 9999) return " --";
        int decimals = metric.Unit is "GHz" or "V" ? 2 : metric.Unit is "W" or "GB" or "KB/s" or "MB/s" or "GB/s" or "A" ? 1 : 0;
        if (Math.Abs(v.Value) >= 100) decimals = 0;
        return v.Value.ToString("F" + decimals, CultureInfo.InvariantCulture).PadLeft(3);
    }
}
