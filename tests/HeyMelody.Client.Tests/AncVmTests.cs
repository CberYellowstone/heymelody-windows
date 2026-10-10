using HeyMelodyWindows.App;

internal static class AncVmTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("ANC main buttons send parent actions without a remembered or default child", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            foreach (int primary in new[] { 11, 1, 2, 0, 11 })
            {
                await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == primary));
                Check(vm.SelectedAncMode?.Key == primary && vm.SelectedAncSubmode is null && vm.SelectedAnc is null);
                Check(vm.CanSetAncMode && vm.CanSetAnc);
            }
            Check(service.Actions.Select(x => (x.Kind, x.Value)).SequenceEqual(
                new[] { ("AncMode", 11), ("AncMode", 1), ("AncMode", 2), ("AncMode", 0), ("AncMode", 11) }));
            await vm.SelectAncModeAsync(vm.SelectedAncMode!);
            Check(service.Actions.Count == 5);
        });
        await test("ANC parent-only state allows explicit adaptive child choices without a fallback write", async () =>
        {
            var service = new Service { Primary = 11, Level = null }; await using var vm = await Connected(service);
            Check(vm.SelectedAncMode?.Key == 11 && vm.SelectedAncSubmode is null && vm.HasAncSubmodes);
            await vm.RefreshStateCommand.ExecuteAsync(null); Check(service.Actions.Count == 0);
            foreach (int level in new[] { 12, 13, 14 })
            {
                await vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Single(x => (x.Key & 255) == level));
                Check(vm.SelectedAncSubmode?.Key == ((11 << 8) | level) && vm.CanSetAncMode && vm.CanSetAnc);
            }
            Check(service.Actions.Select(x => (x.Kind, x.Value)).SequenceEqual(
                new[] { ("Anc", 0x0B0C), ("Anc", 0x0B0D), ("Anc", 0x0B0E) }));
        });
        await test("ANC main receipt accepts a firmware-selected child and never restores the old child", async () =>
        {
            var service = new Service { MainReadbackLevel = 14 }; await using var vm = await Connected(service);
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(service.Actions is [{ Kind: "AncMode", Value: 11 }] && vm.SelectedAncSubmode?.Key == 0x0B0E);
            Check(vm.LastApplyAttempts.Count == 1 && vm.CanSetAncMode);
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 0));
            service.MainReadbackLevel = 12;
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(service.Actions.Count == 3 && vm.SelectedAncSubmode?.Key == 0x0B0C);
        });
        await test("ANC keeps both editors available and submits only the last complete target after success", async () =>
        {
            foreach (bool main in new[] { true, false })
            {
                var service = new Service { Primary = 11, Level = 13 };
                await using var vm = await Connected(service);
                service.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var action = main ? vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 1))
                    : vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Last());
                await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!vm.CanSetAncMode && !vm.CanSetAnc && vm.CanChooseAncMode && vm.CanChooseAnc);
                var off = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 0));
                var adaptive = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
                var intermediate = vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Last());
                var final = vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.First());
                Check(service.Actions.Count == 1);
                Check(vm.SelectedAncMode?.Key == 11 && vm.SelectedAncSubmode?.Key == 0x0B0C);
                var staleSelection = false;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.SelectedAncMode) && vm.SelectedAncMode?.Key != 11)
                        staleSelection = true;
                };
                service.Release.SetResult();
                await Task.WhenAll(action, off, adaptive, intermediate, final).WaitAsync(TimeSpan.FromSeconds(3));
                Check(vm.CanSetAncMode && vm.CanSetAnc && service.Actions.Count == 2 && !staleSelection);
                Check(service.Actions.Last() is { Kind: "Anc", Value: 0x0B0C } && service.MaxConcurrent == 1);
            }
        });
        await test("ANC skips a queued target already confirmed by the first readback", async () =>
        {
            var service = new Service { Primary = 11, Level = 13, Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var vm = await Connected(service);
            var first = vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Last());
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var off = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 0));
            var adaptive = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            var same = vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Last());
            service.Release.SetResult();
            await Task.WhenAll(first, off, adaptive, same).WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Actions is [{ Kind: "Anc", Value: 0x0B0E }] && vm.SelectedAncSubmode?.Key == 0x0B0E);
        });
        await test("ANC uncertain or failed submissions discard later choices without retries", async () =>
        {
            foreach (var failure in new[] { "no-ack", "no-receipt", "timeout", "cancelled", "disconnected", "matching-no-ack" })
            {
                var service = new Service { Failure = failure, Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                var dialogs = new Dialogs(); await using var vm = await Connected(service, dialogs);
                var first = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
                await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var later = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 1));
                service.Release.SetResult();
                await Task.WhenAll(first, later).WaitAsync(TimeSpan.FromSeconds(3));
                Check(service.Actions.Count == 1 && dialogs.Retries == 0 && vm.CanSetAncMode);
                Check(vm.SelectedAncMode?.Key == (failure == "matching-no-ack" ? 11 : 0));
                service.Failure = null;
                await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 1));
                Check(service.Actions.Count == 2 && vm.SelectedAncMode?.Key == 1);
            }
        });
        await test("ANC legacy snapshots keep explicit child control but cannot silently emulate a main action", async () =>
        {
            var service = new Service { MainSupported = false, Primary = 11, Level = 13 };
            await using var vm = await Connected(service);
            Check(!vm.CanChooseAncMode && !vm.CanSetAncMode && vm.CanSetAnc && vm.SelectedAncMode?.Key == 11);
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 0));
            Check(service.Actions.Count == 0);
            await vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Last());
            Check(service.Actions is [{ Kind: "Anc", Value: 0x0B0E }]);
        });
        await test("ANC main unconfirmed mismatch restores observation and waits for another explicit click", async () =>
        {
            var service = new Service { FailMain = true }; var dialogs = new Dialogs();
            await using var vm = await Connected(service, dialogs);
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(service.Actions is [{ Kind: "AncMode", Value: 11 }] && dialogs.Retries == 0);
            Check(vm.SelectedAncMode?.Key == 0 && vm.CanSetAncMode);
        });
        await test("ANC refresh presents a parent-only phone state without submitting a default child", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            service.Primary = 11; service.Level = null;
            await vm.RefreshStateCommand.ExecuteAsync(null);
            Check(vm.SelectedAncMode?.Key == 11 && vm.SelectedAncSubmode is null && vm.SelectedAnc is null);
            Check(service.Actions.Count == 0);
        });
        await test("ANC main disconnection restores the observed radio and allows a later explicit retry", async () =>
        {
            var service = new Service { DisconnectMain = true }; await using var vm = await Connected(service);
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(service.Actions is [{ Kind: "AncMode", Value: 11 }] && vm.SelectedAncMode?.Key == 0 && vm.CanSetAncMode);
            service.DisconnectMain = false;
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(service.Actions.Count == 2 && vm.SelectedAncMode?.Key == 11);
        });
    }

    private static async Task<MainViewModel> Connected(Service service, Dialogs? dialogs = null)
    {
        var vm = new MainViewModel(service, dialogs ?? new());
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        Check(vm.HasVerifiedSnapshot); return vm;
    }
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("ANC VM regression failed."); }

    private sealed class Dialogs : IConfirmationService
    {
        public int Retries;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(true);
        public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token)
        { Retries++; return Task.FromResult(false); }
    }
    private sealed class Service : IHeadsetService
    {
        public int Primary;
        public int? Level = 3, MainReadbackLevel;
        public bool MainSupported = true, FailMain, DisconnectMain;
        public string? Failure;
        public int MaxConcurrent;
        private int _concurrent;
        public List<UiAction> Actions = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        private UiSnapshot Snapshot() => new()
        {
            Identity = "x4", DeviceName = "Enco X4", ProductId = "06EC10", Connected = true,
            ConnectionState = ConnectionState.Connected, SupportedModel = true,
            AncPrimary = MainSupported ? Primary : null, Anc = Level.HasValue ? (Primary << 8) | Level.Value : null,
            CanSetAncMode = MainSupported, CanSetAnc = true,
            AncModes = [new(0, "关闭", "off", [new(3, "关闭")]),
                new(1, "降噪", "anc", [new(0x0104, "深度"), new(0x0107, "智能")]),
                new(11, "自适应降噪", "adaptive", [new(0x0B0C, "更少噪音"), new(0x0B0D, "标准"), new(0x0B0E, "更多噪音")]),
                new(2, "通透", "transparency", [new(0x0208, "通透")])],
            AncOptions = [new(3, "关闭"), new(0x0104, "深度"), new(0x0107, "智能"),
                new(0x0B0C, "更少噪音"), new(0x0B0D, "标准"), new(0x0B0E, "更多噪音"), new(0x0208, "通透")]
        };
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<UiDevice>>([new("x4", "Enco X4", true)]);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => Task.FromResult(Snapshot());
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => Task.FromResult(Snapshot());
        public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            _concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, _concurrent);
            try
            {
            Actions.Add(action); Entered.TrySetResult();
            if (Release is { } release) await release.Task.WaitAsync(token);
            if (Failure == "timeout") throw new TimeoutException("No receipt");
            if (Failure == "cancelled") throw new OperationCanceledException();
            if (DisconnectMain || Failure == "disconnected") return new()
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Disconnected,
                SubmissionAttempted = true, TargetSubmitted = true,
                FailureKind = ApplyFailure.Disconnected, After = null
            };
            if (Failure == "matching-no-ack") { Primary = action.Value; Level = null; }
            if (FailMain || Failure is "no-ack" or "no-receipt" or "matching-no-ack") return new()
            {
                BackendReceiptReceived = Failure != "no-receipt", ConnectionState = ConnectionState.Connected,
                SubmissionAttempted = true, TargetSubmitted = true, AcknowledgementUnknown = true,
                FailureKind = ApplyFailure.TransportUnknown, ReadbackMatches = Failure == "matching-no-ack", After = Snapshot()
            };
            if (action.Kind == "AncMode") { Primary = action.Value; Level = Primary == 11 ? MainReadbackLevel : null; }
            else { Primary = action.Value >> 8; Level = action.Value & 255; }
            return new()
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                SubmissionAttempted = true, TargetSubmitted = true, Accepted = true,
                ReadbackMatches = true, After = Snapshot()
            };
            }
            finally { _concurrent--; }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
