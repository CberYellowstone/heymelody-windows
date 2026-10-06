using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeyMelodyWindows.Windows;

internal static unsafe class Native
{
    internal const int AfBth = 32, SockStream = 1, Rfcomm = 3, WouldBlock = 10035;
    internal static readonly Guid MelodyServiceId = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly object StartupGate = new();
    private static bool _started;

    internal static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Bluetooth is required.");
    }
    internal static void Validate(ulong address, Guid serviceId)
    {
        if (address == 0 || address > 0xFFFFFFFFFFFFUL) throw new ArgumentOutOfRangeException(nameof(address));
        if (serviceId != MelodyServiceId) throw new ArgumentException("Only the Melody 079A service is allowed.", nameof(serviceId));
    }
    internal static Win32Exception Error(int error, string message) => new(error, message);
    internal static void Startup()
    {
        RequireWindows();
        lock (StartupGate)
        {
            if (_started) return;
            // WSADATA is <= 408 bytes on supported Windows ABIs. Process-lifetime startup avoids
            // WSACleanup racing other BCL/native sockets in this process; the OS cleans up on exit.
            byte* data = stackalloc byte[512];
            var error = WSAStartup(0x0202, data);
            if (error != 0) throw Error(error, "Windows Sockets initialization failed.");
            _started = true;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SockAddrBth
    {
        internal ushort Family;
        internal ulong Address;
        internal Guid ServiceId;
        internal uint Port;
    }
    [InlineArray(64)] internal struct SocketArray { private nint _element0; }
    [StructLayout(LayoutKind.Sequential)] internal struct FdSet
    {
        internal uint Count;
        internal SocketArray Sockets;
        internal static FdSet One(nint socket) { var set = new FdSet { Count = 1 }; set.Sockets[0] = socket; return set; }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct TimeVal { internal int Seconds, Microseconds; }
    // Complete SDK WSAQUERYSETW; lpVersion is required before lpszComment. Default natural alignment.
    [StructLayout(LayoutKind.Sequential)] internal struct QuerySet
    {
        internal uint Size;
        internal nint Name, ServiceId, Version, Comment;
        internal uint Namespace;
        internal nint NamespaceProviderId, Context;
        internal uint ProtocolCount;
        internal nint Protocols, QueryString;
        internal uint AddressCount;
        internal nint Addresses;
        internal uint OutputFlags;
        internal nint Blob;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct SocketAddress { internal nint Address; internal int Length; }
    [StructLayout(LayoutKind.Sequential)] internal struct CsAddrInfo
    {
        internal SocketAddress Local, Remote;
        internal int SocketType, Protocol;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct SystemTime
    {
        internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct BluetoothInfo
    {
        internal uint Size;
        internal ulong Address;
        internal uint ClassOfDevice;
        internal int Connected, Remembered, Authenticated;
        internal SystemTime LastSeen, LastUsed;
        internal fixed char Name[248];
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BluetoothSearch
    {
        internal uint Size;
        internal int ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        internal byte TimeoutMultiplier;
        internal nint Radio;
    }

    [DllImport("bthprops.cpl", SetLastError = true)] internal static extern nint BluetoothFindFirstDevice(BluetoothSearch* search, BluetoothInfo* info);
    [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BluetoothFindNextDevice(nint handle, BluetoothInfo* info);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BluetoothFindDeviceClose(nint handle);
    [DllImport("ws2_32.dll")] internal static extern int WSAStartup(ushort version, byte* data);
    [DllImport("ws2_32.dll")] internal static extern int WSAGetLastError();
    [DllImport("ws2_32.dll")] internal static extern nint socket(int family, int type, int protocol);
    [DllImport("ws2_32.dll")] internal static extern int closesocket(nint socket);
    [DllImport("ws2_32.dll")] internal static extern int ioctlsocket(nint socket, int command, ref uint argument);
    [DllImport("ws2_32.dll")] internal static extern int connect(nint socket, ref SockAddrBth address, int length);
    [DllImport("ws2_32.dll")] internal static extern int select(int ignored, FdSet* read, FdSet* write, FdSet* errors, ref TimeVal timeout);
    [DllImport("ws2_32.dll")] internal static extern int getsockopt(nint socket, int level, int option, ref int value, ref int length);
    [DllImport("ws2_32.dll")] internal static extern int send(nint socket, byte* bytes, int length, int flags);
    [DllImport("ws2_32.dll")] internal static extern int recv(nint socket, byte* bytes, int length, int flags);
    [DllImport("ws2_32.dll", EntryPoint = "WSAAddressToStringW", ExactSpelling = true)] internal static extern int AddressToString(ref SockAddrBth address, uint addressLength, nint protocolInfo, char* text, ref uint textLength);
    [DllImport("ws2_32.dll", EntryPoint = "WSALookupServiceBeginW", ExactSpelling = true)] internal static extern int LookupBegin(ref QuerySet query, uint flags, out nint handle);
    [DllImport("ws2_32.dll", EntryPoint = "WSALookupServiceNextW", ExactSpelling = true)] internal static extern int LookupNext(nint handle, uint flags, ref uint length, nint buffer);
    [DllImport("ws2_32.dll", EntryPoint = "WSALookupServiceEnd", ExactSpelling = true)] internal static extern int LookupEnd(nint handle);
}

internal sealed class SafeRfcommSocket : SafeHandleMinusOneIsInvalid
{
    internal SafeRfcommSocket(nint socket) : base(true) => SetHandle(socket);
    protected override bool ReleaseHandle() => Native.closesocket(handle) == 0;
}

/// <summary>Offline managed ABI diagnostics. ARM64 values describe the compiled ABI;
/// cross-building does not validate a real ARM64 Bluetooth stack.</summary>
public static unsafe class NativeLayoutInfo
{
    public static IReadOnlyDictionary<string, int> Values => new Dictionary<string, int>
    {
        ["Pointer.Size"] = IntPtr.Size,
        ["SOCKADDR_BTH.Size"] = sizeof(Native.SockAddrBth),
        ["SOCKADDR_BTH.Address"] = Offset<Native.SockAddrBth>("Address"),
        ["SOCKADDR_BTH.ServiceId"] = Offset<Native.SockAddrBth>("ServiceId"),
        ["SOCKADDR_BTH.Port"] = Offset<Native.SockAddrBth>("Port"),
        ["fd_set.Size"] = sizeof(Native.FdSet),
        ["fd_set.Sockets"] = Offset<Native.FdSet>("Sockets"),
        ["WSAQUERYSETW.Size"] = sizeof(Native.QuerySet),
        ["WSAQUERYSETW.Version"] = Offset<Native.QuerySet>("Version"),
        ["WSAQUERYSETW.Context"] = Offset<Native.QuerySet>("Context"),
        ["WSAQUERYSETW.Addresses"] = Offset<Native.QuerySet>("Addresses"),
        ["WSAQUERYSETW.Blob"] = Offset<Native.QuerySet>("Blob"),
        ["SOCKET_ADDRESS.Size"] = sizeof(Native.SocketAddress),
        ["CSADDR_INFO.Size"] = sizeof(Native.CsAddrInfo),
        ["CSADDR_INFO.Remote"] = Offset<Native.CsAddrInfo>("Remote"),
        ["BLUETOOTH_DEVICE_INFO.Size"] = sizeof(Native.BluetoothInfo),
        ["BLUETOOTH_DEVICE_INFO.Name"] = Offset<Native.BluetoothInfo>("Name"),
        ["BLUETOOTH_DEVICE_SEARCH_PARAMS.Size"] = sizeof(Native.BluetoothSearch),
        ["BLUETOOTH_DEVICE_SEARCH_PARAMS.Radio"] = Offset<Native.BluetoothSearch>("Radio")
    };
    private static int Offset<T>(string field) where T : struct => checked((int)Marshal.OffsetOf<T>(field));
}
