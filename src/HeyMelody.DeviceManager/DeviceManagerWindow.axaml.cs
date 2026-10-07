using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.DeviceManager;

public partial class DeviceManagerWindow : FAAppWindow
{
    private readonly DeviceManagerViewModel _viewModel;
    private readonly MicaAltBackdrop _backdrop;
    private bool _closing, _closed;

    public DeviceManagerWindow() : this(new ClientBridge(), false) { }
    internal DeviceManagerWindow(IDeviceManagementService service, bool preview)
    {
        _backdrop = new MicaAltBackdrop(this);
        InitializeComponent();
        if (preview) Title = "设备管理 · 界面预览";
        _viewModel = new DeviceManagerViewModel(service);
        Page.DataContext = _viewModel;
        _viewModel.ExitRequested += () => Dispatcher.UIThread.Post(Close);
        Opened += async (_, _) => await _viewModel.RefreshAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _backdrop.Dispose();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(_viewModel.ExitCode);
        };
    }
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
