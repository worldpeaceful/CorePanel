using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace LcdWidget;

public static class Verification
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
        System.Xml.Linq.XNamespace taskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        const string testPath = @"C:\测试 & 空格\LcdWidget.exe";
        var startupXml = System.Xml.Linq.XElement.Parse(StartupService.CreateXml(testPath, StartupService.UserSid));
        Check(startupXml.Element(taskNs + "Actions")?.Element(taskNs + "Exec")?.Element(taskNs + "Command")?.Value == testPath,
            "自启动：中文、空格及 XML 特殊字符路径保留");
        Check(startupXml.Element(taskNs + "Principals")?.Element(taskNs + "Principal")?.Element(taskNs + "RunLevel")?.Value == "HighestAvailable"
            && startupXml.Element(taskNs + "Triggers")?.Element(taskNs + "LogonTrigger")?.Element(taskNs + "UserId")?.Value == StartupService.UserSid,
            "自启动：当前账户登录触发、最高可用权限");
        _ = StartupService.IsEnabled(); checks.Add("PASS 自启动：读取实际计划任务状态");
        const string adapter = "luid_0x00000000_0x00001234_phys_0";
        var data = new Dictionary<string, double> {
            [$"pid_1_{adapter}_eng_0_engtype_3D"] = 35,
            [$"pid_2_{adapter}_eng_0_engtype_3D"] = 40,
            [$"pid_1_{adapter}_eng_1_engtype_Copy"] = 60,
            ["pid_9_luid_0x00000000_0x00005678_phys_0_eng_0_engtype_3D"] = 12
        };
        var result = GpuMath.Aggregate(data);
        Check(result[adapter] == 75 && result.Count == 2, "GPU: 同引擎跨进程求和、跨引擎取最大、设备隔离");
        data[$"pid_2_{adapter}_eng_0_engtype_3D"] = 120;
        Check(GpuMath.Aggregate(data)[adapter] == 100, "GPU: 上限 100%");
        Check(LcdDisplay.Format(100) == "100" && LcdDisplay.Format(null) == " --" && LcdDisplay.Format(0) == "  0", "数字边界和缺失值");
        Check(Snapshot.Empty("test").Expire().Cpu.Value == null, "过期快照清空数据");
        Check(MonitorService.IsCoreTemperature("P-Core #1") && MonitorService.IsCoreTemperature("E-Core #12") &&
              !MonitorService.IsCoreTemperature("P-Core #1 Distance to TjMax"), "温度回退不会误用距 TjMax 的温差");
        Check(CpuSampler.Memory().Value is >= 0 and <= 100, "真实内存采集");
        Check(DxgiDevices.Enumerate().Count > 0, "真实 DXGI 设备枚举");
        var app = new Application();
        Check(Metrics.Normalize(null).SequenceEqual(Metrics.Defaults) && Metrics.Normalize(new[] { "gpu.load" })[5] == "disk.load", "指标：旧设置及短数组兼容");
        var oldSlots = new[] { "gpu.load", "cpu.load", "disk.load", "memory.load", "cpu.temp", "gpu.temp" };
        Check(Metrics.Normalize(oldSlots).Take(6).SequenceEqual(oldSlots) && Metrics.Normalize(oldSlots)[6] == "network.download" && Metrics.Normalize(oldSlots)[7] == "network.upload", "八位置：旧六位置无损迁移，追加收发");
        var migrated = new Settings { SlotMetrics = oldSlots.Append("network.activity").ToArray() }; migrated.Migrate();
        Check(migrated.SettingsVersion == 2 && migrated.SlotMetrics[6] == "network.download" && migrated.SlotMetrics[5] == "gpu.temp", "旧默认网络迁移且保留磁盘位置自定义");
        var custom = new Settings { SlotMetrics = oldSlots.Append("cpu.load").ToArray() }; custom.Migrate();
        Check(custom.SlotMetrics[6] == "cpu.load", "自定义第七位置不被覆盖");
        Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(migrated))!.SlotMetrics.SequenceEqual(migrated.SlotMetrics), "八位置设置保存恢复");
        string testGuid = "11111111-2222-3333-4444-555555555555";
        Check(NetworkSampler.MigrateMetricId("sensor.network/nic/{" + testGuid + "}/throughput/7") == "network.nic/" + testGuid + "/upload", "网络 GUID 与指标类型精确迁移");
        Check(NetworkSampler.MigrateMetricId("sensor.network/nic/ambiguous/throughput/7").Contains("ambiguous"), "不按名称猜测迁移");
        NetworkDevice Nic(string id, bool physical, bool connected, bool route) => new(id, "TEST " + id, physical, connected, route, 1_000_000_000, 1_000_000_000, new Reading(1024, "TEST"), new Reading(2048, "TEST"));
        var nics = new[] { Nic("a", true, true, false), Nic("b", true, true, true), Nic("virtual", false, true, true) };
        Check(NetworkSampler.Choose(nics, "", "") == "b", "自动网络优先默认路由物理网卡");
        Check(NetworkSampler.Choose(nics, "", "a") == "a", "已连接网卡保持稳定、不跟随流量切换");
        Check(NetworkSampler.Choose(nics, "missing", "b") == "missing", "指定网卡消失不会悄悄换源");
        Check(NetworkSampler.Choose(nics.Select(n => n with { Connected = n.Id == "virtual" }).ToArray(), "", "b") == "", "自动模式不选择虚拟网卡");
        Check(NetworkSampler.Choose(nics.Select(n => n.Id == "a" ? n with { Connected = false } : n).ToArray(), "", "a") == "b", "自动网卡断开后重新选择");
        Check(NetworkSampler.Rate(2000, 1000, 2) == 500 && NetworkSampler.Rate(1, 1000, 1) == null && NetworkSampler.Rate(2000, 1000, 6) == null, "差分速率、计数重置和休眠预热");
        double sampleTime = 100;
        var sampleRow = new NetworkSampler.IfRow(Guid.Parse(testGuid), 1, "TEST NIC", 6, 1, 1, 1_000_000, 1_000_000, 1000, 2000);
        using (var simulated = new NetworkSampler(() => new() { sampleRow }, () => new() { 1 }, () => sampleTime)) {
            Check(simulated.Sample("", false)[0].Download.State == ReadingState.WarmingUp, "网络首次采样预热");
            sampleTime++; sampleRow = sampleRow with { Rx = 2024, Tx = 4048 };
            Check(simulated.Sample("", false)[0].Download.Value == 1024, "网络单调时钟字节差分");
            sampleTime++; sampleRow = sampleRow with { Rx = 1, Tx = 1 };
            Check(simulated.Sample("", false)[0].Download.Value == null, "计数回退预热且不产生负值");
            sampleTime++; sampleRow = sampleRow with { OperStatus = 2 };
            Check(simulated.Sample(testGuid, false)[0].Download.State == ReadingState.Disconnected && simulated.Selected == testGuid, "固定网卡断开不换源");
            sampleTime++; sampleRow = sampleRow with { OperStatus = 1, Rx = 999999 };
            Check(simulated.Sample(testGuid, false)[0].Download.State == ReadingState.WarmingUp, "断连恢复先预热，不计算离线累计流量");
            sampleTime++; sampleRow = sampleRow with { Rx = 1001023 };
            Check(simulated.Sample(testGuid, false)[0].Download.Value == 1024, "重连后下一秒恢复有效速率");
            sampleTime += 20; sampleRow = sampleRow with { Rx = 9000000 };
            Check(simulated.Sample(testGuid, true)[0].Download.State == ReadingState.WarmingUp, "恢复休眠清除差分基线");
        }
        var networkSnapshot = Snapshot.Empty("TEST") with { Networks = nics, SelectedNetworkId = "b" };
        Check(Metrics.Resolve(networkSnapshot, "network.activity").Reading.Value == 3072 && Metrics.Resolve(networkSnapshot, "network.download").Reading.Value == 1024, "所选网卡收发独立、合计不跨网卡");
        using (var sampler = new NetworkSampler()) {
            var liveNics = sampler.Sample("", false);
            Check(liveNics.Count > 0 && liveNics.All(n => Guid.TryParse(n.Id, out _)), "真实 IP Helper 网卡 GUID 枚举");
            Check(liveNics.Select(n => n.Id).Distinct().Count() == liveNics.Count, "网卡重复接口隔离");
            Check(liveNics.Any(n => n.Physical), "真实物理网卡标识读取");
        }
        Metric Traffic(string nic, int index, double? value) => new("sensor.network/nic/" + nic + "/throughput/" + index, nic + " · speed", "B/s", new Reading(value, "TEST")) { Group = "网络" };
        var traffic = new[] { Traffic("A", 7, 1024), Traffic("A", 8, 2048), Traffic("B", 7, 512), Traffic("B", 8, 512) };
        Check(Metrics.NetworkActivity(traffic).Reading.Value == 3072, "网络：取最忙单网卡，不重复累加多个接口");
        Check(Metrics.NetworkActivity(new[] { Traffic("A", 7, 1), Traffic("A", 8, null) }).Reading.Value == null, "网络：缺少有效收发样本不伪造零值");
        var trafficDisplay = Metrics.ForDisplay(Metrics.NetworkActivity(traffic));
        Check(trafficDisplay.Unit == "KB/s" && Metrics.Format(trafficDisplay).Trim() == "3.0", "网络：速率与单位同步缩放");
        Check(Metrics.Resolve((Snapshot.Empty("TEST") with { ExtraMetrics = traffic }).Expire(), "network.activity").Reading.Value == null, "网络：过期清空派生指标");
        var selectedSettings = new Settings { SlotMetrics = new[] { "gpu.load", "cpu.load", "disk.load", "memory.load", "cpu.temp", "gpu.temp" } };
        Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(selectedSettings))!.SlotMetrics.SequenceEqual(selectedSettings.SlotMetrics), "指标：六个位置选择保存恢复");
        var customMetric = new Metric("test.clock", "CPU · P-Core #12 Frequency TEST", "GHz", new Reading(4.48, "仅测试"));
        Check(Metrics.Format(customMetric) == "4.48" && Metrics.Format(customMetric with { Reading = Reading.Missing("test") }) == " --", "指标：小数与缺失值格式");
        var customSnapshot = Snapshot.Empty("test") with { ExtraMetrics = new[] { customMetric } };
        var tracker = new SnapshotTracker();
        var tracked = tracker.Track(customSnapshot);
        var success = Metrics.Resolve(tracked, customMetric.Id).Reading.SucceededAt;
        var failed = tracker.Track(customSnapshot with { Timestamp = customSnapshot.Timestamp.AddSeconds(1), ExtraMetrics = new[] { customMetric with { Reading = Reading.Missing("TEST failure", ReadingState.Failed) } } });
        Check(success != null && Metrics.Resolve(failed, customMetric.Id).Reading.SucceededAt == success && Metrics.Resolve(failed, customMetric.Id).Reading.AttemptedAt > success, "失败保留最后成功读取时间，尝试时间独立更新");
        Check(Metrics.Resolve(tracked.Expire(), customMetric.Id).Reading.State == ReadingState.Stale && Metrics.Resolve(tracked.Expire(), customMetric.Id).Reading.Value == null, "过期清空数值并保留结构化状态");
        var removed = tracker.Track(Snapshot.Empty("TEST") with { Timestamp = customSnapshot.Timestamp.AddSeconds(2) });
        Check(Metrics.Resolve(removed, customMetric.Id).Reading.State == ReadingState.Disconnected, "本次运行保留已移除指标并标记断开");
        Check(Metrics.Resolve(customSnapshot.Expire(), customMetric.Id).Reading.Value == null && Metrics.Resolve(customSnapshot, "removed").Reading.Value == null, "指标：过期和移除不保留旧数值");
        using var monitor = new MonitorService();
        var window = new MainWindow(new Settings(), monitor, true);
        window.Present(tracker.Track(networkSnapshot with { ExtraMetrics = NetworkSampler.DeviceMetrics(nics[1]).ToArray(), HardwareDevices = new[] { new HardwareDevice("board-test", "主板 TEST", "主板", ReadingState.SensorMissing, "设备已识别，未提供传感器", DateTimeOffset.UtcNow) } }));
        var settingsWindow = new SettingsWindow(window, monitor);
        var beforeFilter = window.Preferences.SlotMetrics.ToArray();
        ((System.Windows.Controls.TextBox)settingsWindow.FindName("MetricSearch")).Text = "网络收发";
        Check(window.Preferences.SlotMetrics.SequenceEqual(beforeFilter), "设置页搜索不自动修改显示指标");
        ((System.Windows.Controls.TextBox)settingsWindow.FindName("MetricSearch")).Text = "";
        var reportPath = Path.Combine(directory, "diagnostic-export-TEST.json");
        settingsWindow.ExportToFileAsync(reportPath).GetAwaiter().GetResult();
        using (var report = JsonDocument.Parse(File.ReadAllText(reportPath))) {
            Check(report.RootElement.GetProperty("Version").GetString() == "1.6.0" && report.RootElement.GetProperty("LibraryReport").ValueKind == JsonValueKind.Null, "设置页诊断导出路径写入 JSON，默认不含硬件原始报告");
        }
        for (int page = 0; page < 3; page++) {
            settingsWindow.SelectPage(page);
            foreach (double dpi in new[] { 1.0, 1.5, 2.0 }) settingsWindow.RenderToFile(Path.Combine(directory, $"settings-TEST-page{page}-dpi{dpi * 100}.png"), dpi);
        }
        settingsWindow.Close();
        Check(!window.Dispatcher.HasShutdownStarted, "关闭设置窗口不退出应用");
        window.Present(customSnapshot);
        for (int i = 0; i < Metrics.Defaults.Length; i++) window.SelectMetric(i, customMetric.Id);
        foreach (var skin in Skins.All) {
            window.ApplySkin(skin.Id, false);
            foreach (int size in new[] { 320, 480, 720 }) window.RenderToFile(Path.Combine(directory, $"metrics-TEST-{skin.Id}-{size}.png"), size);
        }
        for (int i = 0; i < Metrics.Defaults.Length; i++) window.SelectMetric(i, Metrics.Defaults[i]);
        window.Present(Snapshot.Empty("网络布局 TEST") with { ExtraMetrics = traffic });
        window.RenderToFile(Path.Combine(directory, "network-TEST.png"), 480);
        var values = new[] { 0, 12, 34, 56, 78, 90, 100, 123 };
        foreach (int n in values) {
            var r = new Reading(n, "仅布局测试 / 非实测值");
            window.Present(new Snapshot(DateTimeOffset.UtcNow, r, r, r, r, r, r, "test", "布局测试 TEST", "test", "TEST 磁盘", Array.Empty<Device>(), Array.Empty<Device>()));
            foreach (int pixels in new[] { 320, 480, 720 }) window.RenderToFile(Path.Combine(directory, $"layout-TEST-{n}-{pixels}.png"), pixels);
        }
        window.Present(Snapshot.Empty("测试传感器缺失 / TEST"));
        window.RenderToFile(Path.Combine(directory, "layout-TEST-missing.png"), 480);
        checks.Add("PASS 25 张明确标记 TEST 的布局渲染（320/480/720；0–9/100/123/缺失值）");
        Check(Skins.Resolve("missing").Id == "red", "无效皮肤回退到红色，兼容旧设置");
        var settingsRoundtrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { SkinId = "pink" }));
        Check(settingsRoundtrip?.SkinId == "pink", "皮肤选择序列化保留");
        foreach (var skin in Skins.All) {
            Check(skin.CreateClip().FillContains(new Point(240, 240)) && !skin.CreateClip().FillContains(new Point(0, 0)), skin.Name + "：中心保留、外角透明");
            window.ApplySkin(skin.Id, false);
            var skinPath = Path.Combine(directory, "skin-TEST-" + skin.Id + ".png");
            window.RenderToFile(skinPath, 480);
            using var rendered = new System.Drawing.Bitmap(skinPath);
            Check(rendered.GetPixel(0, 0).A == 0 && rendered.GetPixel(240, 240).A == 255,
                skin.Name + "：实际输出外角 alpha=0，内部 alpha=255");
        }
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, Settings.JsonOptions));
        app.Shutdown(); return 0;
    }
}
