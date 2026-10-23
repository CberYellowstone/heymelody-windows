using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HeyMelodyWindows.InstallerSupport;

internal static class WindowsProcesses
{
    internal static IReadOnlyList<ProcessSnapshot> Enumerate()
    {
        var result = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; } // Process exited after the snapshot.
                if (process.Id == Environment.ProcessId || !RunningApplications.IsApplicationName(name))
                    continue;
                using var handle = OpenProcess(0x1000, false, process.Id); // PROCESS_QUERY_LIMITED_INFORMATION
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (HasExited(process)) continue;
                    result.Add(new ProcessSnapshot(process.Id, name, null, new Win32Exception(error).Message));
                    continue;
                }
                var buffer = new StringBuilder(32768);
                var size = (uint)buffer.Capacity;
                if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                    result.Add(new ProcessSnapshot(process.Id, name, buffer.ToString()));
                else
                {
                    var error = Marshal.GetLastWin32Error();
                    if (!HasExited(process))
                        result.Add(new ProcessSnapshot(process.Id, name, null, new Win32Exception(error).Message));
                }
            }
        }
        return result;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder fileName, ref uint size);
}
