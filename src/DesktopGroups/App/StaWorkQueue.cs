using System.Collections.Concurrent;

namespace DesktopGroups;

// Shell COM requires an STA; one background queue also serializes file operations.
internal sealed class StaWorkQueue : IDisposable
{
    private readonly BlockingCollection<Action> work = new();
    private bool disposed;
    public StaWorkQueue(string name)
    {
        var thread = new Thread(() => { foreach (var action in work.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public Task<T> Run<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(StaWorkQueue)));
        try { work.Add(() => { try { result.SetResult(action()); } catch (Exception e) { result.SetException(e); } }); }
        catch (InvalidOperationException) { result.TrySetException(new ObjectDisposedException(nameof(StaWorkQueue))); }
        return result.Task;
    }
    public void Dispose() { disposed = true; work.CompleteAdding(); }
}
