namespace HeyMelodyWindows.InstallerSupport;

internal sealed record ProcessSnapshot(int Id, string Name, string? ExecutablePath, string? QueryError = null);

internal static class RunningApplications
{
    private static readonly HashSet<string> ApplicationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HeyMelody", "HeyMelody.App", "HeyMelody.Host", "HeyMelody.DeviceManager",
        "HeyMelody.FitTest", "HeyMelody.HearingSetup"
    };

    // The setup executable and standalone helper also start with "HeyMelody".
    // Only actual application entry points share preferences or lock the payload.
    internal static bool IsApplicationName(string name) => ApplicationNames.Contains(name);

    internal static IReadOnlyList<ProcessSnapshot> FindAny(IEnumerable<ProcessSnapshot> processes, int selfId) => processes
        .Where(process => process.Id != selfId && IsApplicationName(process.Name)).ToArray();

    internal static IReadOnlyList<ProcessSnapshot> Find(string installationRoot, IEnumerable<ProcessSnapshot> processes, int selfId)
    {
        if (!Path.IsPathFullyQualified(installationRoot))
            throw new ArgumentException("The installation directory must be an absolute path.");
        var prefix = Normalize(installationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var running = new List<ProcessSnapshot>();
        foreach (var process in processes)
        {
            if (process.Id == selfId || !IsApplicationName(process.Name))
                continue;
            if (process.QueryError is not null || process.ExecutablePath is null)
                throw new IOException($"Cannot check {process.Name} (PID {process.Id}): {process.QueryError ?? "executable path unavailable"}");
            if (Normalize(process.ExecutablePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                running.Add(process);
        }
        return running;
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        return Path.GetFullPath(path);
    }
}
