using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HeyMelodyWindows.Windows;

public sealed record BluetoothCandidate(ulong Address, string Name, bool Connected, bool Remembered);

public static class WindowsBluetooth
{
    /// <summary>Enumerates authenticated or remembered devices from Windows, without inquiry or pairing.
    /// This synchronous native operation has no absolute time bound; supervise the caller process.</summary>
    public static unsafe IReadOnlyList<BluetoothCandidate> EnumeratePaired()
    {
        Native.RequireWindows();
        var search = new Native.BluetoothSearch
        {
            Size = (uint)sizeof(Native.BluetoothSearch), ReturnAuthenticated = 1, ReturnRemembered = 1
        };
        var info = new Native.BluetoothInfo { Size = (uint)sizeof(Native.BluetoothInfo) };
        var handle = Native.BluetoothFindFirstDevice(&search, &info);
        var firstError = handle == 0 ? Marshal.GetLastPInvokeError() : 0;
        if (handle == 0)
        {
            if (firstError == 259) return Array.Empty<BluetoothCandidate>();
            throw new Win32Exception(firstError, "Bluetooth device enumeration failed.");
        }
        try
        {
            var result = new Dictionary<ulong, BluetoothCandidate>();
            for (var count = 0; ; count++)
            {
                if (count >= 4096) throw new InvalidDataException("Bluetooth enumeration exceeded the record limit.");
                if (info.Remembered != 0 || info.Authenticated != 0)
                {
                    var length = 0;
                    while (length < 248 && info.Name[length] != '\0') length++;
                    var address = info.Address & 0xFFFFFFFFFFFFUL;
                    result[address] = new(address, new string(info.Name, 0, length), info.Connected != 0, info.Remembered != 0);
                }
                info = new Native.BluetoothInfo { Size = (uint)sizeof(Native.BluetoothInfo) };
                var found = Native.BluetoothFindNextDevice(handle, &info);
                var error = found ? 0 : Marshal.GetLastPInvokeError();
                if (found) continue;
                if (error != 259) throw new Win32Exception(error, "Bluetooth device enumeration failed.");
                return result.Values.OrderBy(x => x.Address).ToArray();
            }
        }
        finally { Native.BluetoothFindDeviceClose(handle); }
    }
}
