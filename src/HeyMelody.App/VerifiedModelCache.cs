using HeyMelody.Rendering.Filament;

namespace HeyMelodyWindows.App;

/// <summary>Shares original model bytes during a read, then retains only a bounded LRU of verified digests.</summary>
internal sealed class VerifiedModelCache(int capacity, Func<ArtworkFile, FilamentModelData> read)
{
    private readonly object _gate = new();
    private readonly LinkedList<FilamentModelData> _models = new();
    private readonly Dictionary<string, Task<FilamentModelData>> _inFlight = new(StringComparer.Ordinal);

    internal int Count { get { lock (_gate) return _models.Count; } }
    internal int InFlightCount { get { lock (_gate) return _inFlight.Count; } }

    internal Task<FilamentModelData> GetAsync(ArtworkFile file, out bool cacheHit)
    {
        var expected = string.IsNullOrEmpty(file.Sha256) ? null : file.Sha256.ToLowerInvariant();
        cacheHit = false;
        lock (_gate)
        {
            if (expected is not null)
            {
                for (var node = _models.First; node is not null; node = node.Next)
                    if (node.Value.Sha256 == expected)
                    {
                        _models.Remove(node);
                        _models.AddFirst(node);
                        cacheHit = true;
                        return Task.FromResult(node.Value);
                    }
                if (_inFlight.TryGetValue(expected, out var pending))
                {
                    if (!pending.IsCompleted) return pending;
                    _ = pending.Exception;
                    _inFlight.Remove(expected);
                }
            }
            // The reader owns this reference even if every waiting view detaches or cancels.
            var lease = file.Acquire();
            Task<FilamentModelData> task;
            try
            {
                task = Task.Run(() =>
                {
                    using (lease)
                    {
                        var model = read(file);
                        if (expected is not null)
                        {
                            if (!string.Equals(model.Sha256, expected, StringComparison.Ordinal))
                                throw new InvalidDataException("Model integrity check failed.");
                            lock (_gate)
                            {
                                _models.AddFirst(model);
                                while (_models.Count > capacity) _models.RemoveLast();
                            }
                        }
                        return model;
                    }
                });
            }
            catch { lease?.Dispose(); throw; }
            if (expected is not null) _inFlight.Add(expected, task);
            _ = task.ContinueWith(completed =>
            {
                // Prefetch callers may already have gone away; faults must always be observed.
                _ = completed.Exception;
                if (expected is null) return;
                lock (_gate)
                    if (_inFlight.TryGetValue(expected, out var current) && ReferenceEquals(current, completed))
                        _inFlight.Remove(expected);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
}
