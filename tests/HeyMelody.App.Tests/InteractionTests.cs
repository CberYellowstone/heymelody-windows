using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
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
        Run("resource settings keep edits local and preserve the EQ draft", ResourceSettingsAsync);
        Run("retry and alert dialogs use the required buttons", DialogButtonsAsync);
        Run("ANC and resource expansion choices survive refreshes and presentation switches", ExpansionPersistenceAsync);
        Run("ANC radio sends a parent and explicit dropdown sends only a child", AncModeInteractionAsync);
        Run("ANC parent-only pushes select the radio without selecting or writing a child", AncParentPushAsync);
        Run("header shows notification guidance and clears after a healthy push", WarningNoticeAsync);
        return 9;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task WarningNoticeAsync()
    {
        var service = new PushService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
            var warning = service.Next(); warning.Warnings = ["部分状态通知不可用，仍可手动刷新状态。"];
            service.Push(warning); Dispatcher.UIThread.RunJobs();
            var notice = view.FindControl<TextBlock>("HeaderWarnings")!;
            var separator = view.FindControl<TextBlock>("WarningSeparator")!;
            var subtitle = view.FindControl<TextBlock>("DeviceSubtitle")!;
            Check(notice.IsVisible && separator.IsVisible && separator.Text == "·"
                && notice.Text == warning.Warnings.Single() && FeatureTestData.Switch(view, "game").IsEnabled
                && view.FindControl<FAInfoBar>("WarningsNotice") is null,
                "The bound notice must preserve notification guidance without claiming a read failure or disabling the game switch.");
            warning = service.Next(); warning.Warnings = [new string('长', 200), "第二项状态提示"];
            service.Push(warning); Dispatcher.UIThread.RunJobs();
            window.Width = 760; window.UpdateLayout();
            Check(notice.Text == string.Join("；", warning.Warnings)
                && Equals(ToolTip.GetTip(notice), string.Join(Environment.NewLine, warning.Warnings))
                && notice.TextTrimming == Avalonia.Media.TextTrimming.CharacterEllipsis
                && subtitle.Bounds.Width > 0 && HeaderClearsSettings(view, notice),
                "Long guidance must remain single-line with complete tooltip and stop before the settings button.");
            var cleared = service.Next(); cleared.Warnings = [];
            service.Push(cleared); Dispatcher.UIThread.RunJobs();
            Check(!notice.IsVisible && !separator.IsVisible && notice.Text == "" && service.Actions.Count == 0,
                "A healthy snapshot must hide and clear the notice without submitting a setting.");
        }
        finally { window.Close(); }
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
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
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
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
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
            Check(service.Actions.Count == 1 && service.Actions[0] is { Kind: "AncMode", Value: 0 }, "Queued main mode sends its captured parent once.");
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
            Check(!dialog.GetVisualDescendants().OfType<ComboBox>().Any(c => Avalonia.Automation.AutomationProperties.GetName(c) == "资源来源"),
                "Single-source settings have no resource selector.");
            var key = dialog.GetVisualDescendants().OfType<TextBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "云端资源密钥");
            key.Text = "edited-a"; Dispatcher.UIThread.RunJobs();
            Check(vm.CloudKey == "key-a", "The dialog keeps key edits local until Save.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Saves == 0 && vm.CloudKey == "key-a", "Cancelling never saves or changes the original key.");

            vm.EqName = "待确认的调整";
            Check(vm.EqDirty, "An unsaved EQ adjustment exists before resource preferences are saved.");
            operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            key = dialog.GetVisualDescendants().OfType<TextBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "云端资源密钥");
            key.Text = "  saved key  ";
            Dispatcher.UIThread.RunJobs();
            var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "保存"));
            Click(window, save); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(confirmations.Count == 0 && service.Saves == 1 && service.Settings.HeyMelodyKey == "  saved key  " && vm.EqDirty,
                "Saving preserves the exact key text and the unsaved EQ adjustment without a confirmation.");
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
        public UiResourceSettings Settings { get; private set; } = new(HeyMelodyKey: "key-a");
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
            var anc = view.FindControl<FASettingsExpander>("AncPanel")!;
            Check(anc.IsExpanded && !section.IsExpanded && view.GetVisualDescendants().OfType<FASettingsExpander>()
                .Where(expander => !ReferenceEquals(expander, anc)).All(expander => !expander.IsExpanded),
                "ANC starts expanded while optional settings start collapsed, including resources with old expansion hints.");
            var ancHeader = anc.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "降噪"
                && !text.GetVisualAncestors().OfType<AncModeSelector>().Any())
                .GetVisualAncestors().OfType<Avalonia.Controls.Primitives.ToggleButton>().First();
            var point = await MainUiTestLayout.ClickPointAsync(window, ancHeader);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Check(!anc.IsExpanded, "The user can collapse the initially expanded ANC panel through its header.");
            var original = section;
            section.IsExpanded = true;
            view.FindControl<FASettingsExpander>("EqualizerPanel")!.IsExpanded = true;
            var snapshot = service.Next(); snapshot.GameMode = true;
            service.Push(FeatureTestData.Decorate(snapshot)); Dispatcher.UIThread.RunJobs();
            section = view.GetVisualDescendants().OfType<FASettingsExpander>().Single(expander => Equals(expander.Header, "连接与体验"));
            Check(ReferenceEquals(section, original) && section.IsExpanded
                && view.FindControl<FASettingsExpander>("EqualizerPanel")!.IsExpanded && !anc.IsExpanded,
                "A replacement definition with the same section ID updates the existing expander and keeps the user's expansion choice.");
            section.IsExpanded = false;
            snapshot = service.Next(); snapshot.GameMode = false;
            service.Push(FeatureTestData.Decorate(snapshot)); Dispatcher.UIThread.RunJobs();
            section = view.GetVisualDescendants().OfType<FASettingsExpander>().Single(expander => Equals(expander.Header, "连接与体验"));
            Check(!section.IsExpanded && !anc.IsExpanded, "Another replacement retains the user's subsequent collapse choice, including ANC.");
            await vm.RefreshStateCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Check(!anc.IsExpanded, "An ordinary state refresh cannot reopen the user's collapsed ANC panel.");
            var real = vm.SelectedDevice;
            await vm.AddDemoDeviceAsync(); Dispatcher.UIThread.RunJobs();
            Check(anc.IsExpanded, "The first demo presentation has its own expanded ANC default.");
            anc.IsExpanded = false;
            await vm.SelectDeviceAsync(real); Dispatcher.UIThread.RunJobs();
            Check(!anc.IsExpanded, "Returning to a real device restores its independent ANC collapse choice.");
            anc.IsExpanded = true;
            await vm.SelectDeviceAsync(vm.Devices.Single(MainViewModel.IsDemoDevice)); Dispatcher.UIThread.RunJobs();
            Check(!anc.IsExpanded, "Returning to the demo restores its independent ANC collapse choice.");
            await vm.SelectDeviceAsync(real); Dispatcher.UIThread.RunJobs();
            Check(anc.IsExpanded && service.Actions.Count == 0,
                "Returning to real settings restores its later expansion choice without submitting a headset setting.");
        }
        finally { window.Close(); }
    }

    /// <summary>The header keeps only the settings button, centred on both lines, and the warning stops before it.</summary>
    private static bool HeaderClearsSettings(MainView view, TextBlock notice)
    {
        var header = view.FindControl<Grid>("DeviceHeader")!;
        var settings = view.FindControl<Button>("AppSettingsButton")!;
        var noticeRight = notice.TranslatePoint(new Point(notice.Bounds.Width, 0), header);
        var settingsBox = settings.TranslatePoint(default, header);
        return view.FindControl<Button>("RefreshStateButton") is null && view.FindControl<Button>("ConnectButton") is null
            && settings.Bounds.Width > 0 && noticeRight is { } right && settingsBox is { } box && right.X <= box.X + .5
            && Math.Abs(box.Y + settings.Bounds.Height / 2 - header.Bounds.Height / 2) <= 1;
    }

    private static Window CreateWindow(MainView view) => new()
        { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };

    private static void AddAdaptiveMode(UiSnapshot snapshot)
    {
        UiOption[] adaptive = [new(0x0B0C, "更少噪音"), new(0x0B0D, "标准"), new(0x0B0E, "更多噪音")];
        snapshot.AncModes = snapshot.AncOptions.GroupBy(x => x.Key >> 8)
            .Select(g => new UiAncMode(g.Key, g.First().Name, "anc", g.ToArray()))
            .Append(new(11, "自适应降噪", "adaptive", adaptive)).ToArray();
        snapshot.AncOptions = snapshot.AncOptions.Concat(adaptive).ToArray();
    }

    private static async Task AncModeInteractionAsync()
    {
        var service = new BlockingService { IncludeAdaptive = true };
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm }; var window = CreateWindow(view);
        try
        {
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
            var selector = view.GetVisualDescendants().OfType<AncModeSelector>().Single();
            var adaptive = selector.GetVisualDescendants().OfType<RadioButton>()
                .Single(x => AutomationProperties.GetName(x) == "自适应降噪");
            Click(window, adaptive); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            Check(service.Actions is [{ Kind: "AncMode", Value: 11 }], "Main radio submits the parent without a child write.");
            var combo = FeatureTestData.Anc(view);
            Check(adaptive.IsChecked == true && combo.SelectedItem is null && combo.PlaceholderText == "档位未确认" && combo.IsEnabled,
                "A confirmed parent keeps the explicit fine adjustment available without inventing its value.");
            // A binding/state update alone must not submit an action.
            combo.SelectedItem = vm.SelectedAncMode!.Options.Last(); Dispatcher.UIThread.RunJobs();
            Check(service.Actions.Count == 1, "Programmatic child selection is not an input commit.");
            combo.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            combo.SelectedItem = vm.SelectedAncMode.Options.Single(x => x.Key == 0x0B0D);
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            Check(service.Actions is [{ Kind: "AncMode", Value: 11 }, { Kind: "Anc", Value: 0x0B0D }],
                "Explicit dropdown input submits exactly one child action.");
        }
        finally { window.Close(); }
    }

    private static async Task AncParentPushAsync()
    {
        var service = new PushService(); await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent; await vm.InitializeAsync();
        var view = new MainView { DataContext = vm }; var window = CreateWindow(view);
        try
        {
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
            var snapshot = service.Next(); AddAdaptiveMode(snapshot);
            snapshot.AncPrimary = 11; snapshot.Anc = null;
            service.Push(snapshot); Dispatcher.UIThread.RunJobs();
            var selector = view.GetVisualDescendants().OfType<AncModeSelector>().Single();
            var adaptive = selector.GetVisualDescendants().OfType<RadioButton>()
                .Single(x => AutomationProperties.GetName(x) == "自适应降噪");
            Check(adaptive.IsChecked == true && FeatureTestData.Anc(view).SelectedItem is null && service.Actions.Count == 0,
                "A parent-only phone push selects the main mode without sending a fallback child.");
            var legacy = service.Next(); legacy.CanSetAncMode = false;
            service.Push(legacy); Dispatcher.UIThread.RunJobs();
            Check(!adaptive.IsEnabled && FeatureTestData.Anc(view).IsEnabled,
                "An old Host's missing main capability disables main radios while explicit children remain available.");
        }
        finally { window.Close(); }
    }

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

    internal sealed class BlockingService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        private UiSnapshot? _snapshot;
        public bool BlockNextRead { get; set; }
        public bool IncludeAdaptive { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<UiAction> Actions { get; } = [];
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        {
            _snapshot = FeatureTestData.Decorate(await _inner.ConnectAsync(identity, token));
            if (IncludeAdaptive) AddAdaptiveMode(_snapshot);
            return _snapshot;
        }
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
            if (action.Kind == "Anc") { _snapshot!.Anc = action.Value; _snapshot.AncPrimary = action.Value >> 8; }
            if (action.Kind == "AncMode") { _snapshot!.AncPrimary = action.Value; _snapshot.Anc = null; }
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
