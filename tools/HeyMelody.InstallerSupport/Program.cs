namespace HeyMelodyWindows.InstallerSupport;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is not ["compatibility"] && args is not ["probe-compatibility"]
            && args is not ["check-running", _] && args is not ["check-installation", _]
            && args is not ["desktop-preference", "read"] && args is not ["desktop-preference", "write", "true" or "false"])
        {
            Console.Error.WriteLine("Usage: HeyMelody.InstallerSupport compatibility | probe-compatibility | check-running <installation-root>"
                + " | check-installation <installation-root> | desktop-preference read | desktop-preference write <true|false>");
            return 2;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Installer support requires Windows.");
            if (args[0] == "desktop-preference")
            {
                var path = CurrentUserPreferencesPath("installer-preferences.json");
                if (args[1] == "read")
                {
                    var enabled = DesktopShortcutPreferences.Read(path);
                    Console.WriteLine(enabled ? "true" : "false");
                    return enabled ? 0 : 20;
                }
                DesktopShortcutPreferences.Write(path, args[2] == "true");
                Console.WriteLine("Desktop shortcut preference saved: " + args[2]);
                return 0;
            }
            if (args[0] is "compatibility" or "probe-compatibility")
            {
                var adapters = WindowsDisplayAdapters.Enumerate();
                if (!adapters.Any(CompatibilityPreferences.IsAffected))
                {
                    Console.WriteLine("No affected display adapter; preferences unchanged.");
                    return 0;
                }
                if (args[0] == "probe-compatibility")
                {
                    Console.WriteLine("Affected QCOM043A display adapter found; no preferences were accessed.");
                    return 20;
                }
                // Preferences are shared across portable copies and installations for this user.
                var runningForPreferences = RunningApplications.FindAny(WindowsProcesses.Enumerate(), Environment.ProcessId);
                if (runningForPreferences.Count != 0) return ReportRunning(runningForPreferences);
                var path = CurrentUserPreferencesPath("preferences.json");
                var result = CompatibilityPreferences.Apply(adapters, path);
                Console.WriteLine(result switch
                {
                    CompatibilityResult.NotAffected => "No affected display adapter; preferences unchanged.",
                    CompatibilityResult.AlreadyEnabled => "Basic Render is already enabled; preferences unchanged.",
                    _ => "Enabled Basic Render for the older Qualcomm Adreno 680/685/690 adapter family (QCOM043A)."
                });
                return 0;
            }
            var running = RunningApplications.Find(args[1], WindowsProcesses.Enumerate(), Environment.ProcessId);
            if (running.Count != 0) return ReportRunning(running);
            if (args[0] == "check-installation")
            {
                var checkedFiles = InstallationFiles.CheckUnlocked(args[1]);
                Console.WriteLine($"No running HeyMelody applications; {checkedFiles} payload files are unlocked. Missing payload files are allowed.");
                return 0;
            }
            Console.WriteLine("No running HeyMelody applications in the installation directory.");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static string CurrentUserPreferencesPath(string fileName)
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
            throw new IOException("The current user's LocalAppData directory is unavailable; preferences were not changed.");
        return Path.Combine(localApplicationData, "HeyMelody", fileName);
    }

    private static int ReportRunning(IReadOnlyList<ProcessSnapshot> processes)
    {
        Console.Error.WriteLine("Close HeyMelody and its background host before continuing: "
            + string.Join(", ", processes.Select(process => $"{process.Name} (PID {process.Id})")));
        return 10;
    }
}
