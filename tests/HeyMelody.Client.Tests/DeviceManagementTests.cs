using HeyMelodyWindows.App;

internal static class DeviceManagementTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("management initialization accepts already enumerated offline devices without another query", async () =>
        {
            var service = new Service();
            await using var vm = new MainViewModel(service, new Confirmations());
            UiDevice[] devices = [new("cached-offline", "离线的蓝牙音频设备", false)];
            await vm.InitializeAsync(devices);
            Check(ReferenceEquals(vm.Devices, devices) && vm.SelectedDevice == devices[0]
                && service.Enumerations == 0 && service.Connections == 0 && !vm.HasVerifiedSnapshot,
                "The startup coordinator's validated device list is adopted once, including offline entries.");
        });
        await test("rejecting management discard leaves the verified session and draft intact", async () =>
        {
            var service = new Service(); var confirmations = new Confirmations(false);
            await using var vm = await Connected(service, confirmations);
            vm.EqName = "不应丢弃"; vm.Bands[0].Gain = 4;
            Check(!await vm.PrepareDeviceManagementAsync(), "Rejected discard must stop the manager launch.");
            Check(confirmations.Calls == 1 && confirmations.Titles.Single() == "管理设备"
                && service.Disconnections == 0 && service.Enumerations == 1,
                "Opening management uses the real discard confirmation without enumeration or disconnect on rejection.");
            Check(vm.SelectedDevice?.Identity == "device-a" && vm.HasVerifiedSnapshot && vm.EqDirty
                && vm.EqName == "不应丢弃" && vm.Bands[0].Gain == 4 && vm.CanManageDevices && !vm.ManagingDevices,
                "A cancelled management request preserves the original device and draft and releases its lifecycle guard.");
        });
        await test("management waits for a running read and cancels queued settings before opening", async () =>
        {
            var service = new Service { BlockNextRead = true };
            await using var vm = await Connected(service);
            var poll = vm.PollAsync();
            await service.ReadStarted.Task.WaitAsync(Deadline);
            var write = vm.ToggleGameCommand.ExecuteAsync(true);
            var preparation = vm.PrepareDeviceManagementAsync();
            Check(!preparation.IsCompleted && !vm.CanManageDevices && !vm.CanSetGameMode
                && service.Disconnections == 0, "Preparation blocks new settings and waits for the current RPC.");
            Check(!await vm.PrepareDeviceManagementAsync(), "A second preparation cannot overlap the first.");
            service.ReadRelease.TrySetResult();
            Check(await preparation.WaitAsync(Deadline), "Preparation succeeds after the current read completes.");
            await Task.WhenAll(poll, write).WaitAsync(Deadline);
            Check(service.Actions.Count == 0 && service.Disconnections == 1 && !vm.HasVerifiedSnapshot
                && vm.SelectedDevice is null && vm.ManagingDevices,
                "Old queued settings never run and the old session is cleared before management starts.");
            Check(await vm.CompleteDeviceManagementAsync(saved: false), "The original effective list can be restored.");
        });
        await test("cancelled management restores cached selection without querying or reconnecting", async () =>
        {
            var service = new Service();
            await using var vm = await Connected(service);
            await vm.SelectDeviceAsync(vm.Devices[1]); await vm.ConnectCommand.ExecuteAsync(null);
            var original = vm.Devices; var selected = vm.SelectedDevice;
            Check(await vm.PrepareDeviceManagementAsync(), "A clean session can enter management.");
            await vm.ToggleGameCommand.ExecuteAsync(true);
            await vm.ConnectCommand.ExecuteAsync(null);
            await vm.RefreshDevicesCommand.ExecuteAsync(null);
            await vm.SelectDeviceAsync(original[0]);
            await vm.PollAsync();
            Check(vm.ManagingDevices && !vm.CanConnect && !vm.CanRefreshState && !vm.CanRefreshDevices
                && !vm.CanEditEq && !vm.CanManageDevices && service.Enumerations == 1
                && service.Connections == 2 && service.Disconnections == 2 && service.Actions.Count == 0,
                "The main control surface stays blocked for the full manager lifetime, including direct command execution.");
            Check(await vm.CompleteDeviceManagementAsync(saved: false), "Cancellation returns the effective cached list.");
            Check(ReferenceEquals(vm.Devices, original) && ReferenceEquals(vm.SelectedDevice, selected)
                && !vm.ManagingDevices && vm.CanManageDevices && vm.CanConnect && !vm.HasVerifiedSnapshot
                && service.Enumerations == 1 && service.Connections == 2,
                "Cancellation restores the original selection and requires an explicit reconnect without enumeration.");
        });
        await test("saving management preserves the prior identity with one enumeration and no second discard", async () =>
        {
            var service = new Service(); var confirmations = new Confirmations(true);
            await using var vm = await Connected(service, confirmations);
            await vm.SelectDeviceAsync(vm.Devices[1]); await vm.ConnectCommand.ExecuteAsync(null);
            vm.EqName = "进入时确认丢弃"; vm.Bands[0].Gain = 4;
            Check(await vm.PrepareDeviceManagementAsync() && !vm.EqDirty, "Approved preparation discards the EQ draft once.");
            service.Devices = [new("device-a", "第一副", false), new("device-b", "重命名后的第二副", false)];
            Check(await vm.CompleteDeviceManagementAsync(saved: true), "A saved offline list remains effective.");
            Check(vm.SelectedDevice?.Identity == "device-b" && vm.Title == "重命名后的第二副"
                && service.Enumerations == 2 && service.Connections == 2 && service.Disconnections == 2
                && confirmations.Calls == 1 && service.Actions.Count == 0 && !vm.HasVerifiedSnapshot && !vm.EqDirty,
                "The saved list is enumerated once, restores identity despite changed names, and never reconnects or confirms twice.");
        });
        await test("saving removal of the old device selects the first remaining entry without reconnecting", async () =>
        {
            var service = new Service();
            await using var vm = await Connected(service);
            Check(await vm.PrepareDeviceManagementAsync(), "Preparation succeeds.");
            service.Devices = [new("device-b", "保留的离线设备", false)];
            Check(await vm.CompleteDeviceManagementAsync(saved: true), "The remaining paired offline device is valid.");
            Check(vm.SelectedDevice?.Identity == "device-b" && service.Enumerations == 2 && service.Connections == 1
                && service.Disconnections == 1 && !vm.HasVerifiedSnapshot && !vm.CanSetGameMode,
                "Removing the old device cannot retain its control session or reconnect to the replacement.");
            Check(!await vm.CompleteDeviceManagementAsync(saved: true) && service.Enumerations == 2,
                "A duplicate completion cannot enumerate a second time.");
        });
        await test("a failed disconnect returns false and does not retain the management guard", async () =>
        {
            var service = new Service { FailDisconnect = true };
            await using var vm = await Connected(service);
            Check(!await vm.PrepareDeviceManagementAsync(), "Failed preparation must prevent the manager launch.");
            Check(!vm.ManagingDevices && vm.CanManageDevices && !vm.Busy && vm.StatusWarning
                && !vm.HasVerifiedSnapshot && service.Disconnections == 1 && service.Enumerations == 1,
                "A disconnect failure is visible and releases the guard without exposing a verified old session.");
            Check(!await vm.CompleteDeviceManagementAsync(saved: false), "Failed preparation owns no manager completion.");
        });
        await test("empty or failed saved enumeration cannot expose a removed cached device", async () =>
        {
            foreach (var fail in new[] { false, true })
            {
                var service = new Service();
                await using var vm = await Connected(service);
                Check(await vm.PrepareDeviceManagementAsync(), "Preparation succeeds.");
                service.Devices = []; service.FailEnumeration = fail;
                Check(!await vm.CompleteDeviceManagementAsync(saved: true), "No effective saved list is reported as failure.");
                Check(!vm.HasDevices && vm.SelectedDevice is null && !vm.HasVerifiedSnapshot && vm.StatusWarning
                    && !vm.ManagingDevices && vm.CanManageDevices && service.Enumerations == 2 && service.Connections == 1,
                    "Empty or failed results clear cached old devices and allow recovery without reconnecting.");
            }
        });
        await test("closing while a manager is open disposes without waiting for external management", async () =>
        {
            var service = new Service();
            await using var vm = await Connected(service);
            Check(await vm.PrepareDeviceManagementAsync(), "Preparation succeeds.");
            Check(await vm.RequestCloseAsync().WaitAsync(Deadline), "An external manager does not hold the RPC queue open.");
            Check(service.Disposals == 1 && !vm.ManagingDevices && !vm.Busy && !vm.CanManageDevices,
                "Shutdown clears management state and disposes the backend exactly once.");
            Check(!await vm.CompleteDeviceManagementAsync(saved: true) && service.Enumerations == 1,
                "A late manager return cannot repopulate a closed client or enumerate devices.");
        });
        await test("closing during management discard cancels that dialog before the ordinary close confirmation", async () =>
        {
            var service = new Service(); var confirmations = new ClosingConfirmations();
            await using var vm = await Connected(service, confirmations);
            vm.EqName = "关闭前未保存";
            var preparation = vm.PrepareDeviceManagementAsync();
            await confirmations.ManagementEntered.Task.WaitAsync(Deadline);
            var closing = vm.RequestCloseAsync();
            Check(!await preparation.WaitAsync(Deadline) && await closing.WaitAsync(Deadline),
                "Close supersedes pending management consent and does not deadlock on dialog ownership.");
            Check(confirmations.Calls == 2 && service.Disconnections == 0 && service.Disposals == 1
                && !vm.ManagingDevices && service.Actions.Count == 0,
                "Only close consent finishes shutdown; stale management approval cannot disconnect or launch later.");
        });
    }

    private static async Task<MainViewModel> Connected(Service service, IConfirmationService? confirmations = null)
    {
        var vm = new MainViewModel(service, confirmations ?? new Confirmations());
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        Check(vm.HasVerifiedSnapshot, "The fake connection must establish a verified headset session.");
        return vm;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Confirmations(bool response = true) : IConfirmationService
    {
        public int Calls { get; private set; }
        public List<string> Titles { get; } = [];
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        { Calls++; Titles.Add(title); return Task.FromResult(response); }
    }

    private sealed class ClosingConfirmations : IConfirmationService
    {
        public int Calls { get; private set; }
        public TaskCompletionSource ManagementEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _managementAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        {
            Calls++;
            if (title != "管理设备") return Task.FromResult(true);
            ManagementEntered.TrySetResult();
            return _managementAnswer.Task.WaitAsync(token);
        }
    }

    private sealed class Service : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        private string _identity = "device-a";
        public UiDevice[] Devices { get; set; } = [new("device-a", "OPPO Enco X4", true), new("device-b", "第二副耳机", false)];
        public int Enumerations, Connections, Disconnections, Disposals;
        public bool FailEnumeration, FailDisconnect, BlockNextRead;
        public List<UiAction> Actions { get; } = [];
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        {
            Enumerations++;
            return FailEnumeration ? Task.FromException<IReadOnlyList<UiDevice>>(new IOException("test-enumeration-failure"))
                : Task.FromResult<IReadOnlyList<UiDevice>>(Devices);
        }
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        { Connections++; _identity = identity; return Adjust(await _inner.ConnectAsync(identity, token)); }
        public Task DisconnectAsync(CancellationToken token)
        {
            Disconnections++;
            return FailDisconnect ? Task.FromException(new IOException("test-disconnect-failure")) : Task.CompletedTask;
        }
        public async Task<UiSnapshot> RefreshAsync(CancellationToken token)
        {
            if (BlockNextRead)
            {
                BlockNextRead = false;
                ReadStarted.TrySetResult();
                await ReadRelease.Task.WaitAsync(token);
            }
            return Adjust(await _inner.RefreshAsync(token));
        }
        private UiSnapshot Adjust(UiSnapshot snapshot)
        {
            snapshot.Identity = _identity;
            snapshot.DeviceName = Devices.Single(device => device.Identity == _identity).Name;
            return snapshot;
        }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Actions.Add(action); throw new InvalidOperationException("Device management must not write headset settings."); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
