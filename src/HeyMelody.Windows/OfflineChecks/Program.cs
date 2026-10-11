using System.Runtime.InteropServices;
using HeyMelodyWindows.Resources;
using HeyMelodyWindows.Windows;

// Offline only: no enumeration, service lookup or native socket creation is called.
unsafe class Program
{
    static void Main()
    {
        var wide = IntPtr.Size == 8;
        var expected = new Dictionary<string, int>
        {
            ["SOCKADDR_BTH.Size"] = 30, ["SOCKADDR_BTH.Address"] = 2,
            ["SOCKADDR_BTH.ServiceId"] = 10, ["SOCKADDR_BTH.Port"] = 26,
            ["fd_set.Size"] = wide ? 520 : 260, ["fd_set.Sockets"] = wide ? 8 : 4,
            ["WSANETWORKEVENTS.Size"] = 44, ["WSANETWORKEVENTS.Errors"] = 4,
            ["WSAQUERYSETW.Size"] = wide ? 120 : 60, ["WSAQUERYSETW.Version"] = wide ? 24 : 12,
            ["WSAQUERYSETW.Context"] = wide ? 56 : 28, ["WSAQUERYSETW.Addresses"] = wide ? 96 : 48,
            ["WSAQUERYSETW.Blob"] = wide ? 112 : 56, ["SOCKET_ADDRESS.Size"] = wide ? 16 : 8,
            ["CSADDR_INFO.Size"] = wide ? 40 : 24, ["CSADDR_INFO.Remote"] = wide ? 16 : 8,
            ["BLUETOOTH_DEVICE_INFO.Size"] = 560, ["BLUETOOTH_DEVICE_INFO.Name"] = 64,
            ["BLUETOOTH_DEVICE_SEARCH_PARAMS.Size"] = wide ? 40 : 32,
            ["BLUETOOTH_DEVICE_SEARCH_PARAMS.Radio"] = wide ? 32 : 28
        };
        foreach (var pair in expected)
        {
            if (NativeLayoutInfo.Values[pair.Key] != pair.Value) throw new Exception($"ABI mismatch: {pair.Key} = {NativeLayoutInfo.Values[pair.Key]}, expected {pair.Value}");
            Console.WriteLine($"{pair.Key}={pair.Value}");
        }
        const ulong address = 0x112233445566;
        var serviceIds = new[]
        {
            new Guid("0000079A-D102-11E1-9B23-00025B00A5A5"),
            new Guid("00001107-D102-11E1-9B23-00025B00A5A5")
        };
        if (Native.MelodyServiceId != serviceIds[0] || Native.LegacyMelodyServiceId != serviceIds[1])
            throw new Exception("OPPO service UUID constants do not match the complete vendor UUIDs.");
        CheckSdpDiscovery(serviceIds);
        SdpConnectionChecks.RunAsync(serviceIds).GetAwaiter().GetResult();
        Reject<ArgumentException>(() => WindowsSdp.FindUniqueControlService(address, []));
        var bundled = new BundledResourceStore();
        foreach (var source in Enum.GetValues<ResourceSource>())
        {
            var transports = bundled.GetCatalog(source).Profiles.Values.Where(profile => profile.CanUseControlTransport).ToArray();
            if (transports.Length == 0) throw new Exception($"Bundled catalog has no control transports: {source}");
            var catalogIds = new HashSet<Guid>();
            foreach (var profile in transports)
            {
                var catalogId = Guid.Parse(profile.Uuid!);
                Native.Validate(address, catalogId);
                catalogIds.Add(catalogId);
            }
            if (!catalogIds.SetEquals(serviceIds)) throw new Exception($"Bundled transport UUIDs changed: {source}");
            Console.WriteLine($"PASS: {source} bundled transport validation ({transports.Length} profiles, {catalogIds.Count} UUIDs).");
        }
        foreach (var serviceId in serviceIds)
        {
            Native.Validate(1, serviceId);
            Native.Validate(0xFFFFFFFFFFFFUL, serviceId);
            foreach (var invalidAddress in new[] { 0UL, 0x1000000000000UL })
            {
                Reject<ArgumentOutOfRangeException>(() => Native.Validate(invalidAddress, serviceId));
                Reject<ArgumentOutOfRangeException>(() => WindowsSdp.FindServices(invalidAddress, serviceId));
                Reject<ArgumentOutOfRangeException>(() => RfcommConnection.ConnectAsync(invalidAddress, serviceId, 1, default));
            }
            foreach (var invalidChannel in new[] { 0U, 31U })
                Reject<ArgumentOutOfRangeException>(() => RfcommConnection.ConnectAsync(address, serviceId, invalidChannel, default));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            foreach (var channel in new[] { 1U, 30U })
                Reject<OperationCanceledException>(() => RfcommConnection.ConnectAsync(address, serviceId, channel, cancelled.Token).GetAwaiter().GetResult());
            CheckSdpParser(address, serviceId, serviceIds.Single(id => id != serviceId));
        }
        foreach (var invalidServiceId in new[]
        {
            Guid.Empty,
            new Guid("0000079A-0000-1000-8000-00805F9B34FB"),
            new Guid("00001107-0000-1000-8000-00805F9B34FB"),
            new Guid("0000079A-D102-11E1-9B23-00025B00A5A4"),
            new Guid("00001107-D102-11E1-9B23-00025B00A5A4"),
            new Guid("0000110B-0000-1000-8000-00805F9B34FB")
        })
        {
            Reject<ArgumentException>(() => Native.Validate(address, invalidServiceId));
            Reject<ArgumentException>(() => WindowsSdp.FindServices(address, invalidServiceId));
            Reject<ArgumentException>(() => RfcommConnection.ConnectAsync(address, invalidServiceId, 1, default));
        }
        Console.WriteLine("PASS: offline ABI, bundled transport whitelist, input, pre-cancel, SDP refresh selection and bounded SDP parser checks. No Bluetooth calls performed.");
    }

