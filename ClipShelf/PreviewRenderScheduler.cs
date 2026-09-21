using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClipShelf;

// One STA worker for WPF text measurement and bitmap drawing. Priority is selected between bounded page jobs.
internal sealed class PreviewRenderScheduler : IDisposable
{
    internal static readonly AsyncLocal<int> Priority = new();
    private readonly object sync = new(); private readonly Queue<Action>[] queues = { new(), new(), new() };
    private readonly AutoResetEvent ready = new(false); private readonly Thread thread; private bool stopped;
    internal PreviewRenderScheduler() {
        thread = new Thread(Loop) { IsBackground = true, Name = "ClipShelf preview renderer", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    internal Task<T> Run<T>(Func<T> action, CancellationToken token, int priority = -1) {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) {
            if (stopped) return Task.FromCanceled<T>(new CancellationToken(true));
            queues[Math.Clamp(priority < 0 ? Priority.Value : priority, 0, 2)].Enqueue(() => {
                try { token.ThrowIfCancellationRequested(); var result = action(); token.ThrowIfCancellationRequested(); completion.TrySetResult(result); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); } catch (Exception error) { completion.TrySetException(error); }
            });
            ready.Set();
        }
        return completion.Task;
    }
    private void Loop() {
        try { while (true) { Action? next = null; lock (sync) { foreach (var queue in queues) if (queue.Count > 0) { next = queue.Dequeue(); break; } if (next is null && stopped) return; }
            if (next is null) ready.WaitOne(); else next(); } } finally { ready.Dispose(); }
    }
    public void Dispose() { lock (sync) { if (stopped) return; stopped = true; ready.Set(); } /* Never join a worker on the UI thread. */ }
}
