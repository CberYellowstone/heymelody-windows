namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private bool _managingDevices, _completingDeviceManagement;
    private IReadOnlyList<UiDevice>? _managementDevices;
    private string? _managementSelectedIdentity;

    public bool ManagingDevices => _managingDevices;
    public bool CanManageDevices => !Closing && !_lifecyclePending;

    /// <summary>Disconnects safely and keeps control operations disabled until management completes.</summary>
    public async Task<bool> PrepareDeviceManagementAsync()
    {
        if (!CanManageDevices) return false;
        _lifecyclePending = true;
        _operations.CancelQueuedWrites();
        NotifyState();
        var prepared = false;
        try
        {
            await _operations.RunAsync(async () =>
            {
                if (Closing) return;
                var devices = Devices;
                var identity = SelectedDevice?.Identity;
                if (!await ChangeSelectedDeviceAsync(null, "管理设备",
                    "管理设备需要先断开客户端，并丢弃尚未保存的均衡器调整。继续吗？")) return;
                if (Closing) return;
                _managementDevices = devices;
                _managementSelectedIdentity = identity;
                _managingDevices = prepared = true;
                SetStatus("正在管理设备，耳机控制已暂停。");
            }, OperationCoordinator.Lifecycle, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!Closing)
            {
                InvalidateSnapshot();
                SetStatus("无法断开耳机，设备管理尚未打开。请重试。", true);
            }
        }
        finally
        {
            if (!prepared) ClearDeviceManagement();
            NotifyState();
        }
        return prepared && !Closing;
    }

    /// <summary>Restores cached devices on cancellation, or enumerates the saved list once; never reconnects.</summary>
    public async Task<bool> CompleteDeviceManagementAsync(bool saved)
    {
        if (!_managingDevices || _completingDeviceManagement || Closing) return false;
        _completingDeviceManagement = true;
        var completed = false;
        try
        {
            await _operations.RunAsync(async () =>
            {
                if (Closing) return;
                var devices = saved ? await _service.EnumerateAsync(_lifetime.Token) : _managementDevices!;
                if (Closing) return;
                _devices = devices;
                _selectedDevice = devices.FirstOrDefault(device => device.Identity == _managementSelectedIdentity)
                    ?? devices.FirstOrDefault();
                _title = _selectedDevice?.Name ?? "选择一副耳机";
                ResetSelection();
                completed = devices.Count != 0;
                SetStatus(completed ? "选择耳机后连接" : "没有可用的白名单设备，请重新管理设备。", !completed);
            }, OperationCoordinator.Lifecycle, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!Closing)
            {
                _devices = [];
                _selectedDevice = null;
                _title = "选择一副耳机";
                ResetSelection();
                SetStatus("无法读取管理后的设备名单，请重新管理设备。", true);
            }
        }
        finally
        {
            ClearDeviceManagement();
            NotifyState();
        }
        return completed && !Closing;
    }

    // Shared with ordinary device switching so management uses the actual EQ discard confirmation.
    private async Task<bool> ChangeSelectedDeviceAsync(UiDevice? device, string title, string message)
    {
        if (device?.Identity == SelectedDevice?.Identity) return true;
        if (device is not null && !Devices.Any(candidate => candidate.Identity == device.Identity)) return false;
        var epoch = _sessionEpoch;
        if (EqDirty && !await ConfirmAsync(title, message, _sessionWork.Token)) return false;
        if (Closing || epoch != _sessionEpoch) return false;
        AdvanceSession();
        var hadSession = _sessionIdentity is not null;
        _sessionIdentity = null;
        _selectedDevice = device;
        ResetSelection();
        _title = device?.Name ?? "选择一副耳机";
        if (hadSession) await _service.DisconnectAsync(_lifetime.Token);
        SetStatus("选择耳机后连接");
        return true;
    }

    private void ClearDeviceManagement()
    {
        _managementDevices = null;
        _managementSelectedIdentity = null;
        _managingDevices = _completingDeviceManagement = false;
        _lifecyclePending = false;
    }
}
