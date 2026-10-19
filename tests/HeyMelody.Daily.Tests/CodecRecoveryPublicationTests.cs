using HeyMelodyWindows.Daily;

internal static class CodecRecoveryPublicationTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // Model Host's retirement rule without a real device: an offline snapshot
    // cancels the worker lifetime that also owns the in-flight read refresh.
    private sealed class RetirementObserver : IDisposable
    {
        private readonly DailyClient _client;
        private readonly CancellationTokenSource _lifetime = new();
        private int _offline, _connected, _disconnected;

        internal RetirementObserver(DailyClient client)
        {
            _client = client;
            client.SnapshotChanged += Observe;
            client.Disconnected += OnDisconnected;
        }

        internal CancellationToken Token => _lifetime.Token;
        internal bool Cancelled => _lifetime.IsCancellationRequested;
        internal int Offline => Volatile.Read(ref _offline);
        internal int Connected => Volatile.Read(ref _connected);
        internal int Disconnected => Volatile.Read(ref _disconnected);

        private void Observe(DailySnapshot snapshot)
        {
            if (snapshot.Connected) Interlocked.Increment(ref _connected);
            else
            {
                Interlocked.Increment(ref _offline);
                _lifetime.Cancel();
            }
        }

        private void OnDisconnected() => Interlocked.Increment(ref _disconnected);

        public void Dispose()
        {
            _client.SnapshotChanged -= Observe;
            _client.Disconnected -= OnDisconnected;
            _lifetime.Dispose();
        }
    }

    private enum ReaderFault { Io, Eof, DecodeNoise, UnsolicitedCancellation }

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        foreach (bool timeout in new[] { false, true })
        {
            await test(timeout
                ? "An active codec deadline recovers before Host can retire its worker"
                : "An active matched malformed codec reply recovers before Host can retire its worker", async () =>
            {
                var fake = new Fake();
                bool malformed = false;
                var connector = new CodecWireFixtureConnector(fake, sequence => malformed
                    ? CodecWireFixtureConnector.Air4ProV125Ack(sequence, 3)
                    : CodecWireFixtureConnector.Ack(sequence));
                await using var client = new DailyClient(connector);
                var initial = await client.RefreshAsync();
                Check(initial.Connected && initial.ProfileVerified && initial.CodecRaw == 8,
                    "The publication test must begin with an established normal connection.");
                await client.StartMonitoringAsync();
                using var host = new RetirementObserver(client);
                if (timeout) fake.Silent.Add(0x0114);
                else malformed = true;

                var recovered = await client.RefreshAsync(DailyRefreshScope.Information, host.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Check(recovered.Connected && recovered.ProfileVerified && recovered.Error is null
                    && recovered.CodecRaw is null && recovered.Warnings.Any(x => x.Contains("已跳过")),
                    "The eligible codec failure did not publish a usable recovered snapshot.");
                Check(host.Offline == 0 && host.Disconnected == 0 && !host.Cancelled && host.Connected > 0,
                    "Temporary codec recovery published offline and retired the active Host worker.");
                Check(fake.Opens == 2 && fake.MaximumReaders == 1 && fake.Writes.Count == 0
                    && fake.Requests.Count(x => x.Command == 0x0114) == 2,
                    "Recovery must use one fresh reader, suppress the new codec query and never submit SET.");

                var refreshed = await client.RefreshAsync(host.Token);
                Check(refreshed.Connected && refreshed.CodecRaw is null && host.Offline == 0 && !host.Cancelled
                    && fake.Opens == 2 && fake.Requests.Count(x => x.Command == 0x0114) == 2 && fake.Writes.Count == 0,
                    "A later full refresh lost the recovered session or codec suppression.");
            });
        }

        foreach (bool failOpen in new[] { false, true })
        {
            await test(failOpen
                ? "Failure opening the codec recovery connection publishes offline and retires Host"
                : "A required read failure on the codec recovery connection publishes offline and retires Host", async () =>
            {
                var fake = new Fake();
                bool malformed = false;
                int attempts = 0;
                var connector = new CodecWireFixtureConnector(fake, sequence => malformed
                    ? CodecWireFixtureConnector.Air4ProV125Ack(sequence, 3)
                    : CodecWireFixtureConnector.Ack(sequence), onOpen: number =>
                    {
                        attempts = number;
                        if (number != 2) return;
                        if (failOpen) throw new IOException("private recovery transport detail");
                        fake.ReadReplies[0x0106] = [];
                    });
                await using var client = new DailyClient(connector);
                Check((await client.RefreshAsync()).Connected, "The failure test needs a prior connected snapshot.");
                using var host = new RetirementObserver(client);
                malformed = true;

                var failed = await client.RefreshAsync(DailyRefreshScope.Information, host.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Check(!failed.Connected && failed.Error is not null && !failed.Error.Contains("private")
                    && host.Offline > 0 && host.Cancelled,
                    "A failed recovery must publish a sanitized offline result and cancel the Host worker.");
                Check(attempts == 2 && fake.Opens == (failOpen ? 1 : 2) && fake.Writes.Count == 0
                    && fake.Requests.Count(x => x.Command == 0x0114) == 2,
                    "A failed recovery must not allocate a third connection, query codec again or submit SET.");
            });
        }

        foreach (var fault in Enum.GetValues<ReaderFault>())
        {
            await test($"An active reader {fault} failure remains visible and retires Host without codec recovery", async () =>
            {
                var fake = new Fake();
                bool armed = false;
                var connector = new CodecWireFixtureConnector(fake, sequence => !armed
                    ? CodecWireFixtureConnector.Ack(sequence)
                    : fault switch
                    {
                        ReaderFault.Io => throw new IOException("private reader detail"),
                        ReaderFault.Eof => [],
                        ReaderFault.DecodeNoise => [0x01], // Invalid frame start, unrelated to a matched codec ACK.
                        ReaderFault.UnsolicitedCancellation => throw new OperationCanceledException("private reader detail"),
                        _ => throw new InvalidOperationException()
                    });
                await using var client = new DailyClient(connector);
                Check((await client.RefreshAsync()).Connected, "The reader fault test needs a prior connected snapshot.");
                await client.StartMonitoringAsync();
                using var host = new RetirementObserver(client);
                armed = true;

                var failed = await client.RefreshAsync(DailyRefreshScope.Information, host.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Check(!failed.Connected && failed.Error is not null && !failed.Error.Contains("private")
                    && host.Offline > 0 && host.Disconnected > 0 && host.Cancelled,
                    "A genuine reader failure was hidden behind codec recovery or omitted the offline event.");
                Check(fake.Opens == 1 && fake.Writes.Count == 0
                    && fake.Requests.Count(x => x.Command == 0x0114) == 2,
                    "Host retirement must stop reconnecting a genuine failed reader and must never submit SET.");
            });
        }
    }
}
