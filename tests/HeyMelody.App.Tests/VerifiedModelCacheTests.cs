using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.App;

/// <summary>Exercises sharing, source lifetime and eviction using complete, publicly validated GLBs.</summary>
internal static class VerifiedModelCacheTests
{
    internal static async Task<int> RunAsync(string output)
    {
        int checks = 0;
        await SharedReadAsync();
        await FailureAndRetryAsync();
        await LruAsync();
        await UndeclaredDigestAsync();
        return checks;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }

        ArtworkFile FileFor(FilamentModelData model, string name, Func<IDisposable>? acquire = null)
            => new(new Uri(Path.GetFullPath(Path.Combine(output, name + ".glb"))), "", model.Sha256, acquire);

        async Task SharedReadAsync()
        {
            var model = Model("shared");
            int reads = 0, leases = 0;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var cache = new VerifiedModelCache(2, _ =>
            {
                Interlocked.Increment(ref reads);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Shared reader was not released.");
                return model;
            });
            var file = FileFor(model, "shared-first", () =>
            {
                Interlocked.Increment(ref leases);
                return new Lease(() => Interlocked.Decrement(ref leases));
            });
            Task<FilamentModelData>? first = null;
            Task<FilamentModelData>[] joined = [];
            try
            {
                first = cache.GetAsync(file, out var firstHit);
                Check(entered.Wait(TimeSpan.FromSeconds(5)) && !firstHit,
                    "A cache miss starts its source reader and is not reported as an LRU hit.");
                // A different URI and uppercase spelling still identify the same verified bytes.
                var alias = file with { Source = FileFor(model, "shared-alias").Source, Sha256 = model.Sha256.ToUpperInvariant() };
                var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                {
                    var task = cache.GetAsync(alias, out var hit);
                    return (Task: task, Hit: hit);
                })).ToArray();
                var requests = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));
                joined = requests.Select(request => request.Task).ToArray();
                Check(requests.All(request => ReferenceEquals(first, request.Task) && !request.Hit)
                    && Volatile.Read(ref reads) == 1,
                    "Concurrent callers for one digest join the same task and perform only one read.");
                Check(Volatile.Read(ref leases) == 1 && cache.InFlightCount == 1 && cache.Count == 0,
                    "The in-flight reader keeps one source lease and no completed cache entry.");
                release.Set();
                var firstModel = await first.WaitAsync(TimeSpan.FromSeconds(5));
                var results = await Task.WhenAll(joined).WaitAsync(TimeSpan.FromSeconds(5));
                Check(results.All(result => ReferenceEquals(result, firstModel) && result.Bytes.Equals(firstModel.Bytes)),
                    "Joined readers reuse the validated model and its original backing bytes.");
                await UntilAsync(() => Volatile.Read(ref leases) == 0 && cache.InFlightCount == 0);
                Check(cache.Count == 1 && Volatile.Read(ref leases) == 0,
                    "Completing a shared read releases its lease and retains exactly one LRU entry.");
                var cached = await cache.GetAsync(alias, out var cacheHit).WaitAsync(TimeSpan.FromSeconds(5));
                Check(cacheHit && ReferenceEquals(cached, firstModel) && Volatile.Read(ref reads) == 1 && Volatile.Read(ref leases) == 0,
                    "An LRU hit reuses the same bytes without rereading or reacquiring a source lease.");
            }
            finally
            {
                release.Set();
                if (first is not null) await ObserveAsync(first);
                foreach (var task in joined) await ObserveAsync(task);
            }
        }

        async Task FailureAndRetryAsync()
        {
            var model = Model("retry");
            int reads = 0, leases = 0;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var cache = new VerifiedModelCache(2, _ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Failing reader was not released.");
                    throw new InvalidDataException("Expected source verification failure.");
                }
                return model;
            });
            var file = FileFor(model, "retry", () =>
            {
                Interlocked.Increment(ref leases);
                return new Lease(() => Interlocked.Decrement(ref leases));
            });
            Task<FilamentModelData>? first = null;
            try
            {
                first = cache.GetAsync(file, out _);
                Check(entered.Wait(TimeSpan.FromSeconds(5)) && Volatile.Read(ref leases) == 1,
                    "A blocked read retains its source lease before a verification failure.");
                var joined = cache.GetAsync(file, out _);
                Check(ReferenceEquals(first, joined) && cache.InFlightCount == 1,
                    "A caller joining a failing read shares the existing task.");
                release.Set();
                bool failed = false;
                try { await first.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidDataException error) when (error.Message == "Expected source verification failure.") { failed = true; }
                Check(failed && joined.IsFaulted, "Every waiter observes the shared source verification failure.");
                await UntilAsync(() => Volatile.Read(ref leases) == 0 && cache.InFlightCount == 0);
                Check(cache.Count == 0 && Volatile.Read(ref leases) == 0,
                    "A failed read clears its in-flight entry and source lease without populating the LRU.");
                var retried = await cache.GetAsync(file, out var hit).WaitAsync(TimeSpan.FromSeconds(5));
                await UntilAsync(() => Volatile.Read(ref leases) == 0 && cache.InFlightCount == 0);
                Check(!hit && ReferenceEquals(retried, model) && Volatile.Read(ref reads) == 2 && cache.Count == 1,
                    "The next request retries a failed digest and can populate the cache successfully.");
            }
            finally
            {
                release.Set();
                if (first is not null) await ObserveAsync(first);
            }
        }

        async Task LruAsync()
        {
            var models = new[] { Model("lru-a"), Model("lru-b"), Model("lru-c") };
            var byHash = models.ToDictionary(model => model.Sha256, StringComparer.OrdinalIgnoreCase);
            var reads = models.ToDictionary(model => model.Sha256, _ => 0, StringComparer.OrdinalIgnoreCase);
            var cache = new VerifiedModelCache(2, file =>
            {
                lock (reads) reads[file.Sha256]++;
                return byHash[file.Sha256];
            });
            var files = models.Select((model, index) => FileFor(model, "lru-" + index)).ToArray();
            await cache.GetAsync(files[0], out _).WaitAsync(TimeSpan.FromSeconds(5));
            await cache.GetAsync(files[1], out _).WaitAsync(TimeSpan.FromSeconds(5));
            Check(cache.Count == 2, "The model LRU retains its configured capacity of two.");
            var alias = files[0] with { Source = FileFor(models[0], "lru-a-alias").Source, Sha256 = models[0].Sha256.ToUpperInvariant() };
            for (int index = 0; index < 3; index++)
            {
                var cached = await cache.GetAsync(alias, out var hit).WaitAsync(TimeSpan.FromSeconds(5));
                Check(hit && ReferenceEquals(cached, models[0]) && cache.Count == 2,
                    "Repeated digest aliases update LRU recency without inserting duplicate entries (" + index + ").");
            }
            await cache.GetAsync(files[2], out _).WaitAsync(TimeSpan.FromSeconds(5));
            var kept = await cache.GetAsync(files[0], out var keptHit).WaitAsync(TimeSpan.FromSeconds(5));
            Check(keptHit && ReferenceEquals(kept, models[0]) && reads[models[0].Sha256] == 1 && cache.Count == 2,
                "Adding a third digest retains the recently touched first model.");
            await cache.GetAsync(files[1], out var evictedHit).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!evictedHit && reads[models[1].Sha256] == 2 && cache.Count == 2,
                "The least recently used second model is evicted and must be reread.");
            await cache.GetAsync(files[2], out var thirdHit).WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => cache.InFlightCount == 0);
            Check(!thirdHit && reads[models[2].Sha256] == 2 && cache.Count == 2,
                "LRU recency remains correct after reloading an evicted digest.");
        }

        async Task UndeclaredDigestAsync()
        {
            var firstModel = Model("unhashed-first");
            var secondModel = Model("unhashed-second");
            int reads = 0, leases = 0;
            using var entered = new CountdownEvent(2);
            using var release = new ManualResetEventSlim();
            var cache = new VerifiedModelCache(2, _ =>
            {
                int sequence = Interlocked.Increment(ref reads);
                if (sequence <= 2) entered.Signal();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Undeclared-digest readers were not released.");
                return sequence == 2 ? secondModel : firstModel;
            });
            var file = FileFor(firstModel, "same-unhashed-path", () =>
            {
                Interlocked.Increment(ref leases);
                return new Lease(() => Interlocked.Decrement(ref leases));
            }) with { Sha256 = "" };
            Task<FilamentModelData>? first = null, second = null;
            try
            {
                first = cache.GetAsync(file, out var firstHit);
                second = cache.GetAsync(file, out var secondHit);
                Check(entered.Wait(TimeSpan.FromSeconds(5)) && !firstHit && !secondHit
                    && !ReferenceEquals(first, second) && Volatile.Read(ref reads) == 2 && Volatile.Read(ref leases) == 2,
                    "An undeclared digest never merges concurrent reads merely because their source paths match.");
                release.Set();
                var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
                await UntilAsync(() => Volatile.Read(ref leases) == 0 && cache.InFlightCount == 0);
                Check(results.Select(model => model.Sha256).Distinct().Count() == 2 && cache.Count == 0,
                    "Unhashed reads can observe changed source bytes and do not retain either derived digest in the LRU.");
                await cache.GetAsync(file, out var thirdHit).WaitAsync(TimeSpan.FromSeconds(5));
                await UntilAsync(() => Volatile.Read(ref leases) == 0 && cache.InFlightCount == 0);
                Check(!thirdHit && Volatile.Read(ref reads) == 3 && cache.Count == 0,
                    "Later requests without an expected digest reread the source and release every lease.");
            }
            finally
            {
                release.Set();
                if (first is not null) await ObserveAsync(first);
                if (second is not null) await ObserveAsync(second);
            }
        }
    }

    private static FilamentModelData Model(string marker)
    {
        // A complete JSON-only glTF 2.0 binary: the scene is valid and intentionally empty.
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\",\"generator\":\"" + marker
            + "\"},\"scene\":0,\"scenes\":[{}]}");
        int paddedLength = (json.Length + 3) & ~3;
        byte[] bytes = new byte[20 + paddedLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)bytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)paddedLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4e4f534a);
        bytes.AsSpan(20).Fill(0x20);
        json.CopyTo(bytes, 20);
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return FilamentModelData.CreateValidated(bytes, digest);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Cache cleanup did not finish.");
            await Task.Delay(10);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
