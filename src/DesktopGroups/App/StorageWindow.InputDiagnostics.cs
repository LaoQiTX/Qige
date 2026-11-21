namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private readonly InputDiagnostics inputDiagnostics;

    private object InputState(DrawerRuntime runtime) => new
    {
        closing, maintenanceBusy, exitAfterOperations, demo, disposed,
        runtime.Busy, runtime.Definition.Removing, Kind = runtime.Definition.Kind.ToString(),
        runtime.Panel.IsAttached, runtime.Panel.InteractionBlocked,
        MappingTransferActive = activeMappedTransfer != null,
        Desktop = runtime.Row.Store.DesktopPath, runtime.Definition.MappedPath
    };

    private string? InputBlockedReason(DrawerRuntime runtime) => disposed ? "disposed" : closing ? "closing"
        : maintenanceBusy ? "maintenance" : exitAfterOperations ? "exit-pending"
        : runtime.Busy ? "drawer-busy" : runtime.Definition.Removing ? "drawer-removing"
        : !runtime.Panel.IsAttached ? "desktop-not-attached" : null;

    private static string InputRejectionMessage(string reason) => reason switch
    {
        "disposed" or "closing" or "exit-pending" => "程序正在退出，请稍后重新打开。",
        "maintenance" => "正在维护数据，请等待当前操作完成。",
        "drawer-busy" => "此抽屉正在处理文件，请稍后重试。",
        "drawer-removing" => "此抽屉正在移除，请选择其他抽屉。",
        "desktop-not-attached" => "抽屉尚未连接桌面，请在设置中重新连接桌面。",
        "not-desktop-drawer" => "请选择整理抽屉收纳桌面项目。",
        "mapping-unavailable" => "映射抽屉暂时无法接收文件，请等待当前传输完成并检查映射目录。",
        _ => reason
    };

    private bool CanAcceptPanelInput(DrawerRuntime runtime, IReadOnlyList<string> paths)
    {
        var reason = demo ? "样例内容仅在样例目录内使用。" : InputBlockedReason(runtime);
        try
        {
            if (reason == null && runtime.Definition.Kind == DrawerKind.FolderMapping)
                reason = CanTransferIntoMapping(runtime, paths) ? null : "mapping-unavailable";
            else if (reason == null)
            {
                var plan = DesktopInputPlan.Create(runtime.Row.Store, paths);
                catalog.EnsureOwnership(runtime.Definition.Id, plan.Managed);
            }
        }
        catch (Exception error) when (error is System.IO.IOException or ArgumentException or UnauthorizedAccessException)
        {
            reason = error.Message;
            inputDiagnostics.Write("input.validation-error", new { OperationId = runtime.Panel.DropOperationId,
                Drawer = runtime.Definition.Id, Error = error.ToString() });
        }
        inputDiagnostics.Write("input.validation", new { OperationId = runtime.Panel.DropOperationId,
            Drawer = runtime.Definition.Id, Source = "ole", Accepted = reason == null, Reason = reason,
            Paths = InputDiagnostics.Paths(paths), State = InputState(runtime) });
        if (reason != null) Status("拖入未接受：" + InputRejectionMessage(reason));
        return reason == null;
    }
}
