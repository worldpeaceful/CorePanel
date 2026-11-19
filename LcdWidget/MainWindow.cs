using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LcdWidget;

public sealed class MainWindow : Window
{
    private readonly Settings settings;
    private readonly MonitorService monitor;
    private readonly DispatcherTimer timer;
    private readonly Forms.NotifyIcon? tray;
    private readonly System.Drawing.Icon? trayIcon;
    private readonly Canvas face = new() { Width = 480, Height = 480 };
    private readonly Image background = new() { Width = 480, Height = 480, Stretch = Stretch.Fill };
    private readonly TextBlock unitLabel;
    private readonly TextBlock heading;
    private readonly LcdDisplay cpu = new(), cpuTemp = new(), gpu = new(), gpuTemp = new(), memory = new(), disk = new(), network = new(), upload = new();
    private readonly TextBlock gpuName, diskName, status;
    private readonly TextBlock[] metricLabels = new TextBlock[8], metricUnits = new TextBlock[8];
    private LcdDisplay[] Displays => new[] { cpu, cpuTemp, gpu, gpuTemp, memory, disk, network, upload };
    private int nextSlot;
    private Snapshot snapshot = Snapshot.Empty("初始化中");
    private bool closing;
    private readonly bool verification;
    public MainWindow(Settings settings, MonitorService monitor, bool verification = false)
    {
        this.settings = settings; this.monitor = monitor; settings.Migrate();
        this.verification = verification;
        Title = "机核仪表";
        var iconUri = new Uri("pack://application:,,,/Assets/CorePanel.ico");
        Icon = BitmapFrame.Create(iconUri);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Width = Height = double.IsFinite(settings.Size) ? Math.Clamp(settings.Size, 320, 800) : 480;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = settings.Topmost;
        UseLayoutRounding = true;
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = face };
        face.Children.Add(background);
        heading = Label("机核仪表 / CORE PANEL", 82, 69, 9, "#7E8885");
        unitLabel = Label("", 320, 69, 9, "#7E8885");
        Row("CPU", "LOAD", "TEMP", 99, cpu, cpuTemp);
        Row("GPU", "LOAD", "TEMP", 173, gpu, gpuTemp);
        Row("MEMORY", "PHYSICAL", "NETWORK", 247, memory, network);
        Row("DISK", "ACTIVE TIME", "UPLOAD", 321, disk, upload);
        gpuName = Label("检测显卡…", 274, 245, 8, "#717C78", 121);
        // Hardware names are placed under the corresponding numbers, with full names in tooltips.
        Canvas.SetTop(gpuName, 232);
        diskName = Label("检测磁盘…", 268, 348, 10, "#858F8B", 125);