    static void CheckSdpDiscovery(Guid[] serviceIds)
    {
        var modern = new RfcommService(serviceIds[0], 7, "Melody");
        var legacy = new RfcommService(serviceIds[1], 8, "Melody");
        var calls = new List<(Guid Id, bool Refresh)>();
        IReadOnlyList<RfcommService> Cached(Guid id, bool refresh)
        {
            calls.Add((id, refresh));
            if (refresh) throw new Exception("A unique cached service must not trigger remote SDP.");
            return id == modern.ServiceId ? [modern, modern] : [];
        }
        var selected = WindowsSdp.FindUniqueControlService([.. serviceIds, serviceIds[0]], Cached);
        if (selected != modern || calls.Count != 2 || calls.Any(call => call.Refresh))
            throw new Exception("Unique cached SDP selection or UUID deduplication failed.");

        calls.Clear();
        IReadOnlyList<RfcommService> MissingCache(Guid id, bool refresh)
        {
            calls.Add((id, refresh));
            return refresh && id == modern.ServiceId ? [modern] : [];
        }
        selected = WindowsSdp.FindUniqueControlService(serviceIds, MissingCache);
        if (selected != modern || !calls.Select(call => call.Refresh).SequenceEqual(new[] { false, false, true, true })
            || !calls.Select(call => call.Id).SequenceEqual(serviceIds.Concat(serviceIds)))
            throw new Exception("Missing cached SDP must refresh exactly the configured UUIDs.");

        calls.Clear();
        IReadOnlyList<RfcommService> AmbiguousCache(Guid id, bool refresh)
        {
            calls.Add((id, refresh));
            return id == modern.ServiceId ? [modern] : refresh ? [] : [legacy];
        }
        selected = WindowsSdp.FindUniqueControlService(serviceIds, AmbiguousCache);
        if (selected != modern || calls.Count(call => call.Refresh) != 2)
            throw new Exception("Ambiguous cached SDP must use the refreshed result.");

        foreach (bool ambiguous in new[] { false, true })
        {
            try
            {
                WindowsSdp.FindUniqueControlService(serviceIds, (id, _) => ambiguous
                    ? id == modern.ServiceId ? [modern] : [legacy]
                    : [new RfcommService(Guid.Empty, 7, null), new RfcommService(id, 0, null), new RfcommService(id, 31, null)]);
                throw new Exception("SDP selection accepted missing or ambiguous endpoints.");
            }
            catch (RfcommServiceDiscoveryException failure)
            {
                if (failure.IsAmbiguous != ambiguous) throw new Exception("SDP failure reason mismatch.");
            }
        }
        Console.WriteLine("PASS: SDP cache miss/ambiguity refreshes only configured UUIDs; unique, bounded endpoints remain required.");
    }

    static void CheckSdpParser(ulong address, Guid serviceId, Guid otherServiceId)
    {
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            new Span<byte>((void*)buffer, 1024).Clear();
            var query = (Native.QuerySet*)buffer;
            query->Size = (uint)sizeof(Native.QuerySet);
            query->ServiceId = buffer + 160;
            *(Guid*)query->ServiceId = serviceId;
            query->AddressCount = 1;
            query->Addresses = buffer + 200;
            var item = (Native.CsAddrInfo*)query->Addresses;
            item->SocketType = Native.SockStream;
            item->Protocol = Native.Rfcomm;
            item->Remote.Address = buffer + 300;
            item->Remote.Length = sizeof(Native.SockAddrBth);
            var peer = (Native.SockAddrBth*)item->Remote.Address;
            *peer = new Native.SockAddrBth { Family = Native.AfBth, Address = address, Port = 7 };
            query->Name = buffer + 400;
            Marshal.Copy("Melody\0".ToCharArray(), 0, query->Name, 7);
            var result = new List<RfcommService>();
            WindowsSdp.Parse(buffer, 1024, address, serviceId, result);
            WindowsSdp.Parse(buffer, 1024, address, serviceId, result);
            if (result.Count != 1 || result[0] != new RfcommService(serviceId, 7, "Melody")) throw new Exception("SDP parse or deduplication mismatch.");
            var saved = item->Remote.Address;
            item->Remote.Address = buffer + 1010;
            Reject<InvalidDataException>(() => WindowsSdp.Parse(buffer, 1024, address, serviceId, []));
            item->Remote.Address = saved;
            query->AddressCount = uint.MaxValue;
            Reject<InvalidDataException>(() => WindowsSdp.Parse(buffer, 1024, address, serviceId, []));
            query->AddressCount = 1;
            *(Guid*)query->ServiceId = otherServiceId;
            Reject<InvalidDataException>(() => WindowsSdp.Parse(buffer, 1024, address, serviceId, []));
            *(Guid*)query->ServiceId = Guid.Empty;
            Reject<InvalidDataException>(() => WindowsSdp.Parse(buffer, 1024, address, serviceId, []));
            *(Guid*)query->ServiceId = serviceId;
            query->Name = buffer + 1023;
            Reject<InvalidDataException>(() => WindowsSdp.Parse(buffer, 1024, address, serviceId, []));
            query->Name = buffer + 400;
            new Span<byte>((void*)query->Name, 624).Fill(0x41);
            Reject<InvalidDataException>(() => WindowsSdp.Parse(buffer, 1024, address, serviceId, []));
            query->Name = 0;
            query->Addresses = 1;
            Reject<InvalidDataException>(() => WindowsSdp.Parse(buffer, 1024, address, serviceId, []));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
