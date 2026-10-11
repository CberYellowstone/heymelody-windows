using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

/// <summary>0.9.1 header and device-bar interactions: card gestures, F5 keys, combo text and the operation log box.</summary>
internal static class DeviceGestureTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        Run("double click on the current card refreshes once after the triple-click window", DoubleClickAsync);
        Run("triple click reconnects once without refreshing and sends no setting", TripleClickAsync);
        Run("only a click sequence that starts on the current card refreshes", OtherCardAsync);
        Run("real double click on the selected card reaches the gesture through the list", PointerDoubleClickAsync);
        Run("F5 refreshes and Shift+F5 reconnects from the window", KeysAsync);
        Run("combo boxes centre their text and the operation log is a read-only text box", ComboAndLogAsync);
        return 6;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task DoubleClickAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, view, device) = (fixture.Service, fixture.View, fixture.Device);
        var refreshes = service.Refreshes;
        view.HandleDeviceCardPress(device, 1, PointerType.Mouse);
        view.HandleDeviceCardPress(device, 2, PointerType.Mouse);
        Check(service.Refreshes == refreshes, "A double click waits for a possible third click before refreshing.");
        await UntilAsync(() => service.Refreshes == refreshes + 1);
        await Task.Delay(150); Dispatcher.UIThread.RunJobs();
        Check(service.Refreshes == refreshes + 1 && service.Pauses.Count == 0 && service.Resumes.Count == 0 && service.Actions.Count == 0,
            "A double click refreshes exactly once and neither reconnects nor writes.");
    }

    private static async Task TripleClickAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, view, vm, device) = (fixture.Service, fixture.View, fixture.ViewModel, fixture.Device);
        var refreshes = service.Refreshes;
        view.HandleDeviceCardPress(device, 1, PointerType.Mouse);
        view.HandleDeviceCardPress(device, 2, PointerType.Mouse);
        view.HandleDeviceCardPress(device, 3, PointerType.Mouse);
        await UntilAsync(() => service.Resumes.Count == 1);
        await UntilAsync(() => vm.CanRefreshState);
        await Task.Delay(150); Dispatcher.UIThread.RunJobs();
        Check(service.Pauses.SequenceEqual([device.Identity]) && service.Resumes.SequenceEqual([device.Identity])
            && service.ResumedAt - service.PausedAt >= vm.ReconnectDelay - TimeSpan.FromMilliseconds(5),
            "A triple click pauses the device, waits the reconnect delay and resumes it once.");
        Check(service.Refreshes == refreshes && service.Actions.Count == 0 && vm.HasVerifiedSnapshot,
            "The triple click cancels the pending double-click refresh and replays no setting.");
    }

    private static async Task OtherCardAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, view, device) = (fixture.Service, fixture.View, fixture.Device);
        var other = new UiDevice("other-device", "Other", true);
        var refreshes = service.Refreshes;
        view.HandleDeviceCardPress(other, 1, PointerType.Mouse);
        view.HandleDeviceCardPress(other, 2, PointerType.Mouse);
        view.HandleDeviceCardPress(other, 1, PointerType.Mouse);
        view.HandleDeviceCardPress(device, 2, PointerType.Mouse);
        view.HandleDeviceCardPress(device, 3, PointerType.Mouse);
        await Task.Delay(250); Dispatcher.UIThread.RunJobs();
        Check(service.Refreshes == refreshes && service.Pauses.Count == 0 && service.Resumes.Count == 0,
            "Clicks that start on another card switch devices only; they never refresh or reconnect the current one.");
    }

    private static async Task PointerDoubleClickAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, view, window) = (fixture.Service, fixture.View, fixture.Window);
        var picker = view.FindControl<ListBox>("DevicePicker")!;
        var card = (Control)picker.ContainerFromIndex(picker.SelectedIndex)!;
        var centre = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), window)!.Value;
        var refreshes = service.Refreshes;
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
        await UntilAsync(() => service.Refreshes == refreshes + 1);
        Check(service.Pauses.Count == 0 && service.Actions.Count == 0 && picker.SelectedItem is UiDevice { } selected
            && selected.Identity == fixture.Device.Identity, "A real double click on the selected card refreshes it in place.");
    }

    private static async Task KeysAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, window, vm) = (fixture.Service, fixture.Window, fixture.ViewModel);
        var refreshes = service.Refreshes;
        window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        await UntilAsync(() => service.Refreshes == refreshes + 1);
        await UntilAsync(() => vm.CanRefreshState && !vm.Busy);
        window.KeyPress(Key.F5, RawInputModifiers.Shift, PhysicalKey.F5, null);
        window.KeyRelease(Key.F5, RawInputModifiers.Shift, PhysicalKey.F5, null);
        await UntilAsync(() => service.Resumes.Count == 1);
        Check(service.Pauses.Count == 1 && service.Refreshes == refreshes + 1 && service.Actions.Count == 0,
            "F5 refreshes and Shift+F5 reconnects, without any setting writes.");
    }

    private static async Task ComboAndLogAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (view, vm) = (fixture.View, fixture.ViewModel);
        await MainUiTestLayout.ExpandSettingsAsync(view);
        var combos = view.GetVisualDescendants().OfType<ComboBox>().Where(combo => combo.IsEffectivelyVisible).ToArray();
        var shown = combos.Where(combo => combo.SelectedItem is not null).ToArray();
        Check(combos.Length != 0 && combos.All(combo => combo.VerticalContentAlignment == VerticalAlignment.Center)
            && shown.Any(combo => combo.Name == "AncCombo"),
            $"Every combo box centres its content, including the ANC fine adjustment: {string.Join(",", combos.Select(c => c.Name))}.");
        foreach (var combo in shown)
        {
            var presenter = combo.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "ContentPresenter");
            var text = presenter.GetVisualDescendants().OfType<TextBlock>().First();
            var middle = text.TranslatePoint(new Point(0, text.Bounds.Height / 2), combo)!.Value.Y;
            Check(Math.Abs(middle - combo.Bounds.Height / 2) <= 1,
                $"{combo.Name} text sits within 1 px of the box centre: text middle {middle:0.0}, box {combo.Bounds.Height:0.0}.");
        }
        var log = view.FindControl<TextBox>("OperationLogText")!;
        Check(log.IsReadOnly && log.MaxHeight == 200 && log.TextWrapping == Avalonia.Media.TextWrapping.Wrap
            && log.Text == vm.LogText && view.FindControl<Button>("CopyOperationLogButton") is null,
            "The operation log is the read-only, wrapped 200 px text box bound to the full log again.");
    }

    private static async Task UntilAsync(Func<bool> condition, int milliseconds = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new Exception("Timed out waiting for a device gesture result.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        public required GestureService Service { get; init; }
        public required MainViewModel ViewModel { get; init; }
        public required MainView View { get; init; }
        public required Window Window { get; init; }
        public UiDevice Device => ViewModel.SelectedDevice!;

        public static async Task<Fixture> CreateAsync()
        {
            var service = new GestureService();
            var vm = new MainViewModel(service, new NoConfirm()) { ReconnectDelay = TimeSpan.FromMilliseconds(120) };
            service.EventReceived += vm.AdoptBackendEvent;
            await vm.InitializeAsync();
            var view = new MainView { DataContext = vm, DeviceGestureDelayOverride = TimeSpan.FromMilliseconds(80) };
            var window = new Window { Content = view, Width = 1280, Height = 900, WindowDecorations = WindowDecorations.None };
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check(vm.HasVerifiedSnapshot && vm.CanReconnect, "The fixture starts with a verified, monitored device.");
            return new() { Service = service, ViewModel = vm, View = view, Window = window };
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
        }
    }

    /// <summary>An event backend that records pauses, resumes and refreshes and never accepts a setting.</summary>
    private sealed class GestureService : IHeadsetEventService
    {
        private readonly PreviewService _inner = new();
        private UiSnapshot? _snapshot;
        public event Action<BackendEvent>? EventReceived;
        public List<UiAction> Actions { get; } = [];
        public List<string> Pauses { get; } = [];
        public List<string> Resumes { get; } = [];
        public int Refreshes { get; private set; }
        public TimeSpan PausedAt { get; private set; }
        public TimeSpan ResumedAt { get; private set; }
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public async Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        {
            var devices = await _inner.EnumerateAsync(token);
            _snapshot = FeatureTestData.Decorate(await _inner.ConnectAsync(devices[0].Identity, token));
            _snapshot.BackendInstanceId = "gesture-test-backend"; _snapshot.DeviceGeneration = 1; _snapshot.Revision = 1;
            _snapshot.ControlReady = true; _snapshot.MonitoringState = MonitoringState.Monitoring;
            return new() { BackendInstanceId = _snapshot.BackendInstanceId, Devices = devices, Snapshots = [_snapshot] };
        }

        private UiSnapshot Next(MonitoringState state, bool ready)
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<UiSnapshot>(System.Text.Json.JsonSerializer.Serialize(_snapshot))!;
            snapshot.Revision++; snapshot.MonitoringState = state; snapshot.ControlReady = ready;
            if (ready) snapshot.DeviceGeneration++;
            _snapshot = snapshot;
            return System.Text.Json.JsonSerializer.Deserialize<UiSnapshot>(System.Text.Json.JsonSerializer.Serialize(snapshot))!;
        }

        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => Task.FromResult(_snapshot!);
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token)
        { Pauses.Add(identity); PausedAt = _clock.Elapsed; return Task.FromResult(Next(MonitoringState.Paused, false)); }
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token)
        { Resumes.Add(identity); ResumedAt = _clock.Elapsed; return Task.FromResult(Next(MonitoringState.Monitoring, true)); }
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token)
        { Refreshes++; return Task.FromResult(_snapshot!); }
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token)
        { Actions.Add(action); throw new InvalidOperationException("Gestures must never send a setter."); }
        public Task AcknowledgeExitAsync(string requestId, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task DisconnectAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
