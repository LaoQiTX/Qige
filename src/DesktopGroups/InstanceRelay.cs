using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DesktopGroups;

// A current-user-only pipe asks the existing instance to show its controls.
// The file lock remains the authority for exclusive shortcut transfers.
internal sealed class InstanceRelay : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly string name;
    private readonly Action show;
    private readonly Task listener;
    public InstanceRelay(string root, Action show)
    {
        name = "DesktopGroups-" + StartupRegistration.ProjectId(root);
        this.show = show;
        listener = ListenAsync();
    }
    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(2000);
                var bytes = new byte[4];
                await pipe.ReadExactlyAsync(bytes, timeout.Token);
                if (Encoding.ASCII.GetString(bytes) == "show") show();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { if (!stop.IsCancellationRequested) await Task.Delay(100, stop.Token).ConfigureAwait(false); }
        }
    }
    public static async Task<bool> ShowExistingAsync(string root)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "DesktopGroups-" + StartupRegistration.ProjectId(root), PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(2500);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync("show"u8.ToArray(), timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose() { stop.Cancel(); /* CancelAsync callers may still be completing on the pool. */ }
}
