namespace HeyMelodyWindows.InstallerSupport;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is not ["compatibility"] && args is not ["probe-compatibility"] && args is not ["check-running", _])
        {
            Console.Error.WriteLine("Usage: HeyMelody.InstallerSupport compatibility | probe-compatibility | check-running <installation-root>");
            return 2;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Installer support requires Windows.");
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
                var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
                    throw new IOException("The current user's LocalAppData directory is unavailable; preferences were not changed.");
                var path = Path.Combine(localApplicationData, "HeyMelody", "preferences.json");
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
            if (running.Count == 0)
            {
                Console.WriteLine("No running HeyMelody applications in the installation directory.");
                return 0;
            }
            return ReportRunning(running);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static int ReportRunning(IReadOnlyList<ProcessSnapshot> processes)
    {
        Console.Error.WriteLine("Close HeyMelody and its background host before continuing: "
            + string.Join(", ", processes.Select(process => $"{process.Name} (PID {process.Id})")));
        return 10;
    }
}
