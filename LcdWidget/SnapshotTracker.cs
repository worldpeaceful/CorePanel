using System;
using System.Collections.Generic;
using System.Linq;

namespace LcdWidget;

public sealed class SnapshotTracker
{
    private readonly Dictionary<string, Metric> metrics = new();
    private readonly Dictionary<string, HardwareDevice> devices = new();
    public Snapshot Track(Snapshot snapshot)
    {
        var now = snapshot.Timestamp;
        var seenDevices = snapshot.HardwareDevices.Select(d => d.Id).ToHashSet();
        foreach (var d in snapshot.HardwareDevices) devices[d.Id] = d;
        foreach (var id in devices.Keys.Except(seenDevices).ToArray()) devices[id] = devices[id] with { State = ReadingState.Disconnected, Detail = "本次枚举未发现设备" };
        var seen = new HashSet<string>();
        foreach (var metric in Metrics.Raw(snapshot)) {
            seen.Add(metric.Id);
            metrics.TryGetValue(metric.Id, out var old);
            bool same = old?.DeviceId == metric.DeviceId;
            var r = metric.Reading;
            bool valid = r.Value.HasValue && double.IsFinite(r.Value.Value);
            // A carried-forward PDH sample keeps its actual success time.
            var success = valid ? r.SucceededAt ?? now : same ? old?.Reading.SucceededAt : null;
            metrics[metric.Id] = metric with { Reading = r with { State = valid ? ReadingState.Normal : r.State, Source = metric.Source,
                AttemptedAt = now, SucceededAt = success } };
        }
        foreach (var id in metrics.Keys.Except(seen).ToArray()) {
            var m = metrics[id];
            var device = snapshot.HardwareDevices.FirstOrDefault(d => d.Id == m.DeviceId);
            var state = device == null ? ReadingState.Disconnected : device.State == ReadingState.Failed || device.State == ReadingState.PermissionDenied ? device.State : ReadingState.SensorMissing;
            metrics[id] = m with { Reading = m.Reading with { Value = null, State = state, Status = device?.Detail ?? "本次采样未发现该设备或指标", AttemptedAt = now } };
        }
        Reading Get(string id) => metrics[id].Reading;
        return snapshot with {
            Cpu = Get("cpu.load"), CpuTemperature = Get("cpu.temp"), Gpu = Get("gpu.load"), GpuTemperature = Get("gpu.temp"), Memory = Get("memory.load"), Disk = Get("disk.load"),
            Catalog = metrics.Values.ToArray(), HardwareDevices = devices.Values.ToArray()
        };
    }
}
