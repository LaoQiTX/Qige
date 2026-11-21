using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DesktopGroups;

// Disk access stays off the OLE/UI thread. Diagnostics must never reject a file operation.
internal sealed class InputDiagnostics : IDisposable
{
    internal const long MaximumBytes = 2 * 1024 * 1024;
    private readonly Channel<string> pending = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task writer;
    private readonly string session = Guid.NewGuid().ToString("N");
    private long sequence;

    public InputDiagnostics(AppStorage storage)
    {
        writer = Task.Run(async () =>
        {
            await foreach (var line in pending.Reader.ReadAllAsync())
            {
                try
                {
                    var path = storage.Resolve("logs/input-current.jsonl");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                        File.Move(path, storage.Resolve("logs/input-previous.jsonl"), overwrite: true);
                    await File.AppendAllTextAsync(path, line, new UTF8Encoding(false));
                }
                catch { /* An unwritable log must not affect desktop files or OLE callbacks. */ }
            }
        });
    }

    public void Write(string stage, object detail)
    {
        try
        {
            var time = DateTimeOffset.Now;
            var number = Interlocked.Increment(ref sequence);
            var line = JsonSerializer.Serialize(new { Time = time, Session = session, Sequence = number,
                Process = Environment.ProcessId, Stage = stage, Detail = detail });
            if (Encoding.UTF8.GetByteCount(line) > 64 * 1024)
                line = JsonSerializer.Serialize(new { Time = time, Session = session, Sequence = number,
                    Process = Environment.ProcessId, Stage = stage,
                    Detail = new { Truncated = true, Preview = line[..Math.Min(line.Length, 4096)] } });
            pending.Writer.TryWrite(line + "\n");
        }
        catch { /* Diagnostic payload failures must not escape into the application. */ }
    }

    internal static object Paths(IReadOnlyList<string> paths) => new
    {
        Count = paths.Count, Sample = paths.Take(32).ToArray(), Truncated = paths.Count > 32
    };

    public void Dispose()
    {
        pending.Writer.TryComplete();
        try { writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
