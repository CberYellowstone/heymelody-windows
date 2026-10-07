using System.Runtime.InteropServices;

namespace HeyMelodyWindows.Windows;

public sealed record RfcommService(Guid ServiceId, uint Channel, string? Name);

public static class WindowsSdp
{
    private const int MaxBuffer = 1024 * 1024;
    private const int MaxResults = 256;
    private const uint ReturnName = 0x10, ReturnType = 0x20, ReturnAddress = 0x100;

    /// <summary>Queries one explicitly selected OPPO control service, 079A or legacy 1107,
    /// for the specified peer. No inquiry, cache flush,
    /// service registration, registry access, or channel probing occurs. An empty cached result
    /// is not proof that the peer lacks the service. WSALookupServiceBegin/Next are synchronous
    /// native calls and cannot be made absolutely thread-cancellable; the caller must supervise
    /// a worker process if it needs a wall-clock deadline.</summary>
    public static unsafe IReadOnlyList<RfcommService> FindServices(ulong address, Guid serviceId)
    {
        Native.Validate(address, serviceId);
        Native.Startup();
        var endpoint = new Native.SockAddrBth { Family = Native.AfBth, Address = address };
        // Microsoft requires lpszContext to come from WSAAddressToString, not a hand-formatted MAC:
        // https://learn.microsoft.com/windows/win32/bluetooth/bluetooth-and-wsalookupservicebegin-for-service-discovery
        char* text = stackalloc char[256];
        uint chars = 256;
        var formatResult = Native.AddressToString(ref endpoint, (uint)sizeof(Native.SockAddrBth), 0, text, ref chars);
        var formatError = formatResult == 0 ? 0 : Native.WSAGetLastError();
        if (formatResult != 0) throw Native.Error(formatError, "Bluetooth SDP address formatting failed.");
        if (chars == 0 || chars > 256 || text[chars - 1] != '\0') throw new InvalidDataException("Invalid native Bluetooth address string.");
        var query = new Native.QuerySet
        {
            Size = (uint)sizeof(Native.QuerySet), Namespace = 16,
            ServiceId = (nint)(&serviceId), Context = (nint)text
        };
        var begin = Native.LookupBegin(ref query, ReturnName | ReturnType | ReturnAddress, out var lookup);
        var beginError = begin == 0 ? 0 : Native.WSAGetLastError();
        if (begin != 0)
        {
            if (beginError is 10108 or 10110 or 10112) return Array.Empty<RfcommService>();
            throw Native.Error(beginError, "Bluetooth SDP lookup failed.");
        }
        nint buffer = 0;
        try
        {
            var capacity = 4096;
            buffer = Marshal.AllocHGlobal(capacity);
            var result = new List<RfcommService>();
            for (var records = 0; ;)
            {
                new Span<byte>((void*)buffer, capacity).Clear();
                uint size = (uint)capacity;
                var next = Native.LookupNext(lookup, ReturnName | ReturnAddress, ref size, buffer);
                var error = next == 0 ? 0 : Native.WSAGetLastError();
                if (next != 0)
                {
                    if (error is 10108 or 10110 or 10112) return result.ToArray();
                    if (error == 10014 && size > capacity && size <= MaxBuffer)
                    {
                        var newCapacity = checked((int)size);
                        var replacement = Marshal.AllocHGlobal(newCapacity);
                        Marshal.FreeHGlobal(buffer);
                        buffer = replacement;
                        capacity = newCapacity;
                        continue;
                    }
                    throw Native.Error(error, "Bluetooth SDP result retrieval failed.");
                }
                if (++records > MaxResults) throw new InvalidDataException("Bluetooth SDP result count exceeded its limit.");
                if (size < sizeof(Native.QuerySet) || size > capacity) throw new InvalidDataException("Invalid Bluetooth SDP result size.");
                Parse(buffer, checked((int)size), address, serviceId, result);
            }
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            Native.LookupEnd(lookup);
        }
    }

    internal static unsafe void Parse(nint buffer, int size, ulong address, Guid serviceId, List<RfcommService> services)
    {
        var query = Read<Native.QuerySet>(buffer, size, buffer);
        if (query.Size != sizeof(Native.QuerySet)) throw new InvalidDataException("Invalid SDP query structure size.");
        if (query.ServiceId != 0 && Read<Guid>(buffer, size, query.ServiceId) != serviceId)
            throw new InvalidDataException("SDP returned a service outside the requested Melody UUID.");
        if (query.AddressCount > 256) throw new InvalidDataException("Invalid SDP address count.");
        var name = ReadString(buffer, size, query.Name);
        var stride = sizeof(Native.CsAddrInfo);
        if (query.AddressCount != 0) CheckRange(buffer, size, query.Addresses, checked((int)query.AddressCount * stride));
        for (var i = 0; i < query.AddressCount; i++)
        {
            var item = Read<Native.CsAddrInfo>(buffer, size, query.Addresses + checked(i * stride));
            if (item.SocketType != Native.SockStream || item.Protocol != Native.Rfcomm) continue;
            if (item.Remote.Length < sizeof(Native.SockAddrBth)) throw new InvalidDataException("Truncated SDP RFCOMM address.");
            CheckRange(buffer, size, item.Remote.Address, item.Remote.Length);
            var peer = Read<Native.SockAddrBth>(buffer, size, item.Remote.Address);
            if (peer.Family != Native.AfBth || peer.Address != address) continue;
            if (peer.ServiceId != Guid.Empty && peer.ServiceId != serviceId) throw new InvalidDataException("SDP returned a mismatched RFCOMM service.");
            if (peer.Port is < 1 or > 30) continue;
            if (!services.Any(x => x.Channel == peer.Port)) services.Add(new(serviceId, peer.Port, name));
        }
    }
    private static unsafe T Read<T>(nint buffer, int size, nint pointer) where T : unmanaged
    {
        CheckRange(buffer, size, pointer, sizeof(T));
        return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<T>((void*)pointer);
    }
    private static void CheckRange(nint buffer, int size, nint pointer, int length)
    {
        var start = unchecked((nuint)buffer);
        var value = unchecked((nuint)pointer);
        if (pointer == 0 || length < 0 || value < start || value - start > (nuint)size || (nuint)length > (nuint)size - (value - start))
            throw new InvalidDataException("SDP returned a pointer outside the result allocation.");
    }
    private static unsafe string? ReadString(nint buffer, int size, nint pointer)
    {
        if (pointer == 0) return null;
        CheckRange(buffer, size, pointer, 2);
        var available = (int)((nuint)size - (unchecked((nuint)pointer) - unchecked((nuint)buffer))) / 2;
        var limit = Math.Min(available, 4096);
        for (var i = 0; i < limit; i++)
        {
            if (System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ushort>((byte*)pointer + i * 2) == 0)
                return Marshal.PtrToStringUni(pointer, i);
        }
        throw new InvalidDataException("SDP returned an unterminated service name.");
    }
}
