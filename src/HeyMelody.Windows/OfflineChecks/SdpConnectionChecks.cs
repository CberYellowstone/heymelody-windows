using System.ComponentModel;
using HeyMelodyWindows.Windows;

internal static class SdpConnectionChecks
{
    internal static async Task RunAsync(Guid[] serviceIds)
    {
        var cached = new RfcommService(serviceIds[0], 7, "Melody");
        var fresh = new RfcommService(serviceIds[0], 9, "Melody");
        var lookups = new List<(Guid Id, bool Refresh)>();
        var connections = new List<uint>();
        IReadOnlyList<RfcommService> Lookup(Guid id, bool refresh)
        {
            lookups.Add((id, refresh));
            return id == cached.ServiceId ? [refresh ? fresh : cached] : [];
        }
        Task<uint> Success(RfcommService service, CancellationToken token)
        {
            Check(token.CanBeCanceled, "Every native connection requires a deadline.");
            connections.Add(service.Channel);
            return Task.FromResult(service.Channel);
        }
        var channel = await RfcommConnection.ConnectControlCoreAsync(serviceIds, Lookup, Success, default);
        Check(channel == cached.Channel && connections.Count == 1 && lookups.All(call => !call.Refresh),
            "A successful cached connection must not query the peer.");

        lookups.Clear(); connections.Clear();
        Task<uint> Stale(RfcommService service, CancellationToken token)
        {
            connections.Add(service.Channel);
            return service == cached ? Task.FromException<uint>(new IOException("Synthetic stale endpoint."))
                : Task.FromResult(service.Channel);
        }
        channel = await RfcommConnection.ConnectControlCoreAsync(serviceIds, Lookup, Stale, default);
        Check(channel == fresh.Channel && connections.SequenceEqual(new[] { cached.Channel, fresh.Channel }),
            "A failed cached connection must use the fresh channel.");
        Check(lookups.Select(call => call.Refresh).SequenceEqual(new[] { false, false, true, true })
            && lookups.Select(call => call.Id).SequenceEqual(serviceIds.Concat(serviceIds)),
            "Refresh must query exactly the configured UUIDs once.");

        lookups.Clear(); connections.Clear();
        async Task<uint> Timeout(RfcommService service, CancellationToken token)
        {
            connections.Add(service.Channel);
            if (service == cached) await Task.Delay(System.Threading.Timeout.Infinite, token);
            return service.Channel;
        }
        channel = await RfcommConnection.ConnectControlCoreAsync(serviceIds, Lookup, Timeout, default, TimeSpan.FromMilliseconds(20));
        Check(channel == fresh.Channel && connections.Count == 2 && lookups.Count(call => call.Refresh) == 2,
            "An internal connection deadline must allow exactly one fresh attempt.");

        lookups.Clear(); connections.Clear();
        using (var cancelled = new CancellationTokenSource())
        {
            Task<uint> Cancel(RfcommService service, CancellationToken token)
            {
                connections.Add(service.Channel); cancelled.Cancel();
                return Task.FromCanceled<uint>(token);
            }
            await ExpectCancellation(() => RfcommConnection.ConnectControlCoreAsync(serviceIds, Lookup, Cancel, cancelled.Token));
            Check(connections.Count == 1 && lookups.Count == 2 && lookups.All(call => !call.Refresh),
                "User cancellation must not trigger a fresh query or reconnect.");
        }
        lookups.Clear(); connections.Clear();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectCancellation(() => RfcommConnection.ConnectControlCoreAsync(serviceIds, Lookup, Success, cancelled.Token));
            Check(lookups.Count == 0 && connections.Count == 0, "Pre-cancelled opening must not query or connect.");
        }

        lookups.Clear(); connections.Clear();
        IReadOnlyList<RfcommService> MissingCache(Guid id, bool refresh)
        {
            lookups.Add((id, refresh));
            return refresh && id == fresh.ServiceId ? [fresh] : [];
        }
        Task<uint> Rejected(RfcommService service, CancellationToken token)
        {
            connections.Add(service.Channel);
            return Task.FromException<uint>(new Win32Exception(10061));
        }
        try
        {
            await RfcommConnection.ConnectControlCoreAsync(serviceIds, MissingCache, Rejected, default);
            throw new Exception("A failed fresh connection was accepted.");
        }
        catch (Win32Exception failure) when (failure.NativeErrorCode == 10061) { }
        Check(connections.SequenceEqual(new[] { fresh.Channel }) && lookups.Count(call => call.Refresh) == 2,
            "An initially fresh endpoint must not trigger another discovery or connection attempt.");

        lookups.Clear(); connections.Clear();
        IReadOnlyList<RfcommService> AmbiguousFresh(Guid id, bool refresh)
        {
            lookups.Add((id, refresh));
            return id == cached.ServiceId ? [refresh ? fresh : cached]
                : refresh ? [new RfcommService(id, 8, "Melody")] : [];
        }
        try
        {
            await RfcommConnection.ConnectControlCoreAsync(serviceIds, AmbiguousFresh, Stale, default);
            throw new Exception("An ambiguous fresh result was accepted.");
        }
        catch (RfcommServiceDiscoveryException failure) when (failure.IsAmbiguous) { }
        Check(connections.SequenceEqual(new[] { cached.Channel }) && lookups.Count(call => call.Refresh) == 2,
            "Fresh ambiguity must end opening without merging or guessing a channel.");
        Console.WriteLine("PASS: stale SDP channel refresh, bounded connection timeout, user cancellation and failed fresh endpoints. No Bluetooth calls performed.");
    }

    private static async Task ExpectCancellation(Func<Task<uint>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }
}
