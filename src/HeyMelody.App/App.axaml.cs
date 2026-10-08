using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace HeyMelodyWindows.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyTheme(ThemePreferences.Load());
            if (Program.IsModelDemo)
            {
                var window = new MainWindow(new UnsupportedPlatformService(), preview: true)
                {
                    Title = "HeyMelody · Enco Free4 布局演示",
                    StartWithDemo = true
                };
                window.FindControl<MainView>("Page")?.SetModelDemo();
                desktop.MainWindow = window;
            }
            else desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, _) => DeviceVisuals.DisposeResources();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(string preference) => RequestedThemeVariant = preference switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };
}
