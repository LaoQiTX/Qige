using System.IO;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal static class ShortcutIconCheck
{
    public static int Run(AppStorage storage)
    {
        var relative = $"checks/ShortcutIcons/{Guid.NewGuid():N}";
        var desktop = storage.Resolve(relative + "/Desktop");
        var collected = storage.Resolve(relative + "/Storage");
        Directory.CreateDirectory(desktop); Directory.CreateDirectory(collected);
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        var results = new List<string>();
        var plain = Path.Combine(desktop, "自动图标.lnk");
        CreateLink(plain, target, null);
        var copied = Path.Combine(collected, Path.GetFileName(plain)); File.Copy(plain, copied);
        var original = ShortcutIcon.Load(plain, plain);
        var moved = ShortcutIcon.Load(copied, plain);
        Check(original.Source == "TargetResource" && moved.Source == "TargetResource" && Same(original.Bitmap, moved.Bitmap), "default target icon remains identical after relocation");
        var ico = Path.Combine(desktop, "自定义.ico");
        SaveIco(ico, original.Bitmap!);
        var custom = Path.Combine(desktop, "相对图标.lnk"); CreateLink(custom, target, "自定义.ico,0");
        var customCopy = Path.Combine(collected, Path.GetFileName(custom)); File.Copy(custom, customCopy);
        var customIcon = ShortcutIcon.Load(customCopy, custom);
        Check(customIcon.Source == "ExplicitIcon" && Same(original.Bitmap, customIcon.Bitmap), "relative custom icon resolves from original desktop after relocation");
        var broken = Path.Combine(desktop, "图标失效.lnk"); CreateLink(broken, target, Path.Combine(desktop, "missing.ico") + ",0");
        Check(ShortcutIcon.Load(broken).Source == "TargetResource", "missing explicit icon falls back to executable resource");
        var axolotl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Axolotl Launcher.lnk");
        var actualChecked = false;
        if (File.Exists(axolotl))
        {
            var hash = VerifiedTransfer.Hash(axolotl);
            var copy = Path.Combine(collected, "Axolotl Launcher.lnk"); File.Copy(axolotl, copy);
            var before = ShortcutIcon.Load(axolotl, axolotl);
            var after = ShortcutIcon.Load(copy, axolotl);
            Check(after.Source == "TargetResource" && Same(before.Bitmap, after.Bitmap), "Axolotl Launcher uses its executable icon before and after relocation");
            Check(VerifiedTransfer.Hash(axolotl) == hash, "actual Axolotl desktop shortcut remains unchanged");
            SavePng(storage.Resolve("checks/ShortcutIcons/axolotl.png"), after.Bitmap!);
            actualChecked = true;
        }
        storage.WriteJson("checks/ShortcutIcons/latest.json", new { Results = results, ActualAxolotlChecked = actualChecked, ActualDesktopModified = false });
        Console.WriteLine(string.Join(Environment.NewLine, results));
        return 0;

        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            results.Add("PASS " + label);
        }
    }

    private static bool Same(BitmapSource? first, BitmapSource? second)
    {
        if (first == null || second == null || first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return false;
        var stride = (first.PixelWidth * first.Format.BitsPerPixel + 7) / 8;
        var a = new byte[stride * first.PixelHeight]; var b = new byte[a.Length];
        first.CopyPixels(a, stride, 0); second.CopyPixels(b, stride, 0);
        return a.SequenceEqual(b);
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path); encoder.Save(output);
    }

    private static void SaveIco(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream(); encoder.Save(png);
        using var output = new BinaryWriter(File.Create(path));
        output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)1);
        output.Write((byte)bitmap.PixelWidth); output.Write((byte)bitmap.PixelHeight); output.Write((byte)0); output.Write((byte)0);
        output.Write((ushort)1); output.Write((ushort)32); output.Write((uint)png.Length); output.Write(22u); output.Write(png.ToArray());
    }

    private static void CreateLink(string path, string target, string? icon)
    {
        var comma = icon?.LastIndexOf(',') ?? -1;
        var iconPath = comma >= 0 ? icon![..comma] : icon;
        var iconIndex = comma >= 0 ? int.Parse(icon![(comma + 1)..]) : 0;
        NativeShortcut.Write(path, target, iconPath: iconPath, iconIndex: iconIndex);
    }
}
