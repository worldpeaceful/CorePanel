using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LcdWidget;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            if (args.Length == 3 && args[0] == "--set-startup" && (args[1] == "on" || args[1] == "off")) {
                StartupService.SetEnabled(args[1] == "on", args[2]); return 0;
            }
            if (args.Length == 1 && args[0] == "--verify-startup") { StartupService.VerifyScheduler(); return 0; }
            if (args.Contains("--quit")) {
                try { using var quit = EventWaitHandle.OpenExisting(@"Local\LcdWidget.Quit.v1"); quit.Set(); } catch { }
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--diagnose") {
                using var diagnostic = new MonitorService { IncludeHardwareReport = true }; diagnostic.Start();
                Task.Delay(8000).GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(args[1]), diagnostic.DiagnosticJson()); return 0;
            }
            if (args.Length >= 2 && args[0] == "--verify") return Verification.Run(Path.GetFullPath(args[1]));
            if (args.Length >= 2 && args[0] == "--wait-pid" && int.TryParse(args[1], out var pid)) {
                try { using var previous = Process.GetProcessById(pid); if (!previous.WaitForExit(15000)) return 2; } catch (ArgumentException) { }
            }
            using var mutex = new Mutex(true, @"Local\LcdWidget.SingleInstance.v1", out bool first);
            if (!first) {
                try { using var existing = EventWaitHandle.OpenExisting(@"Local\LcdWidget.Show.v1"); existing.Set(); } catch { }
                return 0;
            }
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LcdWidget.Show.v1");
            using var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LcdWidget.Quit.v1");
            using var monitor = new MonitorService();
            var settings = Settings.Load(); monitor.Select(settings.GpuId, settings.DiskId); monitor.SelectNetwork(settings.NetworkId);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) => { Log.Write("界面异常: " + e.Exception); };
            var window = new MainWindow(settings, monitor);
            if (args.Contains("--taskbar")) window.ShowInTaskbar = true;
            var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => app.Dispatcher.BeginInvoke(window.Restore), null, -1, false);
            var quitRegistration = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => app.Dispatcher.BeginInvoke(window.Close), null, -1, false);
            if (args.Contains("--settings")) window.Loaded += (_, _) => window.OpenSettings();
            monitor.Start();
            try { return app.Run(window); }
            finally { registration.Unregister(null); quitRegistration.Unregister(null); mutex.ReleaseMutex(); }
        } catch (Exception e) {
            Log.Write("启动失败: " + e);
            if (args.Length == 0) MessageBox.Show("启动失败：\n" + e.Message + "\n日志：" + Settings.DirectoryPath, "机核仪表");
            return 1;
        }
    }
}
