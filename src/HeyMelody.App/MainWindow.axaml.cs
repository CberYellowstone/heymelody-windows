using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;

namespace HeyMelodyWindows.App;

public partial class MainWindow : FAAppWindow
{
    private readonly MainViewModel _viewModel;
    private readonly IHeadsetService _service;
    private readonly TrackedDeviceManager? _manager;
    private readonly CancellationTokenSource _managerLifetime = new();
    private readonly WindowConfirmationService _confirmations;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _closing, _closed, _opened, _managing;

    public MainWindow() : this(HeadsetServiceFactory.Create(), false,
        OperatingSystem.IsWindows() ? new DeviceManagerProcess() : null) { }

    internal MainWindow(IHeadsetService service, bool preview) : this(service, preview, null) { }

    internal MainWindow(IHeadsetService service, bool preview, IDeviceManagerProcess? manager)
    {
        InitializeComponent();
        _service = service;
        _manager = manager is null ? null : new TrackedDeviceManager(manager);
        _confirmations = new WindowConfirmationService(this);
        _viewModel = new MainViewModel(service, _confirmations, preview);
        Page.DataContext = _viewModel;
        Page.SetPlatformNotice(!OperatingSystem.IsWindows());
        Page.ManageDevicesRequested = manager is not null ? ManageDevicesAsync : null;
        Page.IsEnabled = manager is null;
        StartupOverlay.IsVisible = manager is not null;
        _poll.Tick += async (_, _) => await _viewModel.PollAsync();
        Opened += async (_, _) => await InitializeOnceAsync();
        Closing += OnClosing;
    }

    private async Task InitializeOnceAsync()
    {
        if (_opened) return;
        _opened = true;
        try
        {
            if (_manager is null) await _viewModel.InitializeAsync();
            else
            {
                var devices = await ResolveDevicesAsync();
                if (devices is null) { if (!_closing) Close(); return; }
                await _viewModel.InitializeAsync(devices);
            }
            if (!_closing) { StartupOverlay.IsVisible = false; Page.IsEnabled = true; _poll.Start(); }
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
    }

    private async Task<IReadOnlyList<UiDevice>?> ResolveDevicesAsync()
    {
        var resolver = new DeviceStartupCoordinator(_service, _manager!);
        while (!_closing)
        {
            try { return await resolver.ResolveAsync(_managerLifetime.Token); }
            catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                if (_closing) return null;
                var recovery = await _viewModel.Dialogs.RunAsync(token => _confirmations.RecoverDevicesAsync(token),
                    _managerLifetime.Token);
                if (recovery == FAContentDialogResult.Primary) continue;
                if (recovery != FAContentDialogResult.Secondary) return null;
                // Explicit recovery also allows repairing a damaged selection file.
                try
                {
                    if (await _manager!.RunAsync(_managerLifetime.Token) == DeviceManagerOutcome.Cancelled) return null;
                }
                catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { throw; }
                catch (Exception) { }
            }
        }
        return null;
    }

    internal async Task ManageDevicesAsync()
    {
        if (_manager is null || _closing || _managing || !_viewModel.CanManageDevices) return;
        _managing = true;
        var originalIdentity = _viewModel.SelectedDevice?.Identity;
        var prepared = false;
        try
        {
            prepared = await _viewModel.PrepareDeviceManagementAsync();
            if (!prepared || _closing) return;
            Page.IsEnabled = false;
            DeviceManagerOutcome result;
            while (true)
            {
                try { result = await _manager.RunAsync(_managerLifetime.Token); break; }
                catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    if (_closing) return;
                    var retry = await _viewModel.Dialogs.RunAsync(token => _confirmations.RetryManagerAsync(token),
                        _managerLifetime.Token);
                    if (retry) continue;
                    result = DeviceManagerOutcome.Cancelled;
                    break;
                }
            }
            if (_closing) return;
            var completed = await _viewModel.CompleteDeviceManagementAsync(result == DeviceManagerOutcome.Saved);
            prepared = false;
            if (completed) return;
            if (result == DeviceManagerOutcome.Cancelled) { Close(); return; }
            // Retry only after an actual save race or enumeration failure.
            var devices = await ResolveDevicesAsync();
            if (devices is null) { if (!_closing) Close(); return; }
            await _viewModel.InitializeAsync(devices);
            if (devices.FirstOrDefault(device => device.Identity == originalIdentity) is { } original)
                await _viewModel.SelectDeviceAsync(original);
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
        finally
        {
            if (prepared && !_closing) await _viewModel.CompleteDeviceManagementAsync(saved: false);
            _managing = false;
            if (!_closing) Page.IsEnabled = true;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _viewModel.SuspendActionsForClose();
        _confirmations.CancelActiveDialog();
        await Page.DismissSettingsDialogAsync();
        if (!await _viewModel.RequestCloseAsync())
        {
            _closing = false; Page.ResumeSettingsDialogs(); return;
        }
        _managerLifetime.Cancel();
        if (_manager?.CurrentOperation is { } operation)
        {
            try { await operation; }
            catch (Exception) { }
        }
        _poll.Stop();
        await _viewModel.DisposeAsync();
        _closed = true;
        Dispatcher.UIThread.Post(Close);
    }
}

internal sealed class WindowConfirmationService(TopLevel owner) : IConfirmationService
{
    private FAContentDialog? _activeDialog;
    public void CancelActiveDialog() => _activeDialog?.Hide();
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) =>
        ShowConfirmationAsync(title, message, "继续", token);

    public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) =>
        ShowConfirmationAsync(title, message, "确认", token);

    public Task<FAContentDialogResult> RecoverDevicesAsync(CancellationToken token) => ShowDialogAsync(new FAContentDialog
    {
        Title = "无法读取设备列表",
        Content = "无法检查蓝牙配对记录或设备选择。请确认蓝牙可用，并已完整解压客户端后重试。原来的设备选择不会被清空。",
        PrimaryButtonText = "重试", SecondaryButtonText = "重新选择设备", CloseButtonText = "退出", DefaultButton = FAContentDialogButton.Primary
    }, token);

    public Task<bool> RetryManagerAsync(CancellationToken token) => ShowConfirmationAsync("无法打开设备管理",
        "设备管理组件缺失或未正常完成。请确认已完整解压客户端后重试。", "重试", token);

    public async Task AlertAsync(string title, string message, CancellationToken token) =>
        _ = await ShowDialogAsync(CreateAlert(title, message), token);

    private async Task<bool> ShowConfirmationAsync(string title, string message, string primaryText, CancellationToken token) =>
        await ShowDialogAsync(CreateConfirmation(title, message, primaryText), token) == FAContentDialogResult.Primary
            && !token.IsCancellationRequested;

    internal static FAContentDialog CreateConfirmation(string title, string message, string primaryText) =>
        new()
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Close
        };

    internal static FAContentDialog CreateAlert(string title, string message) =>
        new()
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            CloseButtonText = "确定",
            DefaultButton = FAContentDialogButton.Close
        };

    private async Task<FAContentDialogResult> ShowDialogAsync(FAContentDialog dialog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _activeDialog = dialog;
        using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Hide));
        try { return await dialog.ShowAsync(owner); }
        finally { _activeDialog = null; }
    }
}
