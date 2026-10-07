using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Windowing;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Calibration.UI;

public sealed class CalibrationApplication : Application
{
    public static string Kind { get; private set; } = "fit";
    public static string Handle { get; private set; } = "";
    public static int Run(string kind, string[] args)
    {
        if (args is not ["--handle", var handle] || string.IsNullOrWhiteSpace(handle) || handle.Length > 128 || !OperatingSystem.IsWindows()) return 2;
        Kind = kind; Handle = handle;
        try { return AppBuilder.Configure<CalibrationApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args); }
        catch { return 2; }
    }
    public override void Initialize() => Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = true, PreferUserAccentColor = true });
    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ThemePreferenceReader.Load() switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        { desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; desktop.MainWindow = new CalibrationWindow(new ClientBridge(), Handle, Kind); }
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class CalibrationWindow : FAAppWindow
{
    private readonly CalibrationViewModel _model;
    private readonly ICalibrationService _service;
    private readonly IHeadsetEventService? _events;
    private bool _closing, _closed, _completed;
    public CalibrationWindow(ICalibrationService service, string handle, string kind)
    {
        _service = service; _events = service as IHeadsetEventService;
        _model = new CalibrationViewModel(service, handle, kind);
        Title = kind == "fit" ? "耳塞贴合度测试" : "设置黄金听感";
        Width = 540; Height = 580; MinWidth = 440; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var view = new CalibrationView(_model); Content = view;
        view.CloseRequested += () => { Close(); return Task.CompletedTask; };
        if (_events is not null) _events.EventReceived += OnEvent;
        Opened += async (_, _) =>
        {
            try { if (_events is not null) await _events.SubscribeAsync("calibration", CancellationToken.None); await _model.InitializeAsync(); }
            catch { _model.BackendUnavailable("无法连接蓝牙后台，请关闭此窗口后重试。"); }
        };
        Closing += ClosingAsync;
        Closed += (_, _) =>
        {
            if (_events is not null) _events.EventReceived -= OnEvent;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(_completed ? 0 : 1);
        };
    }
    private void OnEvent(BackendEvent notification) => Dispatcher.UIThread.Post(async () =>
    {
        if (_closed) return;
        if (notification.Calibration is { } state) _model.Adopt(state);
        if (notification.Resources is { } resources) _model.SetResourceSource(resources.Source);
        if (notification.Kind is "unavailable" or "stopping") { _model.BackendUnavailable(notification.Message ?? ""); Close(); }
        else if (notification.Kind == "requestExit" && notification.ExitRequestId is { } requestId && _events is not null)
        {
            try
            {
                await _model.CancelAsync();
                await _events.AcknowledgeExitAsync(requestId, true, CancellationToken.None);
            }
            catch { Close(); }
        }
    });
    private async void ClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return; _closing = true; _completed = _model.State.Stage == "Completed";
        try { await _model.DisposeAsync(); if (_service is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
        finally { _closed = true; Dispatcher.UIThread.Post(Close); }
    }
}
