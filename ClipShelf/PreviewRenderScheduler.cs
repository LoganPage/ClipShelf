using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClipShelf;

// One STA worker for WPF text measurement and bitmap drawing. Priority is selected between bounded page jobs.
internal sealed class PreviewRenderScheduler : IDisposable
{
    internal static readonly AsyncLocal<int> Priority = new();
    private readonly object sync = new(); private readonly LinkedList<Job>[] queues = { new(), new(), new() };
    private readonly AutoResetEvent ready = new(false); private readonly Thread thread; private bool stopped;
    private sealed class Job(Action run, Action cancel) {
        internal readonly Action Run = run, Cancel = cancel;
        internal LinkedListNode<Job>? Node;
        internal CancellationTokenRegistration Registration;
    }
    internal int QueuedCount { get { lock (sync) { int count = 0; foreach (var queue in queues) count += queue.Count; return count; } } }
    internal PreviewRenderScheduler() {
        thread = new Thread(Loop) { IsBackground = true, Name = "ClipShelf preview renderer", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    internal Task<T> Run<T>(Func<T> action, CancellationToken token, int priority = -1) {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new Job(() => {
                try { token.ThrowIfCancellationRequested(); var result = action(); token.ThrowIfCancellationRequested(); completion.TrySetResult(result); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); } catch (Exception error) { completion.TrySetException(error); }
            }, () => completion.TrySetCanceled());
        lock (sync) {
            if (stopped) return Task.FromCanceled<T>(new CancellationToken(true));
            if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
            job.Node = queues[Math.Clamp(priority < 0 ? Priority.Value : priority, 0, 2)].AddLast(job);
            if (token.CanBeCanceled) job.Registration = token.Register(() => {
                lock (sync) { if (job.Node is null) return; job.Node.List?.Remove(job.Node); job.Node = null; }
                job.Cancel();
            });
            ready.Set();
        }
        _ = completion.Task.ContinueWith(_ => job.Registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }
    private void Loop() {
        try { while (true) { Job? next = null; lock (sync) { foreach (var queue in queues) if (queue.Count > 0) { next = queue.First!.Value; queue.RemoveFirst(); next.Node = null; break; } if (next is null && stopped) return; }
            if (next is null) ready.WaitOne(); else next.Run(); } } finally { ready.Dispose(); }
    }
    public void Dispose() {
        var abandoned = new List<Job>();
        lock (sync) { if (stopped) return; stopped = true; foreach (var queue in queues) { foreach (var job in queue) { job.Node = null; abandoned.Add(job); } queue.Clear(); } ready.Set(); }
        foreach (var job in abandoned) job.Cancel();
        /* Never join a worker on the UI thread. */
    }
}
