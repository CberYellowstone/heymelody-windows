using Avalonia;
using Avalonia.Headless;
using HeyMelodyWindows.Host;

namespace HeyMelodyWindows.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (PreviewOptions.TryParse(args, out var preview, out var error))
        {
            if (error is not null) { Console.Error.WriteLine(error); return 2; }
            if (preview!.Live && !OperatingSystem.IsWindows())
            { Console.Error.WriteLine("真实只读预览需要 Windows 蓝牙后台。"); return 2; }
            return PreviewRenderer.Run(preview);
        }
        if (args.Length != 0) { Console.Error.WriteLine(PreviewOptions.Usage); return 2; }
        if (OperatingSystem.IsWindows())
        {
            using var mainInstance = new Mutex(initiallyOwned: false, BackendEndpoint.MainMutexName);
            var ownsMain = false;
            try
            {
                try { ownsMain = mainInstance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMain = true; }
                if (!ownsMain)
                {
                    try { ActivateExistingAsync().GetAwaiter().GetResult(); return 0; }
                    catch { return 2; }
                }
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally { if (ownsMain) mainInstance.ReleaseMutex(); }
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task ActivateExistingAsync()
    {
        await using var service = HeadsetServiceFactory.Create();
        if (service is not IHeadsetEventService events) return;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await events.ActivateMainUiAsync(budget.Token);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect().LogToTrace();

    public static AppBuilder BuildHeadlessApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
