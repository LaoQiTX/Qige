using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal sealed class AsyncIconCache : IDisposable
{
    private readonly StaWorkQueue worker = new("Qige icons");
    private readonly Dictionary<string, BitmapSource?> cache = [];
    private readonly Dictionary<string, Task<BitmapSource?>> pending = [];
    private readonly Queue<string> order = new();
    private readonly object gate = new();
    private readonly Func<string, string?, BitmapSource?> loader;
    public long Hits { get; private set; }
    public long Loads { get; private set; }
    public double TotalMilliseconds { get; private set; }
    public double MaxMilliseconds { get; private set; }
    public int Count { get { lock (gate) return cache.Count; } }
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public AsyncIconCache(Func<string, string?, BitmapSource?>? loader = null)
        => this.loader = loader ?? ((path, original) => ShortcutIcon.Load(path, original).Bitmap);
    public bool TryGet(string key, out BitmapSource? value) { lock (gate) return cache.TryGetValue(key, out value); }
    public Task<BitmapSource?> LoadAsync(string key, string path, string? original)
    {
        lock (gate)
        {
            if (cache.TryGetValue(key, out var value)) { Hits++; return Task.FromResult(value); }
            if (pending.TryGetValue(key, out var task)) { Hits++; return task; }
            var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(key, completion.Task);
            _ = Complete();
            return completion.Task;
            async Task Complete()
            {
                BitmapSource? bitmap = null;
                var clock = Stopwatch.StartNew();
                try { bitmap = await worker.Run(() => { var image = loader(path, original); image?.Freeze(); return image; }); }
                catch (Exception) { /* Optional shell icons never prevent recovery or opening a drawer. */ }
                lock (gate)
                {
                    Loads++; TotalMilliseconds += clock.Elapsed.TotalMilliseconds;
                    MaxMilliseconds = Math.Max(MaxMilliseconds, clock.Elapsed.TotalMilliseconds);
                    cache[key] = bitmap; order.Enqueue(key); pending.Remove(key);
                    while (cache.Count > 256) cache.Remove(order.Dequeue());
                }
                completion.TrySetResult(bitmap);
            }
        }
    }
    public object Metrics() { lock (gate) return new { Hits, Loads, Cached = cache.Count, Pending = pending.Count,
        AverageMs = Loads == 0 ? 0 : TotalMilliseconds / Loads, MaxMilliseconds }; }
    public void Dispose() => worker.Dispose();
}
