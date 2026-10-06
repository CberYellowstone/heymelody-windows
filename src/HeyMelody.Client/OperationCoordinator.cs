namespace HeyMelodyWindows.App;

/// <summary>Serializes complete RPCs; foreground work runs before the next polling section.</summary>
internal sealed class OperationCoordinator
{
    internal const int Lifecycle = 0, User = 1, Refresh = 2;
    private readonly PriorityQueue<Work, (int Priority, long Order)> _queue = new();
    private readonly object _sync = new();
    private long _order;
    private bool _running;
    private TaskCompletionSource _idle = Completed();
    public Task WhenIdle { get { lock (_sync) return _idle.Task; } }

    public Task RunAsync(Func<Task> action, int priority, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var work = new Work(action, priority, cancellation);
        var start = false;
        lock (_sync)
        {
            _queue.Enqueue(work, (priority, ++_order));
            if (!_running)
            { _running = true; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); start = true; }
        }
        if (start) _ = DrainAsync();
        return work.Completion.Task;
    }

    public void CancelQueuedWrites()
    {
        lock (_sync)
        {
            var retained = new List<(Work Work, (int Priority, long Order) Key)>();
            while (_queue.TryDequeue(out var work, out var key))
            {
                if (work.Priority == User) work.Completion.TrySetCanceled();
                else retained.Add((work, key));
            }
            foreach (var item in retained) _queue.Enqueue(item.Work, item.Key);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Work work;
            lock (_sync)
            {
                if (!_queue.TryDequeue(out work!, out _))
                { _running = false; _idle.TrySetResult(); return; }
            }
            try { work.Cancellation.ThrowIfCancellationRequested(); await work.Action(); work.Completion.TrySetResult(); }
            catch (OperationCanceledException) { work.Completion.TrySetCanceled(); }
            catch (Exception error) { work.Completion.TrySetException(error); }
        }
    }

    private sealed class Work(Func<Task> action, int priority, CancellationToken cancellation)
    {
        public Func<Task> Action { get; } = action;
        public int Priority { get; } = priority;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(); completion.SetResult(); return completion;
    }
}
