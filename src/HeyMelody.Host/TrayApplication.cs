using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAvalonia.Styling;

namespace HeyMelodyWindows.Host;

internal sealed class TrayApplication : Application
{
    private BackendSupervisor? _supervisor;
    private BackendPipeServer? _server;
    private WindowsStateMonitor? _monitor;
    private TrayIcon? _tray;
    private bool _exiting;

    public override void Initialize() => Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = true, PreferUserAccentColor = true });

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _supervisor = new(); _server = new(_supervisor); _monitor = new(_supervisor);
            var menu = new NativeMenu();
            var open = new NativeMenuItem("打开主界面");
            open.Click += (_, _) => { try { _server.ActivateMain(true); } catch { _supervisor.ReportHealth("无法打开主界面，请重新解压完整客户端。"); } };
            var exit = new NativeMenuItem("退出");
            exit.Click += async (_, _) => await _server.RequestExitAsync();
            menu.Add(open); menu.Add(exit);
            _tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://HeyMelody.Host/Assets/HeyMelody.ico"))),
                ToolTipText = "HeyMelody", Menu = menu, IsVisible = true };
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            _server.ExitApproved += async () =>
            {
                _exiting = true;
                await Dispatcher.UIThread.InvokeAsync(() => _monitor.Dispose());
                await _server.DisposeAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { _tray.Dispose(); desktop.Shutdown(); });
            };
            desktop.ShutdownRequested += (_, args) =>
            {
                if (_exiting) return;
                args.Cancel = true; _ = _server.RequestExitAsync();
            };
            desktop.Exit += (_, _) => { _monitor.Dispose(); _tray.Dispose(); };
            _server.Start();
            _ = StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
    private async Task StartAsync()
    {
        try { await _monitor!.StartAsync(); await _supervisor!.InitializeAsync(CancellationToken.None); }
        catch (Exception) { _supervisor!.ReportHealth("无法初始化蓝牙设备监听，请检查蓝牙状态。托盘继续运行。"); }
    }
}
