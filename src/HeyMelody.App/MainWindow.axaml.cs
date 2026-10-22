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
    private readonly IHeadsetEventService? _events;
    private Task? _exitVote;
    private Task? _calibrationOperation;
    private bool _closing, _closed, _opened, _managing;
    internal bool StartWithDemo { get; init; }

    public MainWindow() : this(HeadsetServiceFactory.Create(), false,
        OperatingSystem.IsWindows() ? new DeviceManagerProcess() : null) { }

    internal MainWindow(IHeadsetService service, bool preview) : this(service, preview, null) { }

    internal MainWindow(IHeadsetService service, bool preview, IDeviceManagerProcess? manager)
    {
        InitializeComponent();
        Ui.Typography.ApplyTextOptions(this);
        Ui.RenderDiagnostics.Attach(this);
        AnimationYieldTriggers.AttachWindow(this);
        _service = service;
        _events = service as IHeadsetEventService;
        _manager = manager is null ? null : new TrackedDeviceManager(manager);
        _confirmations = new WindowConfirmationService(this);
        _viewModel = new MainViewModel(service, _confirmations, preview);
        Page.DataContext = _viewModel;
        Page.SetPlatformNotice(!OperatingSystem.IsWindows());
        Page.ManageDevicesRequested = manager is not null ? ManageDevicesAsync : null;
        if (!preview && service is ICalibrationService calibration)
        {
            Page.RequestFitTest = () => LaunchCalibrationAsync("fit", calibration);
            Page.RequestHearingSetup = () => LaunchCalibrationAsync("hearing", calibration);
        }
        Page.IsEnabled = manager is null;
        StartupOverlay.IsVisible = manager is not null;
        Opened += async (_, _) => await InitializeOnceAsync();
        Closing += OnClosing;
        if (_events is not null) _events.EventReceived += OnBackendEvent;
        Closed += (_, _) => { if (_events is not null) _events.EventReceived -= OnBackendEvent; };
    }

    private async Task InitializeOnceAsync()
    {
        if (_opened) return;
        _opened = true;
        try
        {
            await _viewModel.AttachBackendAsync();
            if (_manager is null) await _viewModel.InitializeAsync();
            else
            {
                var devices = await ResolveDevicesAsync();
                if (devices is null) { if (!_closing) Close(); return; }
                await _viewModel.InitializeAsync(devices);
            }
            if (!_closing && StartWithDemo) await _viewModel.AddDemoDeviceAsync();
            if (!_closing) { StartupOverlay.IsVisible = false; Page.IsEnabled = true; }
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing) await _viewModel.Dialogs.RunAsync(token => _confirmations.AlertAsync(
                "无法连接蓝牙后台", error is InvalidDataException && error.Message.Contains("版本", StringComparison.Ordinal)
                    ? "蓝牙后台版本不一致，请完整更新客户端后重新启动后台。"
                    : "蓝牙后台未能启动或连接。请尝试重启软件或重新安装。", token), _managerLifetime.Token);
            if (!_closing) Close();
        }
    }

    private void OnBackendEvent(BackendEvent notification) => Dispatcher.UIThread.Post(async () =>
    {
        if (_closed) return;
        try
        {
            _viewModel.AdoptBackendEvent(notification);
            switch (notification.Kind)
            {
                case "activate": ActivateMainUi(); break;
                case "requestExit" when notification.ExitRequestId is { } requestId && _events is not null:
                    if (_exitVote is null)
                    {
                        _exitVote = VoteForBackendExitAsync(requestId);
                        try { await _exitVote; }
                        finally { _exitVote = null; }
                    }
                    break;
                case "exitCancelled":
                    if (!_closing) Page.ResumeSettingsDialogs();
                    break;
                case "stopping": Close(); break;
            }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception)
        {
            if (!_closing)
            {
                _viewModel.CancelBackendExit();
                Page.ResumeSettingsDialogs();
            }
        }
    });

    internal void ActivateMainUi()
    {
        if (_closing || _closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private async Task VoteForBackendExitAsync(string requestId)
    {
        ActivateMainUi();
        _viewModel.SuspendActionsForClose();
        _confirmations.CancelActiveDialog();
        await Page.DismissSettingsDialogAsync();
        var approved = await _viewModel.ConfirmBackendExitAsync();
        try { await _events!.AcknowledgeExitAsync(requestId, approved, _managerLifetime.Token); }
        catch { _viewModel.CancelBackendExit(); Page.ResumeSettingsDialogs(); throw; }
        if (!approved) Page.ResumeSettingsDialogs();
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
            ManagerOverlay.IsVisible = true;
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
            ManagerOverlay.IsVisible = false;
            if (!_closing) Page.IsEnabled = true;
        }
    }

    private async Task LaunchCalibrationAsync(string kind, ICalibrationService service)
    {
        if (_closing || _calibrationOperation is not null) return;
        if (_viewModel.IsDemoSelected)
        {
            _viewModel.ShowDemoFeedback(kind == "fit" ? "耳塞贴合度测试" : "设置黄金听感");
            return;
        }
        var operation = RunCalibrationAsync(kind, service);
        _calibrationOperation = operation;
        try { await operation; }
        finally { if (ReferenceEquals(_calibrationOperation, operation)) _calibrationOperation = null; }
    }
    private async Task RunCalibrationAsync(string kind, ICalibrationService service)
    {
        UiCalibrationState? state = null;
        try
        {
            state = await _viewModel.CreateCalibrationAsync(kind);
            await new CalibrationProcess().RunAsync(kind, state.Handle, service, _managerLifetime.Token);
            if (!_closing) await _viewModel.RefreshStateCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closing) await _viewModel.Dialogs.RunAsync(token => _confirmations.AlertAsync("无法打开测试", "测试组件未能正常启动或完成，请检查耳机连接及客户端文件后重试。", token), _managerLifetime.Token);
        }
        finally
        {
            if (state is not null) { try { await service.CancelCalibrationAsync(state.Handle, CancellationToken.None); } catch { } }
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
        if (_calibrationOperation is { } calibration)
        { try { await calibration; } catch { } }
        if (_manager?.CurrentOperation is { } operation)
        {
            try { await operation; }
            catch (Exception) { }
        }
        await _viewModel.DisposeAsync();
        Page.VisualCatalog.Dispose();
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
        Content = "无法检查蓝牙配对记录或设备选择。请确认蓝牙可用，并重启软件或重新安装。原来的设备选择不会被清空。",
        PrimaryButtonText = "重试", SecondaryButtonText = "重新选择设备", CloseButtonText = "退出", DefaultButton = FAContentDialogButton.Primary
    }, token);

    public Task<bool> RetryManagerAsync(CancellationToken token) => ShowConfirmationAsync("无法打开设备管理",
        "设备管理组件缺失或未正常完成。请确认已完整安装客户端后重试。", "重试", token);

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
