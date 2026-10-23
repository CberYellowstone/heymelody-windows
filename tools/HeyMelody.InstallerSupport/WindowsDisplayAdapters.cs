using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HeyMelodyWindows.InstallerSupport;

internal static class WindowsDisplayAdapters
{
    // EnumDisplayDevices does not contractually expose adapter hardware IDs (DISPLAY_DEVICE.DeviceID is unused).
    // Enumerate present DISPLAY-class devices through SetupAPI instead; do not inspect stale registry devices.
    // https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-display_devicew
    // https://learn.microsoft.com/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceregistrypropertyw
    internal static IReadOnlyList<DisplayAdapter> Enumerate()
    {
        var displayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        using var devices = SetupDiGetClassDevsW(ref displayClass, null, IntPtr.Zero, 0x2); // DIGCF_PRESENT
        if (devices.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate display adapters.");
        var result = new List<DisplayAdapter>();
        for (uint index = 0; ; index++)
        {
            var data = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            if (!SetupDiEnumDeviceInfo(devices, index, ref data))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break; // ERROR_NO_MORE_ITEMS
                throw new Win32Exception(error, "Cannot enumerate a display adapter.");
            }
            var hardwareIds = ReadProperty(devices, ref data, 1, 7); // SPDRP_HARDWAREID / REG_MULTI_SZ
            var description = ReadProperty(devices, ref data, 12, 1).FirstOrDefault() // SPDRP_FRIENDLYNAME / REG_SZ
                ?? ReadProperty(devices, ref data, 0, 1).FirstOrDefault() // SPDRP_DEVICEDESC
                ?? "Unnamed display adapter";
            result.Add(new DisplayAdapter(description, hardwareIds));
        }
        return result;
    }

    private static string[] ReadProperty(SafeDeviceInfoSet devices, ref DeviceInfoData data, uint property, uint expectedType)
    {
        if (!SetupDiGetDeviceRegistryPropertyW(devices, ref data, property, out _, null, 0, out var size))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 13) return []; // ERROR_INVALID_DATA: optional property absent
            if (error != 122) throw new Win32Exception(error, "Cannot read display-adapter information.");
        }
        if (size == 0) return [];
        if (size > 1024 * 1024) throw new InvalidDataException("The display-adapter property is unexpectedly large.");
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(devices, ref data, property, out var type, buffer, size, out var written))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read display-adapter information.");
        if (type != expectedType || written > size || written % 2 != 0)
            throw new InvalidDataException("The display-adapter property has an unexpected format.");
        return Encoding.Unicode.GetString(buffer, 0, checked((int)written))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal UIntPtr Reserved;
    }

    private sealed class SafeDeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDeviceInfoSet() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeDeviceInfoSet SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(SafeDeviceInfoSet devices, uint index, ref DeviceInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(SafeDeviceInfoSet devices, ref DeviceInfoData data,
        uint property, out uint propertyType, [Out] byte[]? buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
}
