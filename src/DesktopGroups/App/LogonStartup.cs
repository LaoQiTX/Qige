using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace DesktopGroups;

// A per-user interactive logon task avoids Explorer's delayed Run queue.
// No elevation, stored password, global startup-delay changes or helper process.
internal sealed class LogonStartup(string executable, string dataRoot, string command) : IStartupValue
{
    private readonly string user = WindowsIdentity.GetCurrent().User!.Value;
    private string TaskName(string name) => name + "-" + StartupRegistration.ProjectId(user);
    private string Arguments => $"--data-dir \"{dataRoot}\" --background";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public string? Read(string name)
    {
        try
        {
            return WithFolder(folder =>
            {
                object? task = null;
                try
                {
                    try { task = folder.GetTask(TaskName(name)); }
                    catch (Exception e) when ((uint)e.HResult == 0x80070002) { return null; }
                    dynamic registered = task;
                    if (!Matches((string)registered.Xml)) return "modified logon task";
                    return (bool)registered.Enabled ? command : null;
                }
                finally { Release(task); }
            });
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or NotSupportedException)
        { return null; }
    }

    private bool Matches(string xml)
    {
        var root = XElement.Parse(xml);
        var actions = root.Element(Ns + "Actions")?.Elements().ToArray();
        return root.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source")?.Value == "Qige"
            && actions is { Length: 1 } && actions[0].Name == Ns + "Exec"
            && actions[0].Element(Ns + "Command")?.Value == executable
            && actions[0].Element(Ns + "Arguments")?.Value == Arguments;
    }

    public void Write(string name, string value)
    {
        if (value != command) throw new ArgumentException("登录任务命令不匹配。");
        if (Read(name) is { } current && current != command) throw new System.IO.IOException("登录任务已被修改。");
        WithFolder<object?>(folder =>
        {
            var xml = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Source", "Qige"),
                    new XElement(Ns + "Description", "栖格 Qige · 登录后立即恢复桌面抽屉")),
                new XElement(Ns + "Triggers", new XElement(Ns + "LogonTrigger",
                    new XElement(Ns + "Enabled", true), new XElement(Ns + "UserId", user))),
                new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "User"),
                    new XElement(Ns + "UserId", user), new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", false), new XElement(Ns + "StopIfGoingOnBatteries", false),
                    new XElement(Ns + "StartWhenAvailable", true), new XElement(Ns + "ExecutionTimeLimit", "PT0S")),
                new XElement(Ns + "Actions", new XAttribute("Context", "User"), new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", executable), new XElement(Ns + "Arguments", Arguments),
                    new XElement(Ns + "WorkingDirectory", System.IO.Path.GetDirectoryName(executable)))));
            object registered = folder.RegisterTask(TaskName(name), xml.ToString(), 6, user, null, 3, null);
            Release(registered);
            return null;
        });
    }

    public void Remove(string name) => WithFolder<object?>(folder =>
    {
        object? task = null;
        try
        {
            try { task = folder.GetTask(TaskName(name)); }
            catch (Exception e) when ((uint)e.HResult == 0x80070002) { return null; }
            if (!Matches((string)((dynamic)task).Xml)) throw new System.IO.IOException("登录任务已被修改，未删除。");
            folder.DeleteTask(TaskName(name), 0);
            return null;
        }
        finally { Release(task); }
    });

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            return action(folder!);
        }
        finally { Release(folder); Release(service); }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
