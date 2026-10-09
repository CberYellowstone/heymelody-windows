using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

internal static class GameAudioInteractionTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        Run("rejected game controls restore unchanged observations and show one alert", RejectedAsync);
        Run("game type 3 reconciles unknown acknowledgement using reads without replay", UnknownAsync);
        Run("spatial mode 2 and Hi-Res submit independently with both states displayed", IndependentAudioAsync);
        return 3;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task RejectedAsync()
    {
        var service = new AudioService { Reject = true };
        var dialogs = new CountingDialogs();
        await using var vm = new MainViewModel(service, dialogs);
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm };
        var window = Window(view);
        try
        {
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
            view.FindControl<TextBox>("EqNameEditor")!.Text = "保留我的 EQ 草稿";
            var toggle = FeatureTestData.Switch(view, "game-sound-enabled");
            var original = (FeatureControlViewModel)toggle.DataContext!;
            await ClickAsync(window, toggle, 16); await SettledAsync(vm);
            Check(service.Actions is [{ FeatureId: "game-sound-enabled", Value: 1 }]
                && ReferenceEquals(original, FeatureTestData.Switch(view, "game-sound-enabled").DataContext)
                && toggle.IsChecked == false && !original.BooleanValue,
                "A rejected SET with unchanged definitions must restore the reused switch.");
            Check(dialogs.Alerts == 1 && dialogs.Retries == 0 && dialogs.Confirmations == 0,
                "One game click must produce one alert and no retry invitation.");

            var combo = Combo(view);
            combo.Focus(); window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null); Dispatcher.UIThread.RunJobs();
            await SettledAsync(vm);
            Check(service.Actions.Count == 2 && service.Actions[1] is { FeatureId: "game-sound", Value: 3 }
                && ((UiOption?)Combo(view).SelectedItem)?.Key == 0 && dialogs.Alerts == 2 && dialogs.Retries == 0,
                $"A rejected game type must submit type 3 once and restore the observed selection: actions={string.Join(",", service.Actions)}, selected={((UiOption?)Combo(view).SelectedItem)?.Key}, alerts={dialogs.Alerts}, retries={dialogs.Retries}.");
            Check(vm.EqDirty && vm.EqName == "保留我的 EQ 草稿", "Settling game controls must preserve unrelated drafts.");
            await vm.RefreshStateCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Check(service.Actions.Count == 2, "Programmatic rollback and GET refresh must never generate SET.");
        }
        finally { window.Close(); }
    }

    private static async Task UnknownAsync()
    {
        var service = new AudioService { UnknownAck = true };
        var dialogs = new CountingDialogs();
        await using var vm = new MainViewModel(service, dialogs);
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm }; var window = Window(view);
        try
        {
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
            var combo = Combo(view);
            Check(combo.IsEnabled && FeatureTestData.Switch(view, "game-sound-enabled").IsChecked == false,
                "Types remain selectable while the independently observed game switch is off.");
            int reads = service.Reads;
            combo.Focus(); window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null); Dispatcher.UIThread.RunJobs(); await SettledAsync(vm);
            Check(service.Actions is [{ FeatureId: "game-sound", Value: 3 }] && service.Reads > reads
                && ((UiOption?)Combo(view).SelectedItem)?.Key == 3,
                "Unknown acknowledgement must reconcile the actual type by GET without another SET.");
            Check(vm.LastApplyAttempts is [{ Accepted: false, AckStatus: null, AcknowledgementUnknown: true }]
                && vm.StatusWarning && dialogs.Alerts == 0 && dialogs.Retries == 0,
                "Matching readback must retain unknown ACK evidence and a truthful warning.");
            Check(FeatureTestData.Switch(view, "game-sound-enabled").IsChecked == false,
                "The UI must publish the observed switch rather than assuming type activation sets feature 27.");
        }
        finally { window.Close(); }
    }

    private static async Task IndependentAudioAsync()
    {
        var service = new AudioService(); var dialogs = new CountingDialogs();
        await using var vm = new MainViewModel(service, dialogs);
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm }; var window = Window(view);
        try
        {
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
            var spatial = view.GetVisualDescendants().OfType<RadioButton>().Single(button =>
                button.DataContext is FeatureControlViewModel { Id: "spatial" } && Equals(button.Content, "头部跟踪"));
            await ClickAsync(window, spatial); await SettledAsync(vm);
            Check(service.Actions is [{ FeatureId: "spatial", Value: 2 }] && vm.HiRes == true && vm.SelectedSpatial?.Key == 2,
                "Spatial option 2 must pass discrete validation while Hi-Res stays visibly enabled.");
            var hires = FeatureTestData.Switch(view, "hires");
            await ClickAsync(window, hires, 16); await SettledAsync(vm);
            await ClickAsync(window, FeatureTestData.Switch(view, "hires"), 16); await SettledAsync(vm);
            Check(service.Actions.Count == 3 && service.Actions[1] is { FeatureId: "hires", Value: 0 }
                && service.Actions[2] is { FeatureId: "hires", Value: 1 }
                && vm.HiRes == true && vm.SelectedSpatial?.Key == 2 && dialogs.Confirmations == 0 && dialogs.Alerts == 0,
                "Each Hi-Res click must send only its own action with no mutex dialog or forced spatial reset.");
            await vm.RefreshStateCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Check(service.Actions.Count == 3 && FeatureTestData.Switch(view, "hires").IsChecked == true,
                "Readback must retain both actual values without another write.");
        }
        finally { window.Close(); }
    }

    private static ComboBox Combo(Control root) => root.GetVisualDescendants().OfType<ComboBox>()
        .Single(control => control.DataContext is FeatureControlViewModel { Id: "game-sound" });
    private static Window Window(Control view) => new() { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
    private static async Task ClickAsync(Window window, Control control, double? x = null)
    {
        var point = await MainUiTestLayout.ClickPointAsync(window, control, x);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task SettledAsync(MainViewModel vm)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do { await Task.Delay(10, budget.Token); Dispatcher.UIThread.RunJobs(); } while (vm.Busy);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class CountingDialogs : IConfirmationService
    {
        public int Alerts, Retries, Confirmations;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) { Confirmations++; return Task.FromResult(true); }
        public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) { Retries++; return Task.FromResult(true); }
        public Task AlertAsync(string title, string message, CancellationToken token) { Alerts++; return Task.CompletedTask; }
    }

    private sealed class AudioService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        private UiSnapshot _snapshot = new();
        private int _gameType;
        public bool Reject, UnknownAck;
        public int Reads;
        public List<UiAction> Actions { get; } = [];
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        {
            _snapshot = await _inner.ConnectAsync(identity, token);
            _snapshot.HiRes = true; _snapshot.Spatial = 0; _snapshot.SpatialHiResMutex = true;
            _snapshot.SpatialOptions = [new(0, "关闭"), new(1, "固定"), new(2, "头部跟踪")];
            _snapshot.ConnectionState = ConnectionState.Connected; Decorate(); return Copy();
        }
        private void Decorate() => _snapshot.FeatureSections = [new("audio", "声音设置", true,
        [new("game-sound-enabled", "游戏音效", "Switch", 0, CanWrite: true),
         new("game-sound", "游戏音效类型", "Combo", _gameType, Options: [new(3, "射击类游戏音效"), new(0, "关闭")], CanWrite: true),
         new("spatial", "空间音效", "Radio", _snapshot.Spatial, Options: _snapshot.SpatialOptions, CanWrite: true),
         new("hires", "Hi-Res", "Switch", _snapshot.HiRes == true ? 1 : 0, CanWrite: true)])];
        private UiSnapshot Copy() => System.Text.Json.JsonSerializer.Deserialize<UiSnapshot>(System.Text.Json.JsonSerializer.Serialize(_snapshot))!;
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) { Reads++; return Task.FromResult(Copy()); }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Actions.Add(action); var before = Copy();
            if (!Reject)
            {
                if (action.FeatureId == "game-sound") _gameType = action.Value;
                if (action.FeatureId == "spatial") _snapshot.Spatial = action.Value;
                if (action.FeatureId == "hires") _snapshot.HiRes = action.Value == 1;
                _snapshot.Revision++; Decorate();
            }
            return Task.FromResult(new UiApplyResult
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                SubmissionAttempted = true, TargetSubmitted = true,
                Accepted = !Reject && !UnknownAck, AckStatus = Reject ? (byte)1 : UnknownAck ? null : (byte)0,
                AcknowledgementUnknown = UnknownAck, ReadbackMatches = !Reject && !UnknownAck,
                FailureKind = Reject ? ApplyFailure.PeerRejected : UnknownAck ? ApplyFailure.TransportUnknown : ApplyFailure.None,
                After = UnknownAck ? before : Copy()
            });
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
