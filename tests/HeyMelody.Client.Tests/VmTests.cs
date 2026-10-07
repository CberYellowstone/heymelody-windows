using HeyMelodyWindows.App;

internal static class VmTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("VM initialization and polling are GET-only", async () =>
        {
            var service = new FakeService(); await using var vm = Create(service);
            await vm.InitializeAsync(); Check(!vm.HasVerifiedSnapshot && vm.HasDevices);
            await vm.ConnectCommand.ExecuteAsync(null);
            await vm.PollAsync(); await vm.PollAsync();
            Check(vm.HasVerifiedSnapshot && service.Refreshes >= 2 && service.Actions.Count == 0);
            Check(vm.ProductId == "06EC10" && vm.LeftBatteryText == "90%");
        });
        await test("VM rejects a snapshot belonging to a different device", async () =>
        {
            var service = new FakeService { RefreshSnapshot = () => Snapshot("device-b") };
            await using var vm = Create(service); await Connect(vm);
            await vm.PollAsync();
            await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(!vm.HasVerifiedSnapshot && !vm.CanSetGameMode && service.Actions.Count == 0);
            Check(vm.LeftBatteryText == "—" && vm.StatusWarning);
        });
        await test("VM validates capability and unknown boolean even when ICommand checks are bypassed", async () =>
        {
            var snapshot = Snapshot(); snapshot.CanSetAnc = false; snapshot.GameMode = null;
            var service = new FakeService { ConnectSnapshot = () => snapshot };
            await using var vm = Create(service); await Connect(vm);
            await vm.ApplyAncCommand.ExecuteAsync(null); await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(!vm.CanSetAnc && !vm.CanSetGameMode && vm.GameStateText == "未确认" && service.Actions.Count == 0);
        });
        await test("VM polling preserves pending protocol IDs and dirty EQ drafts", async () =>
        {
            var service = new FakeService(); await using var vm = Create(service); await Connect(vm);
            vm.SelectedAnc = vm.AncOptions.Last(); vm.SelectedPreset = vm.PresetOptions.Last(); vm.SelectedSpatial = vm.SpatialOptions.Last();
            vm.EqName = "未保存"; vm.Bands[0].Gain = 4;
            await vm.PollAsync();
            Check(vm.SelectedAnc?.Key == 0x0107 && vm.SelectedPreset?.Key == 7 && vm.SelectedSpatial?.Key == 2);
            Check(vm.EqDirty && vm.EqName == "未保存" && vm.Bands[0].Gain == 4 && service.Actions.Count == 0);
        });
        await test("VM cancelling a device switch retains session and draft", async () =>
        {
            var service = new FakeService(); var confirmations = new Confirmations(false);
            await using var vm = Create(service, confirmations); await Connect(vm); vm.EqName = "保留调整";
            await vm.SelectDeviceAsync(vm.Devices[1]);
            Check(vm.SelectedDevice?.Identity == "device-a" && vm.HasVerifiedSnapshot && vm.EqDirty);
            Check(service.Disconnects == 0 && confirmations.Calls == 1);
        });
        await test("VM accepted device switch invalidates old state before reconnect", async () =>
        {
            var service = new FakeService(); await using var vm = Create(service, new Confirmations(true)); await Connect(vm);
            vm.Bands[0].Gain = 3; await vm.SelectDeviceAsync(vm.Devices[1]);
            Check(vm.SelectedDevice?.Identity == "device-b" && !vm.HasVerifiedSnapshot && !vm.EqDirty && service.Disconnects == 1);
            await vm.ToggleGameCommand.ExecuteAsync(true); Check(service.Actions.Count == 0);
            await vm.ConnectCommand.ExecuteAsync(null); Check(vm.HasVerifiedSnapshot && service.LastIdentity == "device-b");
        });
        await test("VM mutex cancellation sends no SET", async () =>
        {
            var service = new FakeService(); var confirmations = new Confirmations(false);
            await using var vm = Create(service, confirmations); await Connect(vm);
            vm.SelectedSpatial = vm.SpatialOptions.Last(); await vm.ApplySpatialCommand.ExecuteAsync(null);
            Check(service.Actions.Count == 0 && confirmations.Calls == 1 && vm.HiRes == true);
        });
        await test("VM confirmed mutex transition carries explicit authorization once", async () =>
        {
            var service = new FakeService(); var confirmations = new Confirmations(true);
            await using var vm = Create(service, confirmations); await Connect(vm);
            vm.SelectedSpatial = vm.SpatialOptions.Last(); await vm.ApplySpatialCommand.ExecuteAsync(null);
            Check(service.Actions.Count == 1 && service.Actions[0] is { Kind: "Spatial", Value: 2, AllowMutexChange: true });
            Check(confirmations.Calls == 1);
        });
        await test("VM unknown ACK keeps EQ dirty and never retries", async () =>
        {
            var service = new FakeService
            {
                Result = _ => new() { SubmissionAttempted = true, AcknowledgementUnknown = true, ReadbackMatches = true, After = Snapshot(), Message = "device-a AA:BB:CC:DD:EE:FF BTHENUM\\RAW" }
            };
            await using var vm = Create(service); await Connect(vm); vm.Bands[0].Gain = 4;
            await vm.SaveEqCommand.ExecuteAsync(null); await vm.PollAsync();
            Check(vm.EqDirty && vm.Bands[0].Gain == 4 && service.Actions.Count == 1);
            Check(!vm.LogText.Contains("AA:BB:CC:DD:EE:FF") && !vm.LogText.Contains("BTHENUM") && !vm.LogText.Contains("device-a"));
        });
        await test("VM verified custom EQ commit clears its draft", async () =>
        {
            var service = new FakeService(); await using var vm = Create(service); await Connect(vm);
            vm.EqName = "我的调整"; vm.Bands[0].Gain = 4;
            await vm.SaveEqCommand.ExecuteAsync(null);
            Check(!vm.EqDirty && service.Actions.Count == 1);
            Check(service.Actions[0].EqualizerEntry is { Bands.Count: 10, Name: "我的调整" });
        });
        await test("VM accepts a write during an overlapping read and serializes its submission", async () =>
        {
            var service = new FakeService { RefreshBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var vm = Create(service); await Connect(vm);
            var read = vm.RefreshStateCommand.ExecuteAsync(null); Check(vm.IsRefreshing && !vm.Busy);
            Check(vm.CanSetGameMode && vm.CanSetAnc && vm.CanEditEq);
            var write = vm.ToggleGameCommand.ExecuteAsync(true);
            await vm.PollAsync();
            Check(service.Actions.Count == 0 && service.Refreshes == 1);
            service.RefreshBlocked.SetResult(Snapshot()); await read; await write;
            Check(service.Actions.Count == 1 && service.Actions[0] is { Kind: "GameMode", Value: 1 });
            Check(!vm.Busy && !vm.IsRefreshing && vm.HasVerifiedSnapshot);
        });
        await test("VM close cancellation preserves draft and accepted close disposes once", async () =>
        {
            var service = new FakeService(); var confirmations = new Confirmations(false, true);
            var vm = Create(service, confirmations); await Connect(vm); vm.Bands[0].Gain = 2;
            Check(!await vm.RequestCloseAsync() && vm.EqDirty && service.Disposals == 0);
            Check(await vm.RequestCloseAsync() && service.Disposals == 1);
            await vm.DisposeAsync(); Check(service.Disposals == 1 && !vm.CanConnect);
        });
        await test("VM close cancels in-flight reads and releases operation ownership", async () =>
        {
            var service = new FakeService { RefreshBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var vm = Create(service); await Connect(vm); var read = vm.RefreshStateCommand.ExecuteAsync(null);
            Check(await vm.RequestCloseAsync()); await read;
            Check(service.Disposals == 1 && !vm.Busy && !vm.CanRefreshState);
        });
        await test("VM entry switch cancellation and reset leave no device write", async () =>
        {
            var service = new FakeService(); await using var vm = Create(service, new Confirmations(false)); await Connect(vm);
            vm.EqName = "保留"; await vm.SelectEqEntryAsync(vm.EqEntries[1]);
            Check(vm.SelectedEqEntry?.Id == 1 && vm.EqDirty);
            vm.ResetEqCommand.Execute(null);
            Check(!vm.EqDirty && vm.EqName == "均衡器一" && service.Actions.Count == 0);
        });
        await test("VM preview and unsupported platform boundary do not call native services", async () =>
        {
            await using var preview = new MainViewModel(HeadsetServiceFactory.CreatePreview(), new Confirmations(true), preview: true);
            await preview.InitializeAsync(); Check(preview.HasVerifiedSnapshot && preview.ProductId == "06EC10");
            await preview.ToggleGameCommand.ExecuteAsync(true); Check(preview.HasVerifiedSnapshot && preview.Status.Contains("不会发送"));
            await using var unsupported = new MainViewModel(new UnsupportedPlatformService(), new Confirmations(true));
            await unsupported.InitializeAsync(); Check(!unsupported.HasDevices && !unsupported.HasVerifiedSnapshot && unsupported.StatusWarning);
        });
        await test("VM invalid custom EQ name never sends an action", async () =>
        {
            var service = new FakeService(); await using var vm = Create(service); await Connect(vm);
            vm.EqName = "\n"; await vm.SaveEqCommand.ExecuteAsync(null);
            Check(service.Actions.Count == 0 && vm.EqDirty && vm.StatusWarning);
        });
        await test("VM dirty disconnect cancellation keeps the verified session", async () =>
        {
            var service = new FakeService(); await using var vm = Create(service, new Confirmations(false)); await Connect(vm);
            vm.Bands[0].Gain = 3; await vm.ConnectCommand.ExecuteAsync(null);
            Check(vm.HasVerifiedSnapshot && vm.EqDirty && service.Disconnects == 0);
        });
        await test("VM close cancels the active mutex confirmation before confirming its draft", async () =>
        {
            var service = new FakeService(); var confirmations = new DeferredConfirmations();
            var vm = new MainViewModel(service, confirmations); await Connect(vm); vm.EqName = "未保存";
            vm.SelectedSpatial = vm.SpatialOptions.Last(); var apply = vm.ApplySpatialCommand.ExecuteAsync(null);
            Check(confirmations.Calls == 1);
            var close = vm.RequestCloseAsync();
            await apply; Check(await close);
            Check(confirmations.Calls == 2 && !confirmations.First.Task.IsCompleted && service.Actions.Count == 0 && service.Disposals == 1);
        });
        await RefreshInteractionTests.RunAsync(test);
        await DeviceManagementTests.RunAsync(test);
    }

    private static MainViewModel Create(FakeService service, Confirmations? confirmations = null) => new(service, confirmations ?? new(true));
    private static async Task Connect(MainViewModel vm) { await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null); Check(vm.HasVerifiedSnapshot); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("VM assertion failed."); }
    private static UiSnapshot Snapshot(string identity = "device-a") => new()
    {
        Identity = identity, ProductId = "06EC10", DeviceName = "OPPO Enco X4", Connected = true, ConnectionState = ConnectionState.Connected, SupportedModel = true,
        LeftBattery = 90, Anc = 0x0104, Equalizer = 0, Spatial = 0, HiRes = true, DualConnection = true, GameMode = false,
        CanSetAnc = true, CanSetEqualizer = true, CanSetCustomEqualizer = true, CanSetSpatial = true,
        CanSetHiRes = true, CanSetDualConnection = true, CanSetGameMode = true, SpatialHiResMutex = true,
        AncOptions = [new(0x0104, "深度降噪"), new(0x0107, "智能降噪")], EqualizerOptions = [new(0, "丹拿原声"), new(7, "丹拿高解析")],
        SpatialOptions = [new(0, "关闭"), new(2, "头部追踪")],
        EqualizerEntries = [new(1, "均衡器一", -6, 6, true, [new(31, 0)]), new(2, "均衡器二", -6, 6, false, [new(31, 2)])]
    };

    private sealed class Confirmations(params bool[] responses) : IConfirmationService
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]);
    }
    private sealed class DeferredConfirmations : IConfirmationService
    {
        public int Calls;
        public TaskCompletionSource<bool> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
            => ++Calls == 1 ? First.Task.WaitAsync(cancellationToken) : Task.FromResult(true);
    }
    private sealed class FakeService : IHeadsetService
    {
        public int Refreshes, Disconnects, Disposals;
        public string LastIdentity = "device-a";
        public List<UiAction> Actions { get; } = [];
        public Func<UiSnapshot>? ConnectSnapshot, RefreshSnapshot;
        public Func<UiAction, UiApplyResult>? Result;
        public TaskCompletionSource<UiSnapshot>? RefreshBlocked;
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UiDevice>>([new("device-a", "OPPO Enco X4", true), new("device-b", "第二副耳机", false)]);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken cancellationToken) { LastIdentity = identity; return Task.FromResult(ConnectSnapshot?.Invoke() ?? Snapshot(identity)); }
        public Task DisconnectAsync(CancellationToken cancellationToken) { Disconnects++; return Task.CompletedTask; }
        public Task<UiSnapshot> RefreshAsync(CancellationToken cancellationToken) { Refreshes++; return RefreshBlocked?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(RefreshSnapshot?.Invoke() ?? Snapshot(LastIdentity)); }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            if (Result is not null) return Task.FromResult(Result(action));
            var after = Snapshot(LastIdentity);
            int? observedId = null;
            switch (action.Kind)
            {
                case "Anc": after.Anc = action.Value; break;
                case "Equalizer": after.Equalizer = action.Value; break;
                case "GameMode": after.GameMode = action.Value == 1; break;
                case "DualConnection": after.DualConnection = action.Value == 1; break;
                case "HiRes": after.HiRes = action.Value == 1; if (after.HiRes == true) after.Spatial = 0; break;
                case "Spatial": after.Spatial = action.Value; if (action.Value != 0) after.HiRes = false; break;
                case "CustomSelect": after.EqualizerEntries = after.EqualizerEntries.Select(existingEntry => existingEntry with { Selected = existingEntry.Id == action.Value }).ToArray(); break;
                case "CustomEqualizer" when action.EqualizerEntry is { } entry:
                    observedId = entry.Id == 0 ? 3 : entry.Id;
                    after.EqualizerEntries = after.EqualizerEntries.Where(existing => existing.Id != observedId)
                        .Append(entry with { Id = observedId.Value, Selected = true }).ToArray(); break;
            }
            return Task.FromResult(new UiApplyResult
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                TargetSubmitted = true, SubmissionAttempted = true, Accepted = true,
                ReadbackMatches = true, ObservedEqualizerId = observedId, After = after
            });
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
