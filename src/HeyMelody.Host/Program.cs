using Avalonia;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Workers never initialize Avalonia or acquire the tray singleton.
        if (args is ["--worker"]) return NativeWorker.RunAsync().GetAwaiter().GetResult();
        if (args is ["--catalog-worker"]) return NativeWorker.EnumerateAsync().GetAwaiter().GetResult();
        if (args.Length != 0 || !OperatingSystem.IsWindows()) return 2;
        using var singleton = new Mutex(false, BackendEndpoint.HostMutexName);
        bool owner;
        try { owner = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { owner = true; }
        if (!owner) return 0;
        try { return AppBuilder.Configure<TrayApplication>().UsePlatformDetect().UseHeyMelodyRendering()
            .UseHeyMelodyTypography(bundledFallback: false).LogToTrace().StartWithClassicDesktopLifetime(args); }
        catch (Exception) { return 1; }
        finally { singleton.ReleaseMutex(); }
    }
}
