using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class InteractionTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        Run("background reads preserve editing and queued mouse actions", BackgroundInteractionAsync);
        Run("backend pushes update bound controls without setters and BLE observations keep settings disabled", EventInteractionAsync);
        Run("settings waits for the shared dialog owner and cancels cleanly", SettingsSerializationAsync);
        Run("resource settings keep keys separate and release the dialog before EQ confirmation", ResourceSettingsAsync);
        Run("retry and alert dialogs use the required buttons", DialogButtonsAsync);
        Run("resource section expansion survives replacement definitions", ExpansionPersistenceAsync);
        return 6;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task EventInteractionAsync()
    {
        var service = new PushService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); view.ExpandAllSettings(); Dispatcher.UIThread.RunJobs();
            var originalAnc = FeatureTestData.Anc(view);
            var originalEq = view.GetVisualDescendants().OfType<EqualizerChoiceView>().Single().GetVisualDescendants().OfType<RadioButton>().First();
            FeatureTestData.Anc(view).SelectedItem = vm.SelectedAncMode!.Options.Last();
            view.FindControl<TextBox>("EqNameEditor")!.Text = "广播期间保留";
            Dispatcher.UIThread.RunJobs();
            var update = service.Next(); update.GameMode = true; update.LeftBattery = 54;
            service.Push(FeatureTestData.Decorate(update)); Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(originalAnc, FeatureTestData.Anc(view))
                && view.GetVisualDescendants().Contains(originalEq), "Unrelated battery and feature pushes keep the existing ANC dropdown and EQ radio controls.");
            Check(FeatureTestData.Switch(view, "game").IsChecked == true && vm.LeftBatteryText == "54%" && service.Actions.Count == 0,
                "A pushed phone setting updates the generic switch without a setter.");
            Check(vm.EqDirty && vm.EqName == "广播期间保留", "State pushes preserve the EQ draft.");
            var ble = service.Next(); ble.ControlReady = false; ble.Connected = false;
            ble.ConnectionState = ConnectionState.Disconnected; ble.ObservationFresh = true;
            ble.MonitoringState = MonitoringState.WaitingForConnection; ble.LeftBattery = 43;
            service.Push(ble); Dispatcher.UIThread.RunJobs();
            Check(vm.LeftBatteryText == "43%" && !FeatureTestData.Switch(view, "game").IsEnabled
                && !FeatureTestData.Anc(view).IsEnabled && !view.FindControl<ItemsControl>("BandEditor")!.IsEnabled && service.Actions.Count == 0,
                "BLE battery remains visible while settings require verified control.");
        }
        finally { window.Close(); }
    }

    private static async Task BackgroundInteractionAsync()
    {
        var service = new BlockingService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); view.ExpandAllSettings(); Dispatcher.UIThread.RunJobs();
            service.BlockNextRead = true;
            var read = vm.PollAsync();
            await service.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); Dispatcher.UIThread.RunJobs();
            Check(vm.IsRefreshing && !vm.Busy, "Background read has independent activity state.");
            Check(FeatureTestData.Anc(view).IsEnabled && view.FindControl<ItemsControl>("BandEditor")!.IsEnabled
                && FeatureTestData.Switch(view, "game").IsEnabled && view.FindControl<Button>("AppSettingsButton")!.IsEnabled,
                "Value editors remain available during a background read.");
            var settings = view.OpenSettingsDialogAsync();
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            Check(!settings.IsCompleted && window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => Equals(dialog.Title, "应用设置")),
                "Settings opens during the blocked read.");
            await view.DismissSettingsDialogAsync(); await settings; view.ResumeSettingsDialogs();
            view.FindControl<TextBox>("EqNameEditor")!.Text = "读取期间的草稿";
            view.FindControl<ItemsControl>("BandEditor")!.GetVisualDescendants().OfType<Slider>().First().Value = 4;
            Dispatcher.UIThread.RunJobs();
            var action = vm.SelectAncModeAsync(vm.AncModes.Single(mode => mode.Key == 0));
            Check(service.Actions.Count == 0 && !action.IsCompleted, "An immediate option change queues behind the current wire read.");
            service.ReleaseRead.TrySetResult();
            await action.WaitAsync(TimeSpan.FromSeconds(3)); await read.WaitAsync(TimeSpan.FromSeconds(3)); Dispatcher.UIThread.RunJobs();
            Check(service.Actions.Count == 1 && service.Actions[0] is { Kind: "Anc", Value: 0x0003 }, "Queued option sends its captured key once.");
            Check(vm.EqDirty && vm.EqName == "读取期间的草稿" && vm.Bands[0].Gain == 4, "EQ edits survive readback.");
        }
        finally { service.ReleaseRead.TrySetResult(); window.Close(); }
    }
    private static async Task SettingsSerializationAsync()
    {
        await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? settings = null;
        Task? owner = null;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            owner = vm.Dialogs.RunAsync(token => release.Task.WaitAsync(token));
            settings = view.OpenSettingsDialogAsync();
            Dispatcher.UIThread.RunJobs();
            Check(!settings.IsCompleted && !window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(dialog => Equals(dialog.Title, "应用设置")), "Settings waits behind the current dialog owner.");
            await view.DismissSettingsDialogAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(settings.IsCompleted, "Closing cancels settings waiting for the dialog owner.");
            release.TrySetResult(); await owner;
            view.ResumeSettingsDialogs();
            settings = view.OpenSettingsDialogAsync();
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            Check(!settings.IsCompleted && window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(dialog => Equals(dialog.Title, "应用设置")), "Settings can open after cancelling the queued interaction.");
            await view.DismissSettingsDialogAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await settings;
        }
        finally
        {
            release.TrySetResult();
            await view.DismissSettingsDialogAsync();
            if (owner is not null) await owner;
            window.Close();
        }
    }

    private static Task DialogButtonsAsync()
    {
        var retry = WindowConfirmationService.CreateConfirmation("重试设置", "再次发送？", "确认");
        Check(retry.PrimaryButtonText == "确认" && retry.CloseButtonText == "取消"
            && retry.DefaultButton == FAContentDialogButton.Close, "Retry confirmation defaults to cancel.");
        var alert = WindowConfirmationService.CreateAlert("设置未完成", "请核对设备状态。");
        Check(string.IsNullOrEmpty(alert.PrimaryButtonText) && string.IsNullOrEmpty(alert.SecondaryButtonText)
            && alert.CloseButtonText == "确定" && alert.DefaultButton == FAContentDialogButton.Close,
            "Failure alert has only its acknowledgement button.");
        return Task.CompletedTask;
    }
    private static async Task ResourceSettingsAsync()
    {
        var service = new ResourceSettingsService(); var confirmations = new AcceptConfirmation();
        await using var vm = new MainViewModel(service, confirmations);
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm }; var window = CreateWindow(view);
        var preferencesPath = Path.GetFullPath(Path.Combine("artifacts", "settings-test-" + Guid.NewGuid().ToString("N"), "preferences.json"));
        ThemePreferences.SettingsPathOverride = preferencesPath;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            var source = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "资源来源");
            var key = dialog.GetVisualDescendants().OfType<TextBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "云端资源密钥");
            key.Text = "edited-a"; source.SelectedItem = vm.ResourceSourceOptions[1]; Dispatcher.UIThread.RunJobs();
            Check(key.Text == "key-b", "The second source displays its own stored key.");
            key.Text = "edited-b"; source.SelectedItem = vm.ResourceSourceOptions[0]; Dispatcher.UIThread.RunJobs();
            Check(key.Text == "edited-a" && vm.CloudKey == "key-a" && vm.SelectedResourceSource.Key == 0,
                "Switching the dialog source preserves local edits without changing view-model drafts.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Saves == 0 && vm.CloudKey == "key-a", "Cancelling never saves or changes the original key.");

            vm.EqName = "待确认的调整";
            Check(vm.EqDirty, "An unsaved EQ adjustment requires a discard confirmation.");
            operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            source = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "资源来源");
            key = dialog.GetVisualDescendants().OfType<TextBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "云端资源密钥");
            source.SelectedItem = vm.ResourceSourceOptions[1]; key.Text = "saved-b";
            Dispatcher.UIThread.RunJobs();
            var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "保存"));
            Click(window, save); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(confirmations.Count == 1 && service.Saves == 1 && service.Settings.Source == "WirelessEarphones"
                && service.Settings.CloudKey == "saved-b" && service.Settings.HeyMelodyKey == "key-a"
                && service.Settings.WirelessEarphonesKey == "saved-b",
                "Saving can acquire the shared EQ confirmation dialog and updates only the selected source key.");
        }
        finally { ThemePreferences.SettingsPathOverride = null; await view.DismissSettingsDialogAsync(); window.Close(); }
    }

    private sealed class AcceptConfirmation : IConfirmationService
    {
        public int Count { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        { Count++; return Task.FromResult(true); }
    }
    private sealed class ResourceSettingsService : IHeadsetService, IResourceSettingsService
    {
        private readonly PreviewService _inner = new();
        public int Saves { get; private set; }
        public UiResourceSettings Settings { get; private set; } = new(CloudKey: "key-a")
            { HeyMelodyKey = "key-a", WirelessEarphonesKey = "key-b" };
        public Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token) => Task.FromResult(Settings);
        public Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token)
        { Settings = settings; Saves++; return Task.FromResult(settings); }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => _inner.RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => _inner.ApplyAsync(action, token);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
    private static async Task ExpansionPersistenceAsync()
    {
        var service = new PushService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent; await vm.InitializeAsync();
        var view = new MainView { DataContext = vm }; var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var section = view.GetVisualDescendants().OfType<FASettingsExpander>().Single(expander => Equals(expander.Header, "连接与体验"));
            Check(section.IsExpanded, "A resource's default-expanded hint is used on first display.");
            section.IsExpanded = false;
            var snapshot = service.Next(); snapshot.GameMode = true;
            service.Push(FeatureTestData.Decorate(snapshot)); Dispatcher.UIThread.RunJobs();
            section = view.GetVisualDescendants().OfType<FASettingsExpander>().Single(expander => Equals(expander.Header, "连接与体验"));
            Check(!section.IsExpanded, "A new definition with the same section ID retains the user's collapse choice.");
        }
        finally { window.Close(); }
    }

    private static Window CreateWindow(MainView view) => new()
        { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BlockingService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        private UiSnapshot? _snapshot;
        public bool BlockNextRead { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<UiAction> Actions { get; } = [];
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _snapshot = FeatureTestData.Decorate(await _inner.ConnectAsync(identity, token));
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => RefreshAsync(RefreshScope.Full, token);
        public async Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token)
        {
            if (BlockNextRead)
            {
                BlockNextRead = false; ReadStarted.TrySetResult();
                await ReleaseRead.Task.WaitAsync(token);
            }
            return _snapshot!;
        }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Actions.Add(action);
            if (action.Kind == "Anc") _snapshot!.Anc = action.Value;
            return Task.FromResult(new UiApplyResult
            {
                BackendReceiptReceived = true, Accepted = true, SubmissionAttempted = true,
                TargetSubmitted = true, ConnectionState = ConnectionState.Connected,
                ReadbackMatches = true, After = _snapshot
            });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PushService : IHeadsetEventService
    {
        private readonly PreviewService _inner = new();
        private UiSnapshot? _snapshot;
        public event Action<BackendEvent>? EventReceived;
        public List<UiAction> Actions { get; } = [];
        public UiSnapshot Next()
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<UiSnapshot>(
                System.Text.Json.JsonSerializer.Serialize(_snapshot))!;
            snapshot.Revision++; return snapshot;
        }
        public void Push(UiSnapshot snapshot)
        {
            _snapshot = snapshot;
            EventReceived?.Invoke(new() { Kind = "snapshot", BackendInstanceId = snapshot.BackendInstanceId, Snapshot = snapshot });
        }
        public async Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        {
            var devices = await _inner.EnumerateAsync(token);
            _snapshot = FeatureTestData.Decorate(await _inner.ConnectAsync(devices[0].Identity, token));
            _snapshot.BackendInstanceId = "ui-test-backend"; _snapshot.DeviceGeneration = 1; _snapshot.Revision = 1;
            _snapshot.ControlReady = true; _snapshot.MonitoringState = MonitoringState.Monitoring;
            return new() { BackendInstanceId = _snapshot.BackendInstanceId, Devices = devices, Snapshots = [_snapshot] };
        }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => Task.FromResult(_snapshot!);
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshot!);
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token)
        { Actions.Add(action); throw new InvalidOperationException("A state push must never send a setter."); }
        public Task AcknowledgeExitAsync(string requestId, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task DisconnectAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
