using System.Text.Json;
using HeyMelodyWindows.App;

internal static class FeatureInteractionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("both sources group game controls and place BassWave beside EQ for every model", async () =>
        {
            foreach (string source in new[] { "HeyMelody", "WirelessEarphones" })
            foreach (string product in new[] { "06EC10", "067410", "068C10", "06C010", "future-model" })
            {
                var service = new Service(); service.State.ResourceSource = source; service.State.ProductId = product;
                var original = service.State.FeatureSections.Single().Controls;
                service.State.FeatureSections = [new("audio", "声音", true,
                [new("game", "游戏低延迟", "Switch", 0, CanWrite: true),
                 new("hires", "Hi-Res", "Switch", 0, CanWrite: true),
                 new("bass-enabled", "低音增强", "Switch", 1, CanWrite: true),
                 original.Single(control => control.Id == "spatial"),
                 new("vocal-enhance", "人声增强", "Switch", 0, CanWrite: true),
                 new("bass", "低音强度", "Slider", -4, Minimum: -9, Maximum: 8, CanWrite: true),
                 original.Single(control => control.Id == "prompt-volume"),
                 original.Single(control => control.Id == "game-sound-enabled"),
                 original.Single(control => control.Id == "game-sound"),
                 original.Single(control => control.Id == "gesture:1:0:2")])];
                await using var vm = await Connected(service);
                Check(vm.FeatureSections.Single().Controls.Select(control => control.Id)
                    .SequenceEqual(["game", "game-sound-enabled", "hires", "spatial", "vocal-enhance", "prompt-volume", "gesture:1:0:2"]),
                    "Game effect follows low latency while other visible controls retain their relative order.");
                Check(vm.EqualizerFeatures.Select(control => control.Id).SequenceEqual(["bass-enabled", "bass"])
                    && vm.EqualizerFeatures[0].Label == "BassWave™动态低音"
                    && vm.EqualizerFeatures[0].Description == "动态增益低音，清晰且高保真地呈现深邃低音效果。"
                    && vm.EqualizerFeatures[1] is { Minimum: -9, Maximum: 8, NumberValue: -4 },
                    "The equalizer presentation keeps native BassWave copy and the observed signed slider range.");
                Check(service.Actions.Count == 0 && service.State.FeatureSections.Single().Controls.Any(control => control.Id == "game-sound"),
                    "Presentation neither mutates source definitions nor writes a default game type.");
                var bass = Feature(vm, "bass"); bass.NumberValue = -3; await bass.SubmitAsync();
                Check(service.Actions is [{ FeatureId: "bass", Value: -3 }] && Feature(vm, "bass").NumberValue == -3,
                    "A relocated slider submits its existing native feature identifier once.");
                var enabled = Feature(vm, "bass-enabled"); enabled.BooleanValue = false; await enabled.SubmitAsync();
                Check(service.Actions.Count == 2 && service.Actions[1] is { FeatureId: "bass-enabled", Value: 0 }
                    && Feature(vm, "bass").NumberValue == -3,
                    "Toggling BassWave preserves the separate observed gain.");
                var disconnected = service.Next(); disconnected.ControlReady = false; disconnected.Connected = false;
                disconnected.ConnectionState = ConnectionState.Disconnected; service.Push(disconnected);
                Check(vm.EqualizerFeatures.All(control => !control.CanEdit), "Relocated editors follow the same connection permissions.");
                var draft = Feature(vm, "bass"); draft.NumberValue = 4; await draft.SubmitAsync();
                Check(service.Actions.Count == 2 && draft.NumberValue == -3, "Offline BassWave edits settle without submitting.");
            }
        });
        await test("missing game or BassWave definitions leave no synthetic controls", async () =>
        {
            var service = new Service(); service.State.FeatureSections = [new("audio", "声音", true,
                [new("game-sound-enabled", "游戏音效", "Switch", 0, CanWrite: true),
                 new("hires", "Hi-Res", "Switch", 0, CanWrite: true),
                 new("game-sound", "游戏音效类型", "Combo", 3, Options: [new(3, "射击")], CanWrite: true)])];
            await using var vm = await Connected(service);
            Check(!vm.HasEqualizerFeatures && vm.FeatureSections.Single().Controls.Select(control => control.Id)
                .SequenceEqual(["game-sound-enabled", "hires"]) && service.Actions.Count == 0,
                "Models without low latency or BassWave retain their available controls and never acquire defaults.");
        });
        await test("discrete feature options preserve native identifiers beyond default numeric bounds", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            foreach (var choice in new[] { ("spatial", 2), ("gesture:1:0:2", 29) })
            {
                var control = Feature(vm, choice.Item1);
                Check(control.Maximum == 1, "The fixture must retain the Host's default numeric bounds.");
                control.SelectedOption = control.Options.Single(option => option.Key == choice.Item2);
                await control.SubmitAsync();
                Check(Feature(vm, choice.Item1).SelectedOption?.Key == choice.Item2, "The current observed control must show the submitted native identifier.");
            }
            Check(service.Actions.Select(action => action.Value).SequenceEqual([2, 29]), "Each valid discrete selection must reach the backend once.");
        });
        await test("unlisted discrete choices and out-of-range sliders restore without submission", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            var spatial = Feature(vm, "spatial"); spatial.SelectedOption = new(4, "未报告的类型"); await spatial.SubmitAsync();
            var volume = Feature(vm, "prompt-volume"); volume.NumberValue = 11; await volume.SubmitAsync();
            Check(service.Actions.Count == 0 && spatial.SelectedOption?.Key == 0 && volume.NumberValue == 6,
                "Rejected local drafts must settle without dispatching a backend action.");
        });
        await test("binary feature actions reject nonbinary values even with malformed wide numeric bounds", async () =>
        {
            var service = new Service(); service.State.FeatureSections = service.State.FeatureSections.Select(section => section with
            { Controls = section.Controls.Append(new UiFeatureControl("binary", "二态开关", "Switch", 0, Minimum: -5, Maximum: 5, CanWrite: true)).ToArray() }).ToArray();
            await using var vm = await Connected(service);
            var apply = typeof(MainViewModel).GetMethod("ApplyAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (int value in new[] {-1, 2, 5})
                await (Task)apply.Invoke(vm, [new UiAction("Feature", value, FeatureId: "binary")])!;
            Check(service.Actions.Count == 0, "Switch definitions cannot expand the binary request vocabulary.");
            await (Task)apply.Invoke(vm, [new UiAction("Feature", 1, FeatureId: "binary")])!;
            Check(service.Actions is [{ Value: 1 }] && Feature(vm, "binary").BooleanValue, "A valid binary target still submits through the same action boundary.");
        });
        foreach (string id in new[] { "game-sound-enabled" })
            await test(id + " explicit rejection shows one alert, never retries, and restores the reused control", async () =>
            {
                var service = new Service(); var dialogs = new Dialogs { AlertRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                service.Write = (_, _) => Task.FromResult(service.Rejected(ApplyFailure.PeerRejected));
                await using var vm = await Connected(service, dialogs);
                var section = vm.FeatureSections.Single(); var control = Feature(vm, id);
                var otherDraft = Feature(vm, "gesture:1:0:2"); otherDraft.SelectedOption = otherDraft.Options.Single(option => option.Key == 29);
                vm.SelectedAnc = vm.AncOptions.Last(); vm.EqName = "保留未保存调整"; vm.Bands[0].Gain = 4;
                Edit(control, 1);
                var submit = control.SubmitAsync(); await dialogs.AlertEntered.Task.WaitAsync(Deadline);
                Check(service.Actions.Count == 1 && dialogs.Alerts == 1 && dialogs.Retries == 0 && service.Reads == 0,
                    "A rejected game sound action must stop at its first receipt and alert.");
                dialogs.AlertRelease.SetResult(); await submit.WaitAsync(Deadline);
                Check(ReferenceEquals(section, vm.FeatureSections.Single()) && ReferenceEquals(control, Feature(vm, id)), "An unchanged observation must retain the existing controls.");
                Check(control.Kind == "Switch" ? !control.BooleanValue : control.SelectedOption?.Key == 0, "The reused editor must return to the observed original value.");
                Check(otherDraft.SelectedOption?.Key == 29 && vm.SelectedAnc?.Key == 0x0107 && vm.EqDirty && vm.Bands[0].Gain == 4,
                    "Settling one feature must preserve every unrelated draft.");
                Check(service.Actions.Count == 1 && service.Reads == 1 && dialogs.Alerts == 1, "Dismissing the alert permits a GET, without another setter.");
            });
        foreach (string id in new[] { "game-sound-enabled" })
            await test(id + " unknown acknowledgement reconciles by GET and preserves its original evidence", async () =>
            {
                var service = new Service(); var dialogs = new Dialogs(); UiApplyResult? receipt = null;
                service.Write = (action, _) =>
                {
                    service.Observe(action);
                    receipt = service.Rejected(ApplyFailure.TransportUnknown); receipt.After = null;
                    receipt.AcknowledgementUnknown = true; receipt.ReadbackMatches = null;
                    return Task.FromResult(receipt);
                };
                await using var vm = await Connected(service, dialogs); var control = Feature(vm, id);
                Edit(control, 1); await control.SubmitAsync();
                Check(service.Actions.Count == 1 && service.Reads == 1 && dialogs.Alerts == 0 && dialogs.Retries == 0,
                    "Only a fresh GET may reconcile the uncertain acknowledgement.");
                Check(ReferenceEquals(vm.LastApplyAttempts.Single(), receipt) && receipt is { Accepted: false, AckStatus: null, AcknowledgementUnknown: true, ReadbackMatches: null },
                    "A matching later observation must not rewrite the original acknowledgement or verification flags.");
                Check(vm.StatusWarning && vm.Status.Contains("响应未确认"), "The observed target must retain its acknowledgement warning.");
                Check(Feature(vm, id).Kind == "Switch" ? Feature(vm, id).BooleanValue : Feature(vm, id).SelectedOption?.Key == 3,
                    "The newer observed model must retain the actual successful state.");
            });
        await test("an uncertain game sound mismatch ends with one alert and no replay", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs();
            service.Write = (_, _) => Task.FromResult(service.Rejected(ApplyFailure.TransportUnknown));
            await using var vm = await Connected(service, dialogs); var control = Feature(vm, "game-sound-enabled");
            control.BooleanValue = true; await control.SubmitAsync();
            Check(service.Actions.Count == 1 && service.Reads == 2 && dialogs.Alerts == 1 && dialogs.Retries == 0 && !Feature(vm, control.Id).BooleanValue,
                "Uncertain mismatch must reconcile, alert once, and refresh without repeating SET.");
        });
        await test("an unknown game sound acknowledgement remains a warning even alongside an accepted flag", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs();
            service.Write = (action, _) =>
            {
                var result = service.Success(action); result.AcknowledgementUnknown = true; return Task.FromResult(result);
            };
            await using var vm = await Connected(service, dialogs); var control = Feature(vm, "game-sound-enabled");
            control.BooleanValue = true; await control.SubmitAsync();
            Check(vm.StatusWarning && vm.Status.Contains("响应未确认") && vm.LastApplyAttempts.Single() is { Accepted: true, AcknowledgementUnknown: true }
                && service.Actions.Count == 1 && dialogs.Alerts == 0 && dialogs.Retries == 0, "A matching observation must preserve explicit acknowledgement uncertainty.");
        });
        await test("game sound stale-generation completion cannot change the new visible model", async () =>
        {
            var service = new Service(); var pending = new TaskCompletionSource<UiApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Write = (_, token) => pending.Task.WaitAsync(token);
            await using var vm = await Connected(service); var old = Feature(vm, "game-sound-enabled");
            old.BooleanValue = true; var submit = old.SubmitAsync();
            var next = service.Next(); next.DeviceGeneration++; next.Revision = 1; service.Push(next);
            var current = Feature(vm, old.Id); current.BooleanValue = true;
            pending.SetResult(service.Rejected(ApplyFailure.PeerRejected)); await submit.WaitAsync(Deadline);
            Check(!ReferenceEquals(old, current) && ReferenceEquals(current, Feature(vm, old.Id)) && current.BooleanValue,
                "An old model's finally must not overwrite a new session's local edit.");
            Check(service.Actions.Count == 1 && service.Reads == 0, "A retired intent must not refresh or retry against the new generation.");
        });
        await test("a queued game sound intent is dropped after control generation changes", async () =>
        {
            var service = new Service(); var pending = new TaskCompletionSource<UiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Read = (_, token) => pending.Task.WaitAsync(token);
            await using var vm = await Connected(service); var original = service.State;
            var refresh = vm.RefreshStateCommand.ExecuteAsync(null); var old = Feature(vm, "game-sound-enabled");
            old.BooleanValue = true; var submit = old.SubmitAsync();
            var next = service.Next(); next.DeviceGeneration++; next.Revision = 1; service.Push(next);
            pending.SetResult(original); await Task.WhenAll(refresh, submit).WaitAsync(Deadline);
            Check(service.Actions.Count == 0 && !Feature(vm, old.Id).BooleanValue && !old.BooleanValue,
                "A queued action must settle its old editor without sending to a newer session.");
        });
        await test("feature no-op, exception, cancellation, and newer local edit settle without reentrant submissions", async () =>
        {
            int calls = 0;
            var disabled = new FeatureControlViewModel(new("disabled", "已停用", "Switch", 0), _ => { calls++; return Task.CompletedTask; });
            disabled.BooleanValue = true; await disabled.SubmitAsync(); Check(!disabled.BooleanValue && calls == 0, "A disabled submit must restore the observed value.");
            foreach (bool cancellation in new[] { false, true })
            {
                var control = new FeatureControlViewModel(new("test", "测试", "Switch", 0, CanWrite: true), _ =>
                {
                    calls++; return cancellation ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new IOException("test"));
                });
                control.BooleanValue = true;
                try { await control.SubmitAsync(); throw new Exception("Expected the submission to fail."); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
                Check(!control.BooleanValue, "An interrupted submission must restore its observed value.");
            }
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new UiOption[] { new(0, "关闭"), new(17, "游戏模式"), new(29, "接听") };
            var draft = new FeatureControlViewModel(new("test", "测试", "Combo", 0, Options: options, CanWrite: true), _ => { calls++; return pending.Task; });
            draft.SelectedOption = options[1]; var submit = draft.SubmitAsync(); draft.SelectedOption = options[2]; pending.SetResult(); await submit;
            Check(draft.SelectedOption?.Key == 29 && calls == 3, "Completion must preserve a newer edit and never submit as a consequence of restoring bindings.");
        });
        await test("successful demo feature values survive settlement", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service); await vm.AddDemoDeviceAsync();
            var control = Feature(vm, "game-sound-enabled"); control.BooleanValue = true; await control.SubmitAsync();
            Check(Feature(vm, control.Id).BooleanValue && service.Actions.Count == 0, "The visible successful demo state must remain local and enabled.");
        });
        foreach (string kind in new[] { "Spatial", "HiRes" })
            await test(kind + " safe retry needs only its own observed state", async () =>
            {
                var service = new Service(); var dialogs = new Dialogs();
                if (kind == "Spatial") service.State.HiRes = null; else service.State.Spatial = null;
                service.Write = (action, _) => Task.FromResult(service.Actions.Count == 1 ? service.Rejected(ApplyFailure.PeerRejected) : service.Success(action));
                await using var vm = await Connected(service, dialogs);
                if (kind == "Spatial") { vm.SelectedSpatial = vm.SpatialOptions.Last(); await vm.ApplySpatialCommand.ExecuteAsync(null); }
                else await vm.ToggleHiResCommand.ExecuteAsync(true);
                Check(service.Actions.Count == 2 && service.Actions.All(action => !action.AllowMutexChange) && dialogs.Confirmations == 0 && dialogs.Retries == 0,
                    "Legacy mutex metadata and an unknown other mode must not introduce a prerequisite or confirmation.");
            });
    }

    private static void Edit(FeatureControlViewModel control, int value)
    { if (control.Kind == "Switch") control.BooleanValue = value == 1; else control.SelectedOption = control.Options.Single(option => option.Key == value); }
    private static FeatureControlViewModel Feature(MainViewModel vm, string id) => vm.FeatureSections.SelectMany(section => section.Controls)
        .Concat(vm.EqualizerFeatures).Single(control => control.Id == id);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static async Task<MainViewModel> Connected(Service service, Dialogs? dialogs = null)
    {
        var vm = new MainViewModel(service, dialogs ?? new Dialogs()); service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync(); Check(vm.HasVerifiedSnapshot, "The fixture must establish a current backend session."); return vm;
    }
    private sealed class Dialogs : IConfirmationService
    {
        public int Confirmations, Retries, Alerts;
        public TaskCompletionSource AlertEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? AlertRelease;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) { Confirmations++; return Task.FromResult(true); }
        public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) { Retries++; return Task.FromResult(true); }
        public Task AlertAsync(string title, string message, CancellationToken token)
        { Alerts++; AlertEntered.TrySetResult(); return AlertRelease?.Task.WaitAsync(token) ?? Task.CompletedTask; }
    }
    private sealed class Service : IHeadsetEventService
    {
        public event Action<BackendEvent>? EventReceived;
        public UiSnapshot State = new()
        {
            Identity = "peer", DeviceName = "OPPO Enco X4", BackendInstanceId = "features", DeviceGeneration = 1, Revision = 1,
            ProductId = "06EC10", Connected = true, ConnectionState = ConnectionState.Connected, ControlReady = true,
            SupportedModel = true, MonitoringState = MonitoringState.Monitoring, ObservationFresh = true,
            Anc = 0x0104, Spatial = 0, HiRes = false, SpatialHiResMutex = true,
            CanSetAnc = true, CanSetSpatial = true, CanSetHiRes = true, CanSetCustomEqualizer = true,
            AncOptions = [new(0x0104, "降噪"), new(0x0107, "智能")], SpatialOptions = [new(0, "关闭"), new(2, "头部追踪")],
            EqualizerFrequencies = [31], EqualizerEntries = [new(1, "均衡器一", -6, 6, true, [new(31, 0)])],
            FeatureSections = [new("audio", "声音", true,
            [new("game-sound-enabled", "游戏音效", "Switch", 0, CanWrite: true),
             new("game-sound", "游戏音效类型", "Combo", 0, Options: [new(3, "射击游戏"), new(0, "关闭")], CanWrite: true),
             new("spatial", "空间音效", "Radio", 0, Options: [new(0, "关闭"), new(2, "头部追踪")], CanWrite: true),
             new("gesture:1:0:2", "左耳双击", "Combo", 17, Options: [new(17, "游戏模式"), new(29, "接听")], CanWrite: true),
             new("prompt-volume", "提示音音量", "Slider", 6, Minimum: 1, Maximum: 10, CanWrite: true)])]
        };
        public int Reads;
        public List<UiAction> Actions { get; } = [];
        public Func<UiAction, CancellationToken, Task<UiApplyResult>>? Write;
        public Func<RefreshScope, CancellationToken, Task<UiSnapshot>>? Read;
        public UiSnapshot Next() { var next = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(State))!; next.Revision++; return next; }
        public void Push(UiSnapshot next) { State = next; EventReceived?.Invoke(new() { Kind = "snapshot", BackendInstanceId = next.BackendInstanceId, Snapshot = next }); }
        public void Observe(UiAction action)
        {
            State = Next();
            if (action.Kind == "Feature")
            {
                SetFeature(action.FeatureId!, action.Value);
                if (action.FeatureId == "game-sound") SetFeature("game-sound-enabled", action.Value == 0 ? 0 : 1);
                if (action.FeatureId == "spatial") State.Spatial = action.Value;
            }
            else if (action.Kind == "Spatial") State.Spatial = action.Value;
            else if (action.Kind == "HiRes") State.HiRes = action.Value == 1;
        }
        private void SetFeature(string id, int value) => State.FeatureSections = State.FeatureSections.Select(section => section with
        { Controls = section.Controls.Select(control => control.Id == id ? control with { Value = value } : control).ToArray() }).ToArray();
        public UiApplyResult Success(UiAction action)
        {
            Observe(action); return new() { BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                SubmissionAttempted = true, TargetSubmitted = true, Accepted = true, AckStatus = 0, ReadbackMatches = true, After = State };
        }
        public UiApplyResult Rejected(ApplyFailure failure)
        {
            State = Next(); return new() { BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected, FailureKind = failure,
                SubmissionAttempted = true, TargetSubmitted = true, Accepted = false, AckStatus = failure == ApplyFailure.PeerRejected ? (byte)1 : null,
                ReadbackMatches = false, After = State };
        }
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token) => Task.FromResult(new BackendSubscription
        { BackendInstanceId = State.BackendInstanceId, Devices = [new("peer", "OPPO Enco X4", true)], Snapshots = [State] });
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>([new("peer", "OPPO Enco X4", true)]);
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => Task.FromResult(State);
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token)
        { Reads++; if (Read is not null) return Read(scope, token); State = Next(); return Task.FromResult(State); }
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token)
        { Actions.Add(action); return Write?.Invoke(action, token) ?? Task.FromResult(Success(action)); }
        public Task AcknowledgeExitAsync(string id, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