        status = Label("INITIALIZING", 83, 402, 8, "#717C78", 215);
        ApplySkin(settings.SkinId, false); Present(snapshot);
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); Save(); } };
        MouseRightButtonUp += (_, e) => { OpenMenu(); e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); if (e.Key == Key.Apps) OpenMenu(); };
        Loaded += (_, _) => {
            if (double.IsFinite(settings.Left) && double.IsFinite(settings.Top) && !(settings.Left == -1 && settings.Top == -1)) { Left = settings.Left; Top = settings.Top; }
            else { Left = SystemParameters.WorkArea.Right - Width - 24; Top = SystemParameters.WorkArea.Top + 48; }
            KeepVisible();
        };
        if (!verification) {
            using (var resource = Application.GetResourceStream(iconUri).Stream)
            using (var sourceIcon = new System.Drawing.Icon(resource, new System.Drawing.Size(32, 32)))
                trayIcon = (System.Drawing.Icon)sourceIcon.Clone();
            tray = new Forms.NotifyIcon { Text = "机核仪表", Icon = trayIcon, Visible = true };
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("显示 / 隐藏", null, (_, _) => Dispatcher.Invoke(() => { if (IsVisible) Hide(); else Restore(); }));
            trayMenu.Items.Add("设置…", null, (_, _) => Dispatcher.Invoke(() => OpenSettings()));
            trayMenu.Items.Add("传感器状态…", null, (_, _) => Dispatcher.Invoke(() => OpenSettings(2)));
            trayMenu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
            tray.ContextMenuStrip = trayMenu;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh(); if (!verification) timer.Start();
        Closing += OnClosing;
    }
    private void Row(string name, string sub, string? second, double top, LcdDisplay value, LcdDisplay? temperature)
    {
        int left = nextSlot++;
        metricLabels[left] = Label("", 83, top, 10, "#A1ABA7", 156);
        AddDigits(value, 83, top + 20, 109, 44);
        metricUnits[left] = Label("", 198, top + 40, 11, "#8F9B96", 40);
        if (temperature != null) {
            int right = name == "MEMORY" ? 6 : name == "DISK" ? 7 : nextSlot++;
            metricLabels[right] = Label("", 277, top, 9, "#A1ABA7", 118);
            AddDigits(temperature, 277, top + 23, 87, 38);
            metricUnits[right] = Label("", 364, top + 40, 8, "#8F9B96", 33);
        }
        if (name != "DISK") {
            var line = new System.Windows.Shapes.Line { X1 = 83, X2 = 395, Y1 = top + 70, Y2 = top + 70, Stroke = LcdDisplay.Frozen("#29302D"), StrokeThickness = .6 };
            face.Children.Add(line);
        }
    }
    private TextBlock Label(string text, double x, double y, double size, string color, double width = double.NaN)
    {
        var label = new TextBlock { Text = text, FontFamily = new FontFamily("Bahnschrift, Segoe UI"), FontSize = size,
            Foreground = LcdDisplay.Frozen(color), Width = width, TextTrimming = TextTrimming.CharacterEllipsis };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); face.Children.Add(label); return label;
    }
    private void AddDigits(LcdDisplay display, double x, double y, double width, double height)
    {
        display.Width = width; display.Height = height;
        Canvas.SetLeft(display, x); Canvas.SetTop(display, y); face.Children.Add(display);
    }
    private static void Set(LcdDisplay display, Reading reading)
    {
        display.Text = LcdDisplay.Format(reading.Value); display.ToolTip = reading.Status;
        System.Windows.Automation.AutomationProperties.SetName(display, $"{display.Text.Trim()}：{reading.Status}");
    }
    public void Refresh()
    {
        var value = monitor.Latest;
        if (DateTimeOffset.UtcNow - value.Timestamp > TimeSpan.FromSeconds(5)) value = value.Expire();
        Present(value);
        SnapshotChanged?.Invoke();
    }
    public void Present(Snapshot value)
    {
        snapshot = value;
        bool missing = false;
        for (int i = 0; i < Metrics.Defaults.Length; i++) {
            var metric = Metrics.ForDisplay(Metrics.Resolve(value, settings.SlotMetrics[i]));
            metricLabels[i].Text = metric.Name; metricLabels[i].ToolTip = metric.Name;
            metricUnits[i].Text = metric.Unit;
            var display = Displays[i];
            display.Text = Metrics.Format(metric);
            display.ToolTip = metric.Name + "\n" + metric.Device + " · " + metric.Source + "\n" + metric.Reading.Status + "\n右键 → 显示指标 → " + Metrics.Positions[i];
            if (metric.Id.StartsWith("gpu.") || metric.Id.StartsWith("sensor.gpu")) display.ToolTip += "\n" + value.GpuName + " / " + value.GpuId;
            if (metric.Id == "disk.load") display.ToolTip += "\n" + value.DiskName + " / " + value.DiskId;
            System.Windows.Automation.AutomationProperties.SetName(display, metric.Name + " " + display.Text + " " + metric.Unit);
            missing |= metric.Reading.Value == null || display.Text.Contains('-');
        }
        gpuName.Text = ""; diskName.Text = "";
        status.Text = missing ? "LIVE / 部分数据不可用 · 悬停查看" : "LIVE / 1 SEC   •   SYSTEM MONITOR";
        status.ToolTip = "左键拖动 · 右键设置 · 托盘显示/隐藏\n" + value.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
    public void Restore() { Show(); WindowState = WindowState.Normal; KeepVisible(); Activate(); }
    private void DisplayChanged(object? sender, EventArgs e) { if (!closing) Dispatcher.BeginInvoke(KeepVisible); }
    private void KeepVisible()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        // Screen rectangles use physical pixels; WPF placement uses the window's current DPI.
        var screen = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double left = screen.Left / dpi.DpiScaleX, top = screen.Top / dpi.DpiScaleY;
        double right = screen.Right / dpi.DpiScaleX, bottom = screen.Bottom / dpi.DpiScaleY;
        if (Width > right - left || Height > bottom - top) Width = Height = Math.Min(right - left, bottom - top);
        Left = Math.Clamp(Left, left, Math.Max(left, right - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
    }
    private MenuItem Item(string title, Action action, bool check = false)
    {
        var item = new MenuItem { Header = title, IsChecked = check };
        item.Click += (_, _) => action(); return item;
    }
    private void OpenMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("设置…", () => OpenSettings()));
        menu.Items.Add(Item("传感器状态…", () => OpenSettings(2)));
        var metricsMenu = new MenuItem { Header = "显示指标" };
        foreach (int i in Metrics.ScreenOrder) {
            int slot = i;
            var position = new MenuItem { Header = Metrics.Positions[i] + "：" + Metrics.Resolve(snapshot, settings.SlotMetrics[i]).Name };
            foreach (var group in Metrics.All(snapshot).Where(m => m.Group != "原始网络传感器").GroupBy(m => m.Group)) {
                var category = new MenuItem { Header = group.Key };
                foreach (var deviceGroup in group.GroupBy(m => group.Key is "基本指标" or "CPU 传感器" or "所选 GPU 传感器" ? "" : m.Device)
                    .OrderByDescending(g => g.Any(m => m.Reading.Value.HasValue))) {
                    var destination = category;
                    if (deviceGroup.Key.Length > 0) {
                        destination = new MenuItem { Header = deviceGroup.Key + (deviceGroup.All(m => m.Reading.Value == null) ? "（无读数）" : "") };
                        category.Items.Add(destination);
                    }
                    foreach (var metric in deviceGroup) {
                        var choice = Item(metric.Name + " (" + metric.Unit + ")", () => SelectMetric(slot, metric.Id), settings.SlotMetrics[slot] == metric.Id);
                        choice.ToolTip = metric.Reading.Status; destination.Items.Add(choice);
                    }
                }
                position.Items.Add(category);
            }
            metricsMenu.Items.Add(position);
        }
        metricsMenu.Items.Add(new Separator());
        metricsMenu.Items.Add(Item("恢复默认指标", () => { ResetMetrics(); }));
        menu.Items.Add(metricsMenu);
        var skins = new MenuItem { Header = "皮肤" };
        foreach (var skin in Skins.All)
            skins.Items.Add(Item(skin.Name, () => ApplySkin(skin.Id), settings.SkinId == skin.Id));
        menu.Items.Add(skins);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("始终置顶", () => { Topmost = !Topmost; Save(); }, Topmost));
        var startup = new MenuItem { Header = "开机自启动（读取中…）", IsEnabled = false };
        menu.Items.Add(startup);
        LoadStartupMenu(startup);
        var sizes = new MenuItem { Header = "缩放" };
        foreach (var size in new[] { 320, 400, 480, 600, 720, 800 }) {
            int chosen = size;
            sizes.Items.Add(Item($"{size} × {size}" + (size == 480 ? "（默认）" : ""), () => { Width = Height = chosen; KeepVisible(); Save(); }, Math.Abs(Width - chosen) < 1));
        }
        menu.Items.Add(sizes);
        var gpus = new MenuItem { Header = "选择 GPU" };
        gpus.Items.Add(Item("自动选择", () => SelectDevice("", settings.DiskId), settings.GpuId == ""));
        foreach (var d in snapshot.Gpus) gpus.Items.Add(Item(d.Name, () => SelectDevice(d.StableId.Length > 0 ? d.StableId : d.Id, settings.DiskId), settings.GpuId == d.Id || settings.GpuId == d.StableId));
        menu.Items.Add(gpus);
        var disks = new MenuItem { Header = "选择磁盘（活动率）" };
        disks.Items.Add(Item("自动选择系统盘", () => SelectDevice(settings.GpuId, ""), settings.DiskId == ""));
        foreach (var d in snapshot.Disks) disks.Items.Add(Item(d.Name, () => SelectDevice(settings.GpuId, d.Id), settings.DiskId == d.Id));
        menu.Items.Add(disks);
        menu.Items.Add(new Separator());
        var admin = Item(MonitorService.IsAdministrator ? "已以管理员运行" : "以管理员重启（读取温度）", RestartElevated);
        admin.IsEnabled = !MonitorService.IsAdministrator; menu.Items.Add(admin);
        menu.Items.Add(Item("导出诊断报告…", () => { OpenSettings(2); settingsWindow!.ExportDiagnostic(); }));
        menu.Items.Add(Item("隐藏到托盘", Hide));
        menu.Items.Add(Item("退出", Close));
        ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
    public void ApplySkin(string id, bool save = true)
    {
        var skin = Skins.Resolve(id);
        background.Source = skin.LoadImage();
        face.Clip = skin.CreateClip();
        unitLabel.Text = "LIVE   •   " + skin.Unit;
        Canvas.SetLeft(heading, skin.Id == "blue" ? 110 : 82);
        Canvas.SetTop(heading, skin.Id == "blue" ? 72 : 69);
        Canvas.SetLeft(status, skin.Id == "blue" ? 100 : 83);
        Canvas.SetTop(status, skin.Id == "blue" ? 394 : 402);
        settings.SkinId = skin.Id;
        if (save) Save();
    }
    public void SelectMetric(int slot, string id)
    {
        if (slot < 0 || slot >= Metrics.Defaults.Length) throw new ArgumentOutOfRangeException(nameof(slot));
        settings.SlotMetrics[slot] = id; Save(); Present(snapshot);
    }
    public void SelectDevice(string gpuId, string diskId)
    {
        settings.GpuId = gpuId; settings.DiskId = diskId; monitor.Select(gpuId, diskId); Save();
        Present(snapshot with { Catalog = snapshot.Catalog.Select(m => m.Id is "gpu.load" or "gpu.temp" or "disk.load" ? m with { Reading = Reading.Missing("切换后等待采样", ReadingState.WarmingUp) } : m).ToArray(), Gpu = Reading.Missing("切换后等待采样", ReadingState.WarmingUp), GpuTemperature = Reading.Missing("切换后等待采样", ReadingState.WarmingUp), Disk = Reading.Missing("切换后等待采样", ReadingState.WarmingUp) });
        SnapshotChanged?.Invoke();
    }
    public void RestartElevated()
    {
        try {
            Save();
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            info.ArgumentList.Add("--wait-pid"); info.ArgumentList.Add(Environment.ProcessId.ToString());
            if (ShowInTaskbar) info.ArgumentList.Add("--taskbar");
            if (Process.Start(info) != null) Close();
        } catch (Win32Exception e) when (e.NativeErrorCode == 1223) { status.Text = "已取消提权 · 普通监测继续"; }
        catch (Exception e) { MessageBox.Show(this, "管理员重启失败：" + e.Message, "机核仪表"); }
    }
    private bool startupChanging;
    private async void LoadStartupMenu(MenuItem item)
    {
        try {
            bool enabled = await Task.Run(StartupService.IsEnabled);
            item.Header = "开机自启动"; item.IsChecked = enabled; item.IsEnabled = !startupChanging;
            item.ToolTip = "登录当前 Windows 账户后以最高可用权限启动；修改时可能需要 UAC 授权。";
            item.Click += async (_, _) => {
                if (startupChanging) return;
                startupChanging = true; item.IsEnabled = false;
                try {
                    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
                    if (!MonitorService.IsAdministrator) info.Verb = "runas";
                    info.ArgumentList.Add("--set-startup"); info.ArgumentList.Add(enabled ? "off" : "on"); info.ArgumentList.Add(StartupService.UserSid);
                    using var helper = Process.Start(info) ?? throw new InvalidOperationException("无法启动配置程序。");
                    await helper.WaitForExitAsync();
                    if (helper.ExitCode != 0) throw new InvalidOperationException("配置失败，请检查当前账户权限。详细原因见 widget.log。");
                    bool actual = await Task.Run(StartupService.IsEnabled);
                    if (actual == enabled) throw new InvalidOperationException("自启动状态未改变，请重试。");
                    enabled = actual; item.IsChecked = actual;
                    MessageBox.Show(this, actual ? "已开启：下次登录 Windows 后自动启动小组件。\n请保留当前发布目录的位置。" : "已关闭开机自启动。", "机核仪表");
                } catch (Win32Exception e) when (e.NativeErrorCode == 1223) {
                    MessageBox.Show(this, "已取消授权，自启动设置未更改。", "机核仪表");
                } catch (Exception e) { Log.Write("自启动配置失败: " + e); MessageBox.Show(this, e.Message, "自启动设置失败"); }
                finally { startupChanging = false; item.IsEnabled = true; SettingsChanged?.Invoke(); }
            };
        } catch (Exception e) { item.Header = "开机自启动（无法读取）"; item.ToolTip = e.Message; Log.Write("自启动读取失败: " + e); }
    }
    private SettingsWindow? settingsWindow;
    public Settings Preferences => settings;
    public Snapshot CurrentSnapshot => snapshot;
    public string? SaveError { get; private set; }
    public event Action? SettingsChanged;
    public event Action? SnapshotChanged;
    public void OpenSettings(int page = 0)
    {
        if (settingsWindow == null) {
            settingsWindow = new SettingsWindow(this, monitor);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.SelectPage(page); settingsWindow.Show();
        settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate();
    }
    public void ResetMetrics() { settings.SlotMetrics = (string[])Metrics.Defaults.Clone(); Save(); Present(snapshot); }
    public void SetSize(double size) { Width = Height = size; KeepVisible(); Save(); }
    public void SetTopmost(bool value) { Topmost = value; Save(); }
    public void SelectNetwork(string id) { settings.NetworkId = id; monitor.SelectNetwork(id); Save(); }
    public async Task ToggleStartupAsync()
    {
        if (startupChanging) return;
        startupChanging = true;
        try {
            bool enabled = await Task.Run(StartupService.IsEnabled);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
            if (!MonitorService.IsAdministrator) info.Verb = "runas";
            info.ArgumentList.Add("--set-startup"); info.ArgumentList.Add(enabled ? "off" : "on"); info.ArgumentList.Add(StartupService.UserSid);
            using var helper = Process.Start(info) ?? throw new InvalidOperationException("无法启动配置程序。");
            await helper.WaitForExitAsync();
            if (helper.ExitCode != 0) throw new InvalidOperationException("配置失败，详细原因见 widget.log。");
        } catch (Win32Exception e) when (e.NativeErrorCode == 1223) { MessageBox.Show("已取消授权，自启动设置未更改。", "机核仪表"); }
        finally { startupChanging = false; SettingsChanged?.Invoke(); }
    }
    private void Save()
    {
        if (verification) return;
        settings.Left = Left; settings.Top = Top; settings.Size = Width; settings.Topmost = Topmost; SaveError = settings.Save(); SettingsChanged?.Invoke();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        closing = true; settingsWindow?.Close(); timer.Stop(); Save();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose();
    }
    public void RenderToFile(string path, int pixels)
    {
        face.Measure(new Size(480, 480)); face.Arrange(new Rect(0, 0, 480, 480)); face.UpdateLayout();
        var image = new RenderTargetBitmap(pixels, pixels, 96.0 * pixels / 480, 96.0 * pixels / 480, PixelFormats.Pbgra32);
        image.Render(face); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
