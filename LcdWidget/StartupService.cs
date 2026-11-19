using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace LcdWidget;

internal static class StartupService
{
    internal static string UserSid => WindowsIdentity.GetCurrent().User!.Value;
    private static string TaskName => "LcdWidget.Startup." + UserSid;
    internal static string CreateXml(string executable, string sid, bool enabled = true)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "机核仪表：当前用户登录后启动")),
            E("Triggers", E("LogonTrigger", new object[] { E("Enabled", true), E("UserId", sid), E("Delay", "PT10S") })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new object[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("StartWhenAvailable", true), E("Enabled", enabled), E("ExecutionTimeLimit", "PT0S") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), E("Exec", new object[] {
                E("Command", executable), E("WorkingDirectory", Path.GetDirectoryName(executable)!) }))).ToString();
    }

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try { ((dynamic)service).Connect(); folder = ((dynamic)service).GetFolder(@"\"); return action(folder); }
        finally { if (folder != null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    internal static bool IsEnabled() => WithFolder<bool>(folder => {
        object? task = null;
        try { task = folder.GetTask(TaskName); return (bool)((dynamic)task).Enabled; }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { return false; }
        finally { if (task != null) Marshal.FinalReleaseComObject(task); }
    });
    internal static void SetEnabled(bool enabled, string expectedSid)
    {
        if (expectedSid != UserSid) throw new InvalidOperationException("请使用当前 Windows 账户提权，不能使用其他账户配置自启动。");
        WithFolder<int>(folder => {
            if (enabled) {
                object task = folder.RegisterTask(TaskName, CreateXml(Environment.ProcessPath!, UserSid), 6, UserSid, null, 3, null);
                Marshal.FinalReleaseComObject(task);
            } else {
                try { folder.DeleteTask(TaskName, 0); }
                catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { }
            }
            return 0;
        });
        if (IsEnabled() != enabled) throw new InvalidOperationException("自启动状态校验失败。");
    }

    // Isolated, disabled task: validates the real scheduler without enabling user autostart.
    internal static void VerifyScheduler()
    {
        WithFolder<int>(folder => {
            string name = "LcdWidget.Verification." + Guid.NewGuid().ToString("N");
            bool created = false;
            try {
                object task = folder.RegisterTask(name, CreateXml(Environment.ProcessPath!, UserSid, false), 2, UserSid, null, 3, null);
                created = true;
                try {
                    dynamic registered = task;
                    if ((bool)registered.Enabled) throw new InvalidOperationException("测试任务必须禁用");
                    XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                    var xml = XElement.Parse((string)registered.Xml);
                    if (xml.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value != Environment.ProcessPath)
                        throw new InvalidOperationException("计划任务可执行文件路径不匹配");
                    registered.Enabled = true;
                    if (!(bool)registered.Enabled) throw new InvalidOperationException("无法启用测试任务");
                    registered.Enabled = false;
                }
                finally { Marshal.FinalReleaseComObject(task); }
            } finally { if (created) folder.DeleteTask(name, 0); }
            return 0;
        });
    }
}
