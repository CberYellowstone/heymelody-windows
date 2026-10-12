using Avalonia;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.DeviceManager;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--ui-preview"]) DeviceManagerApp.Preview = true;
        else if (args.Length != 0) return 2;
        if (!DeviceManagerApp.Preview && !OperatingSystem.IsWindows()) return 2;
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception) { return 2; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DeviceManagerApp>().UsePlatformDetect()
        .UseHeyMelodyRendering().UseHeyMelodyTypography().LogToTrace();
}
