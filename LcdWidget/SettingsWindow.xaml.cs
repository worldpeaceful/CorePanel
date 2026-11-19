using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LcdWidget;

public sealed class MetricRow : INotifyPropertyChanged
{
    public Metric Metric { get; private set; }
    public MetricRow(Metric metric) => Metric = metric;
    public string Id => Metric.Id;
    public string Name => Metric.Name;
    public string Device => Metric.Device;
    public string Unit => Metrics.ForDisplay(Metric).Unit;
    public string Value => Metrics.Format(Metrics.ForDisplay(Metric));
    public string ValueWithUnit => Value.Trim() + " " + Unit;
    public string State => Metrics.StateText(Metric.Reading.State);
    public string Source => Metric.Source;
    public string LastSuccess => Metric.Reading.SucceededAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "从未成功";
    public string LastAttempt => Metric.Reading.AttemptedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "尚未尝试";
    public void Update(Metric m) { Metric = m; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("")); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) || (Name + " " + Device + " " + Metric.OriginalName + " " + Id).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}

public partial class SettingsWindow : Window
{
    private readonly MainWindow widget;
    private readonly MonitorService monitor;
    private readonly ObservableCollection<MetricRow> rows = new();
    private readonly Dictionary<string, MetricRow> byId = new();
    private readonly ListCollectionView metricView, statusView;
    private readonly Button[] slotButtons = new Button[8];
    private int slot;
    private bool syncing, startupBusy;
    public SettingsWindow(MainWindow widget, MonitorService monitor)
    {
        this.widget = widget; this.monitor = monitor;
        InitializeComponent(); Icon = widget.Icon; BrandIcon.Source = widget.Icon;
        PageScroll.SizeChanged += (_, _) => {
            // Keep a finite viewport so the inner metric grids remain virtualized.
            PageLayout.Width = Math.Max(680, PageScroll.ActualWidth - 42);
            PageLayout.Height = Math.Max(600, PageScroll.ActualHeight - 42);
        };
        metricView = new ListCollectionView(rows); statusView = new ListCollectionView(rows);
        metricView.Filter = item => item is MetricRow r && r.Metric.Group != "原始网络传感器" && r.Matches(MetricSearch.Text)
            && (GroupBox.SelectedItem is not string group || group == "全部类别" || r.Metric.Group == group)
            && (DeviceBox.SelectedItem is not string device || device == "全部设备" || r.Device == device);
        statusView.Filter = item => item is MetricRow r && r.Matches(StatusSearch.Text)
            && (OnlyErrors.IsChecked != true || r.Metric.Reading.State != ReadingState.Normal)
            && (OnlyUsed.IsChecked != true || widget.Preferences.SlotMetrics.Contains(r.Id));
        MetricGrid.IsSynchronizedWithCurrentItem = false; StatusGrid.IsSynchronizedWithCurrentItem = false;
        MetricGrid.ItemsSource = metricView; StatusGrid.ItemsSource = statusView;
        SkinBox.ItemsSource = Skins.All; SizeBox.ItemsSource = new[] { 320, 400, 480, 600, 720, 800 };
        foreach (int i in Metrics.ScreenOrder) {
            int position = i;
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 5), ToolTip = Metrics.Positions[i] };
            button.Click += (_, _) => { slot = position; SyncSlot(); };
            slotButtons[i] = button; Slots.Children.Add(button);
        }
        MetricSearch.TextChanged += (_, _) => FilterMetrics(); StatusSearch.TextChanged += (_, _) => statusView.Refresh();
        GroupBox.SelectionChanged += (_, _) => { if (!syncing) FilterMetrics(); };
        DeviceBox.SelectionChanged += (_, _) => { if (!syncing) FilterMetrics(); };
        OnlyErrors.Click += (_, _) => statusView.Refresh(); OnlyUsed.Click += (_, _) => statusView.Refresh();
        MetricGrid.SelectionChanged += (_, _) => { if (!syncing && MetricGrid.SelectedItem is MetricRow row) widget.SelectMetric(slot, row.Id); };
        StatusGrid.SelectionChanged += (_, _) => UpdateDetail();
        SkinBox.SelectionChanged += (_, _) => { if (!syncing && SkinBox.SelectedValue is string id) widget.ApplySkin(id); };
        SizeBox.SelectionChanged += (_, _) => { if (!syncing && SizeBox.SelectedItem is int size) widget.SetSize(size); };
        TopmostBox.Click += (_, _) => widget.SetTopmost(TopmostBox.IsChecked == true);
        GpuBox.SelectionChanged += (_, _) => { if (!syncing && GpuBox.SelectedValue is string id) widget.SelectDevice(id, widget.Preferences.DiskId); };
        DiskBox.SelectionChanged += (_, _) => { if (!syncing && DiskBox.SelectedValue is string id) widget.SelectDevice(widget.Preferences.GpuId, id); };
        NetworkBox.SelectionChanged += (_, _) => { if (!syncing && NetworkBox.SelectedValue is string id) widget.SelectNetwork(id); };
        ResetButton.Click += (_, _) => widget.ResetMetrics();
        StartupButton.Click += async (_, _) => {
            StartupButton.IsEnabled = false;
            try { await widget.ToggleStartupAsync(); } catch (Exception e) { MessageBox.Show(this, e.Message, "自启动设置失败"); }
            finally { await RefreshStartup(); }
        };
        AdminButton.IsEnabled = !MonitorService.IsAdministrator;
        AdminButton.Click += (_, _) => widget.RestartElevated();
        LogButton.Click += (_, _) => {
            try { Directory.CreateDirectory(Settings.DirectoryPath); Process.Start(new ProcessStartInfo(Settings.DirectoryPath) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "无法打开日志目录"); }
        };
        ExportButton.Click += (_, _) => ExportDiagnostic();
        widget.SettingsChanged += SettingsChanged; widget.SnapshotChanged += RefreshSnapshot;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closed += (_, _) => { widget.SettingsChanged -= SettingsChanged; widget.SnapshotChanged -= RefreshSnapshot; SystemEvents.DisplaySettingsChanged -= DisplayChanged; };
        Loaded += async (_, _) => { KeepVisible(); await RefreshStartup(); };
        Activated += async (_, _) => await RefreshStartup();
        SettingsChanged(); RefreshSnapshot();
    }
    public async void ExportDiagnostic()
    {
            var dialog = new SaveFileDialog { Title = "导出当前诊断（不自动上传）", FileName = "机核仪表-诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", Filter = "JSON 报告|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            ExportButton.IsEnabled = false;
            try { await ExportToFileAsync(dialog.FileName); SaveStatus.Text = "诊断已保存：" + dialog.FileName; }
            catch (Exception e) { MessageBox.Show(this, e.Message, "导出失败"); }
            finally { ExportButton.IsEnabled = true; }
    }
    public Task ExportToFileAsync(string path) => Task.Run(() => File.WriteAllText(path, monitor.DiagnosticJson()));
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(KeepVisible);
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Dispatcher.BeginInvoke(KeepVisible);
    }
    private void KeepVisible()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = screen.Width / dpi.DpiScaleX, h = screen.Height / dpi.DpiScaleY;
        MinWidth = Math.Min(740, w); MinHeight = Math.Min(560, h);
        MaxWidth = w; MaxHeight = h;
        Width = Math.Min(Width, w); Height = Math.Min(Height, h);
        double left = screen.Left / dpi.DpiScaleX, top = screen.Top / dpi.DpiScaleY;
        Left = Math.Clamp(Left, left, Math.Max(left, left + w - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, top + h - Height));
    }
    private void FilterMetrics()
    {
        bool previous = syncing; syncing = true;
        try { metricView.Refresh(); SyncSlot(); } finally { syncing = previous; }
    }
    public void SelectPage(int page) => Tabs.SelectedIndex = Math.Clamp(page, 0, 2);
    public void RenderToFile(string path, double scale)
    {
        var content = (FrameworkElement)Content;
        content.Measure(new Size(1000, 700)); content.Arrange(new Rect(0, 0, 1000, 700)); content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(1000 * scale), (int)(700 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private async Task RefreshStartup()
    {
        if (startupBusy) return;
        startupBusy = true;
        try { bool on = await Task.Run(StartupService.IsEnabled); StartupButton.Content = on ? "自启动：开启（点击关闭）" : "自启动：关闭（点击开启）"; StartupButton.IsEnabled = true; }
        catch (Exception e) { StartupButton.Content = "自启动状态读取失败"; StartupButton.ToolTip = e.Message; StartupButton.IsEnabled = false; }
        finally { startupBusy = false; }
    }
    private void SettingsChanged()
    {
        syncing = true;
        try {
            SkinBox.SelectedValue = widget.Preferences.SkinId; SizeBox.SelectedItem = (int)widget.Preferences.Size;
            TopmostBox.IsChecked = widget.Preferences.Topmost;
            SyncSlot();
            SaveStatus.Text = widget.SaveError == null ? "已保存 · 设置即时生效" : "保存失败（当前修改仅临时生效）：" + widget.SaveError;
            SaveStatus.Foreground = widget.SaveError == null ? LcdDisplay.Frozen("#A6C2CD") : LcdDisplay.Frozen("#FFAE92");
        } finally { syncing = false; }
        RefreshSnapshot();
        _ = RefreshStartup();
    }
    private static void Options(ComboBox box, IEnumerable<Device> items, string selected)
    {
        var array = items.ToArray();
        if (selected.Length > 0 && !array.Any(d => d.Id == selected)) array = array.Append(new Device(selected, "已保存设备（当前未发现）")).ToArray();
        if (box.ItemsSource is not Device[] old || !old.SequenceEqual(array)) box.ItemsSource = array;
        box.SelectedValue = selected;
    }
    private static void Filters(ComboBox box, string first, IEnumerable<string> values)
    {
        var items = new[] { first }.Concat(values.Where(v => v.Length > 0).Distinct().OrderBy(v => v)).ToArray();
        string selected = box.SelectedItem as string ?? first;
        if (box.ItemsSource is not string[] old || !old.SequenceEqual(items)) box.ItemsSource = items;
        box.SelectedItem = items.Contains(selected) ? selected : first;
    }
    private void SyncSlot()
    {
        bool was = syncing; syncing = true;
        try {
            foreach (int i in Metrics.ScreenOrder) {
                var m = Metrics.Resolve(widget.CurrentSnapshot, widget.Preferences.SlotMetrics[i]);
                slotButtons[i].Content = new TextBlock { Text = Metrics.Positions[i] + "   " + m.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 245 };
                slotButtons[i].BorderBrush = i == slot ? LcdDisplay.Frozen("#8FE3DD") : LcdDisplay.Frozen("#466072");
            }
            SlotHeading.Text = "正在配置：" + Metrics.Positions[slot];
            MetricGrid.SelectedItem = byId.GetValueOrDefault(widget.Preferences.SlotMetrics[slot]);
        } finally { syncing = was; }
    }
    private void RefreshSnapshot()
    {
        syncing = true;
        try {
            var s = widget.CurrentSnapshot;
            EnvironmentText.Text = $"v1.6.0  ·  {(MonitorService.IsAdministrator ? "管理员" : "普通权限")}  ·  PawnIO：{(s.Catalog.Count == 0 ? "检测中" : s.PawnIoInstalled ? "已安装" : "未安装")}  ·  LibreHardwareMonitor {typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version}";
            foreach (var m in Metrics.All(s).Concat(widget.Preferences.SlotMetrics.Where(id => !Metrics.All(s).Any(m => m.Id == id)).Select(id => Metrics.Resolve(s, id))).DistinctBy(m => m.Id)) {
                if (byId.TryGetValue(m.Id, out var row)) row.Update(m);
                else { row = new MetricRow(m); byId[m.Id] = row; rows.Add(row); }
            }
            Filters(GroupBox, "全部类别", rows.Where(r => r.Metric.Group != "原始网络传感器").Select(r => r.Metric.Group));
            Filters(DeviceBox, "全部设备", rows.Select(r => r.Device));
            RefreshView(MetricGrid, metricView); RefreshView(StatusGrid, statusView);
            Options(GpuBox, new[] { new Device("", "自动选择 GPU") }.Concat(s.Gpus.Select(d => new Device(d.StableId.Length > 0 ? d.StableId : d.Id, d.Name))), widget.Preferences.GpuId);
            Options(DiskBox, new[] { new Device("", "自动选择系统盘") }.Concat(s.Disks), widget.Preferences.DiskId);
            Options(NetworkBox, new[] { new Device("", "自动 · 稳定联网物理网卡") }.Concat(s.Networks.Select(n => new Device(n.Id, n.Name + " · " + n.Kind + " · " + n.Connection))), widget.Preferences.NetworkId);
            ReplaceRows(NetworkGrid, s.Networks, o => ((NetworkDevice)o).Id);
            var nic = s.Networks.FirstOrDefault(n => n.Id == s.SelectedNetworkId);
            NetworkSummary.Text = "当前来源：" + (nic?.Name ?? "未找到符合条件的网卡") + (s.NetworkError.Length > 0 ? "\n" + s.NetworkError : "");
            ReplaceRows(DeviceGrid, s.HardwareDevices.Select(d => new DeviceRow(d.Id, d.Name, d.Category, Metrics.StateText(d.State), d.Detail)).ToArray(), o => ((DeviceRow)o).Id);
            SyncSlot(); UpdateDetail();
        } finally { syncing = false; }
    }
    private static ScrollViewer? Scroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = Scroll(VisualTreeHelper.GetChild(root, i)); if (found != null) return found; }
        return null;
    }
    private static void RefreshView(DataGrid grid, ListCollectionView view)
    {
        var scroller = Scroll(grid); double offset = scroller?.VerticalOffset ?? 0;
        var selected = grid.SelectedItem; view.Refresh(); grid.SelectedItem = selected;
        scroller?.ScrollToVerticalOffset(offset);
    }
    private sealed record DeviceRow(string Id, string Name, string Category, string State, string Detail);
    private static void ReplaceRows<T>(DataGrid grid, IEnumerable<T> items, Func<object, string> key)
    {
        var scroller = Scroll(grid); double vertical = scroller?.VerticalOffset ?? 0, horizontal = scroller?.HorizontalOffset ?? 0;
        string? selection = grid.SelectedItem == null ? null : key(grid.SelectedItem);
        var array = items.ToArray(); grid.ItemsSource = array;
        if (selection != null) grid.SelectedItem = array.FirstOrDefault(i => i != null && key(i) == selection);
        scroller?.ScrollToVerticalOffset(vertical); scroller?.ScrollToHorizontalOffset(horizontal);
    }
    private void UpdateDetail()
    {
        if (StatusGrid.SelectedItem is not MetricRow row) return;
        var m = row.Metric;
        DetailText.Text = $"{m.Name}  |  {m.Device}\n原名：{m.OriginalName}  ·  ID：{m.Id}\n{m.Reading.Status}\n{Metrics.Suggestion(m.Reading.State)}\n最后成功：{row.LastSuccess}  ·  最后尝试：{row.LastAttempt}";
    }
}
