using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DesktopGroups;

internal interface IStartupValue
{
    string? Read(string name);
    void Write(string name, string value);
    void Remove(string name);
}

internal sealed class StartupRegistration
{
    private readonly IStartupValue registry;
    private readonly IStartupValue? tasks;
    public string ValueName { get; }
    public string Command { get; }
    private bool RunDisabled(string name) => registry is CurrentUserStartup current && current.IsDisabled(name);
    public bool Enabled => tasks?.Read(ValueName) == Command || (registry.Read(ValueName) == Command && !RunDisabled(ValueName));

    public StartupRegistration(AppStorage storage, string? executable = null, IStartupValue? registry = null, IStartupValue? tasks = null)
    {
        this.registry = registry ?? new CurrentUserStartup();
        executable = Path.GetFullPath(executable ?? AppLaunch.Executable);
        if (!executable.StartsWith(storage.InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("开机启动只能指向安装目录内的程序。");
        ValueName = "Qige-" + ProjectId(storage.Root);
        Command = $"\"{executable}\" --data-dir \"{storage.Root}\" --background";
        this.tasks = tasks ?? (registry == null ? new LogonStartup(executable, storage.Root, Command) : null);
    }

    public void UpgradeLegacyRegistration(AppStorage storage)
    {
        var oldExecutable = Path.Combine(storage.InstallDirectory, "DesktopGroups.exe");
        var renamedCommand = $"\"{oldExecutable}\" --data-dir \"{storage.Root}\" --background";
        if (registry.Read(ValueName) == renamedCommand) registry.Write(ValueName, Command);

        foreach (var root in new[] { storage.LegacyRoot, storage.InstallDirectory }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = "DesktopGroups-" + ProjectId(root);
            var oldCommand = $"\"{oldExecutable}\" --root \"{root}\" --background";
            if (registry.Read(name) != oldCommand || RunDisabled(name)) continue;
            var current = registry.Read(ValueName);
            if (current != null && current != Command) throw new IOException("新启动入口已被修改，旧启动入口已保留。");
            registry.Write(ValueName, Command);
            registry.Remove(name);
        }
        // Only migrate an enabled, exactly matching entry. Never enable startup
        // for users who have not opted in or overwrite a modified command.
        if (registry.Read(ValueName) == Command && !RunDisabled(ValueName) && tasks != null) SetEnabled(true);
    }

    public void SetEnabled(bool enabled)
    {
        var existing = registry.Read(ValueName);
        if (existing != null && existing != Command)
            throw new IOException("本项目的启动入口已被其他内容修改，未覆盖；请先检查系统启动设置。");
        var task = tasks?.Read(ValueName);
        if (task != null && task != Command)
            throw new IOException("本项目的登录任务已被修改，未覆盖；请先检查任务计划程序。");
        if (enabled)
        {
            if (tasks != null)
            {
                try
                {
                    tasks.Write(ValueName, Command);
                    // Keep the old entry until the task is confirmed readable.
                    if (tasks.Read(ValueName) != Command) throw new COMException("无法确认登录任务。");
                    if (existing != null) registry.Remove(ValueName);
                    return;
                }
                catch (Exception e) when (e is COMException or UnauthorizedAccessException or NotSupportedException)
                { /* Restricted systems retain the per-user Run fallback. */ }
            }
            registry.Write(ValueName, Command);
        }
        else
        {
            try { tasks?.Remove(ValueName); }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException or NotSupportedException)
            {
                // A restricted scheduler must not prevent disabling the Run fallback.
                if (existing == null) throw;
            }
            if (existing != null) registry.Remove(ValueName);
        }
    }

    internal static string ProjectId(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..12];

    private sealed class CurrentUserStartup : IStartupValue
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public bool IsDisabled(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return key?.GetValue(name) is byte[] { Length: > 0 } state && state[0] is 3 or 7;
        }
        public string? Read(string name) { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(name) as string; }
        public void Write(string name, string value) { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue(name, value, RegistryValueKind.String); }
        public void Remove(string name) { using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true); key?.DeleteValue(name, throwOnMissingValue: false); }
    }
}
