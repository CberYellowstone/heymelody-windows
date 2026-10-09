using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Windowing;

namespace HeyMelodyWindows.DeviceManager;

public partial class DeviceManagerWindow : FAAppWindow
{
    private readonly DeviceManagerViewModel _viewModel;
    private readonly MicaAltBackdrop _backdrop;
    private readonly IHeadsetEventService? _events;
    private bool _closing, _closed;

    public DeviceManagerWindow() : this(new ClientBridge(), false) { }
    internal DeviceManagerWindow(IDeviceManagementService service, bool preview)
    {
        _backdrop = new MicaAltBackdrop(this);
        InitializeComponent();
        if (preview) Title = "设备管理 · 界面预览";
        _viewModel = new DeviceManagerViewModel(service);
        _events = service as IHeadsetEventService;
        if (_events is not null) _events.EventReceived += OnBackendEvent;
        Page.DataContext = _viewModel;
        _viewModel.ExitRequested += () => Dispatcher.UIThread.Post(Close);
        Opened += async (_, _) => await _viewModel.RefreshAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            if (_events is not null) _events.EventReceived -= OnBackendEvent;
            _backdrop.Dispose();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(_viewModel.ExitCode);
        };
    }

    private void OnBackendEvent(BackendEvent notification) => Dispatcher.UIThread.Post(async () =>
    {
        if (_closing || _closed) return;
        try
        {
            _viewModel.AdoptBackendEvent(notification);
            if (notification.Kind == "requestExit" && notification.ExitRequestId is { } requestId)
            {
                await _viewModel.PrepareBackendExitAsync();
                if (!_closing) await _events!.AcknowledgeExitAsync(requestId, true, CancellationToken.None);
            }
            else if (notification.Kind == "exitCancelled") _viewModel.CancelBackendExit();
            else if (notification.Kind == "stopping") Close();
        }
        catch { if (!_closing) _viewModel.CancelBackendExit(); }
    });
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try { await _viewModel.DisposeAsync(); }
        finally { _closed = true; Dispatcher.UIThread.Post(Close); }
    }
}
