using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.DeviceManager;

public partial class DeviceManagerApp : Application
{
    internal static bool Preview { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ThemePreferenceReader.Load() switch
        { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new DeviceManagerWindow(Preview
                ? new PreviewDeviceManagementService() : new ClientBridge(), Preview);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
