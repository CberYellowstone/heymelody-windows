using HeyMelodyWindows.InstallerSupport;
using static HeyMelodyWindows.InstallerSupport.Tests.Program;

namespace HeyMelodyWindows.InstallerSupport.Tests;

internal static class ProcessTests
{
    internal static int Run(string output)
    {
        var root = Path.Combine(output, "installed");
        var snapshots = new[]
        {
            new ProcessSnapshot(1, "HeyMelody.App", Path.Combine(root, "GUI", "HeyMelody.App.exe")),
            new ProcessSnapshot(2, "HeyMelody.Host", Path.Combine(root, "Host", "HeyMelody.Host.exe")),
            new ProcessSnapshot(3, "HeyMelody.DeviceManager", Path.Combine(root + "-other", "HeyMelody.DeviceManager.exe")),
            new ProcessSnapshot(4, "HeyMelody.App", Path.Combine(output, "portable", "HeyMelody.App.exe")),
            new ProcessSnapshot(5, "unrelated", Path.Combine(root, "unrelated.exe")),
            new ProcessSnapshot(6, "HeyMelody.InstallerSupport", Path.Combine(root, "GUI", "HeyMelody.InstallerSupport.exe"))
        };
        Check(RunningApplications.Find(root, snapshots, selfId: 6).Select(process => process.Id).SequenceEqual([1, 2]),
            "Only this installation's applications and background host block the operation.");
        Check(RunningApplications.Find(root + Path.DirectorySeparatorChar, snapshots, selfId: 6).Count == 2,
            "A trailing separator does not change directory identity.");
        Check(RunningApplications.Find(root, [], selfId: 6).Count == 0, "An idle installation can continue.");
        Check(RunningApplications.FindAny(snapshots, selfId: 6).Select(process => process.Id).SequenceEqual([1, 2, 3, 4]),
            "Preference updates must also wait for other installations and portable copies sharing the same user settings.");
        var installerProcesses = new[]
        {
            new ProcessSnapshot(20, "HeyMelody_Windows_0.9.2_x64_Setup", Path.Combine(root, "Setup.exe")),
            new ProcessSnapshot(21, "HeyMelody.InstallerSupport", null, "Access denied"),
            new ProcessSnapshot(22, "Uninstall", Path.Combine(root, "Uninstall.exe"))
        };
        Check(RunningApplications.FindAny(installerProcesses, selfId: 6).Count == 0
            && RunningApplications.Find(root, installerProcesses, selfId: 6).Count == 0,
            "The parent installer, other installer helpers and uninstaller must not block compatibility defaults.");
        foreach (var name in new[] { "HeyMelody", "HeyMelody.DeviceManager", "HeyMelody.FitTest", "HeyMelody.HearingSetup", "heymelody.host" })
            Check(RunningApplications.IsApplicationName(name), "Every published GUI and Host must be recognized: " + name);
        Throws(() => RunningApplications.Find("relative-install-directory", snapshots, selfId: 6), "Relative paths must be rejected.");
        Throws(() => RunningApplications.Find(root, [new ProcessSnapshot(7, "HeyMelody.Host", null, "Access denied")], selfId: 6),
            "An inaccessible live HeyMelody process must not be silently ignored.");
        if (OperatingSystem.IsWindows())
        {
            Check(RunningApplications.Find(root.ToUpperInvariant(), snapshots, selfId: 6).Count == 2, "Windows paths are case-insensitive.");
            Check(RunningApplications.Find(root, [new ProcessSnapshot(8, "HeyMelody.App", @"\\?\" + Path.Combine(root, "GUI", "HeyMelody.App.exe"))], 6).Count == 1,
                "Extended Win32 paths identify the same installation.");
        }
        Console.WriteLine("PASS running-app scope, shared-host detection, path boundaries and inaccessible-process handling");
        return 6;
    }
}
