namespace HeyMelodyWindows.InstallerSupport.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        var output = args is ["--output-dir", var requested] ? Path.GetFullPath(requested)
            : Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "installer-support-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(output);
            var count = CompatibilityTests.Run(output) + ProcessTests.Run(output) + DesktopShortcutTests.Run(output)
                + InstallationFilesTests.Run(output);
            Console.WriteLine($"PASS {count} installer-support checks; no UI, hardware enumeration or user preferences accessed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Throws(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or ArgumentException or System.Text.DecoderFallbackException) { return; }
        throw new InvalidOperationException(message);
    }
}
