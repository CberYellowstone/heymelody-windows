using System.Runtime.InteropServices;
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
        var serviceId = Native.MelodyServiceId;
        Reject<ArgumentException>(() => WindowsSdp.FindServices(address, Guid.Empty));
        Reject<ArgumentOutOfRangeException>(() => WindowsSdp.FindServices(0, serviceId));
        Reject<ArgumentException>(() => RfcommConnection.ConnectAsync(address, Guid.Empty, 1, default));
        Reject<ArgumentOutOfRangeException>(() => RfcommConnection.ConnectAsync(address, serviceId, 0, default));
        Reject<ArgumentOutOfRangeException>(() => RfcommConnection.ConnectAsync(address, serviceId, 31, default));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Reject<OperationCanceledException>(() => RfcommConnection.ConnectAsync(address, serviceId, 1, cancelled.Token).GetAwaiter().GetResult());
        }

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
        Console.WriteLine("PASS: offline ABI, whitelist input, pre-cancel and bounded SDP parser checks. No Bluetooth calls performed.");
    }
    static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
