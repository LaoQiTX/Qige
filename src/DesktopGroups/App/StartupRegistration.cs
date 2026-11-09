using System.IO;
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
    public string ValueName { get; }
    public string Command { get; }
    public bool Enabled => registry.Read(ValueName) == Command;

    public StartupRegistration(AppStorage storage, string? executable = null, IStartupValue? registry = null)
    {
        this.registry = registry ?? new CurrentUserStartup();
        executable = Path.GetFullPath(executable ?? AppLaunch.Executable);
        if (!executable.StartsWith(storage.InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("开机启动只能指向安装目录内的程序。");
        ValueName = "Qige-" + ProjectId(storage.Root);
        Command = $"\"{executable}\" --data-dir \"{storage.Root}\" --background";
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
            if (registry.Read(name) != oldCommand) continue;
            var current = registry.Read(ValueName);
            if (current != null && current != Command) throw new IOException("新启动入口已被修改，旧启动入口已保留。");
            registry.Write(ValueName, Command);
            registry.Remove(name);
        }
    }

    public void SetEnabled(bool enabled)
    {
        var existing = registry.Read(ValueName);
        if (existing != null && existing != Command)
            throw new IOException("本项目的启动入口已被其他内容修改，未覆盖；请先检查系统启动设置。");
        if (enabled) registry.Write(ValueName, Command);
        else if (existing != null) registry.Remove(ValueName);
    }

    internal static string ProjectId(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..12];

    private sealed class CurrentUserStartup : IStartupValue
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public string? Read(string name) { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(name) as string; }
        public void Write(string name, string value) { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue(name, value, RegistryValueKind.String); }
        public void Remove(string name) { using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true); key?.DeleteValue(name, throwOnMissingValue: false); }
    }
}
