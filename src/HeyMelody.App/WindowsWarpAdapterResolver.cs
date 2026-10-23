using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HeyMelodyWindows.Ui;

/// <summary>Identifies DXGI's WARP adapter without relying on its display name or adding a graphics dependency.</summary>
internal static unsafe class WindowsWarpAdapterResolver
{
    private static readonly Guid Factory4Id = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
    private static readonly Guid Adapter1Id = new("29038f61-3839-4626-91fd-086879011a05");

    internal static byte[]? GetAdapterLuid()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        nint factory = 0;
        nint adapter = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in Factory4Id, out factory));
            if (factory == 0)
                return null;

            // IDXGIFactory4::EnumWarpAdapter is slot 27, including inherited IUnknown/DXGI methods.
            var enumWarpAdapter = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)factory)[27];
            var adapterId = Adapter1Id;
            Marshal.ThrowExceptionForHR(enumWarpAdapter(factory, &adapterId, &adapter));
            if (adapter == 0)
                return null;

            // IDXGIAdapter1::GetDesc1 is slot 10. All HRESULTs are checked before reading the output.
            var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription1*, int>)(*(nint**)adapter)[10];
            AdapterDescription1 description = default;
            Marshal.ThrowExceptionForHR(getDescription(adapter, &description));
            return description.AdapterLuid.ToBytes();
        }
        finally
        {
            if (adapter != 0)
                Marshal.Release(adapter);
            if (factory != 0)
                Marshal.Release(factory);
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid interfaceId, out nint factory);

    // Native LUID has four-byte alignment even in an x86 process. Using ulong here would change DESC1's layout.
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterLuid
    {
        public uint LowPart;
        public int HighPart;

        internal readonly byte[] ToBytes()
        {
            var result = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(result, LowPart);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), HighPart);
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterDescription1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public AdapterLuid AdapterLuid;
        public uint Flags;
    }
}
