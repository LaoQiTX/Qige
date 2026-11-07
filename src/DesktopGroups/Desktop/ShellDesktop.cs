using System.Runtime.InteropServices;

namespace DesktopGroups;

// Inspection is read-only. Positioning is used only by the explicit interactive test.
internal static class ShellDesktop
{
    public static ShellViewInfo Inspect()
    {
        try
        {
            return WithView(folderView =>
            {
                Marshal.ThrowExceptionForHR(folderView.ItemCount(2, out var count));
                Marshal.ThrowExceptionForHR(folderView.GetSpacing(out var spacing));
                Marshal.ThrowExceptionForHR(folderView.GetCurrentViewMode(out var mode));
                var result = folderView.GetAutoArrange();
                bool? autoArrange = result == 0 ? true : result == 1 ? false : null;
                return new ShellViewInfo(true, count, mode, new(spacing.X, spacing.Y), autoArrange, null);
            });
        }
        catch (Exception exception)
        {
            return new(false, null, null, null, null, $"{exception.GetType().Name} (0x{exception.HResult:X8}): {exception.Message}");
        }
    }

    internal static T WithView<T>(Func<IFolderViewNative, T> action)
    {
        object? windows = null, desktop = null, browser = null, view = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            windows = Activator.CreateInstance(type)!;
            object location = 0, root = 0;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out int _, 1);
            if (desktop == null) throw new InvalidOperationException("Shell 未返回桌面自动化对象。");
            var provider = (IServiceProviderNative)desktop;
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserId = typeof(IShellBrowserNative).GUID;
            Marshal.ThrowExceptionForHR(provider.QueryService(ref service, ref browserId, out var pointer));
            try { browser = Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            Marshal.ThrowExceptionForHR(((IShellBrowserNative)browser).QueryActiveShellView(out view));
            return action((IFolderViewNative)view);
        }
        finally
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var item in new[] { view, browser, desktop, windows })
                if (item != null && Marshal.IsComObject(item) && seen.Add(item)) Marshal.ReleaseComObject(item);
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderNative
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid interfaceId, out nint result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserNative
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        [PreserveSig] int InsertMenusSB(nint sharedMenu, nint menuWidths);
        [PreserveSig] int SetMenuSB(nint sharedMenu, nint oleMenu, nint activeWindow);
        [PreserveSig] int RemoveMenusSB(nint sharedMenu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint idList, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint window);
        [PreserveSig] int SendControlMsg(uint id, uint message, nuint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderViewNative
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object folder);
        [PreserveSig] int Item(int index, out nint idList);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid interfaceId, out nint items);
        [PreserveSig] int GetSelectionMarkedItem(out int index);
        [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(nint idList, out NativeDesktop.Point point);
        [PreserveSig] int GetSpacing(out NativeDesktop.Point point);
        [PreserveSig] int GetDefaultSpacing(out NativeDesktop.Point point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int index, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] idLists,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativeDesktop.Point[] positions, uint flags);
    }
}

internal sealed record IconSpacing(int X, int Y);
internal sealed record ShellViewInfo(bool Available, int? ItemCount, uint? ViewMode,
    IconSpacing? Spacing, bool? AutoArrange, string? Error);
