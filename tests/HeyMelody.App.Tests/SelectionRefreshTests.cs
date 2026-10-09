using System.Text.Json;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

internal static class SelectionRefreshTests
{
    public static int Run(HeadlessUnitTestSession session, string output, List<object> receipts)
    {
        Run("disconnected startup keeps selection through connection catalog and enables controls without another click", ConnectionRefreshAsync);
        Run("device identity survives sorting, replacement records and an internal empty selection", MultipleDevicesAsync);
        Run("backend restart restores preferred device with new identities and cached controls", BackendRestartAsync);
        Run("offline controls stay visible and disabled until a fresh capability snapshot replaces them", () => CachedControlsAsync(output, receipts));
        return 4;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task ConnectionRefreshAsync()
    {
        var service = await CatalogService.CreateAsync(connected: false);
        await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = WindowFor(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check(vm.SelectedDevice?.Identity == "a" && !vm.HasVerifiedSnapshot, "Disconnected A is selected at startup.");
            var selects = service.Selections.Count;
            service.Catalog(service.Devices.Select(device => device with { Connected = true, ProductId = "06EC10", Color = 2 }).ToArray());
            Dispatcher.UIThread.RunJobs();
            CheckSelection(view, vm, "a");
            Check(service.Selections.Count == selects, "A status-only catalog does not reselect or detach A.");
            service.Ready("a"); Dispatcher.UIThread.RunJobs();
            Check(vm.HasVerifiedSnapshot && FeatureTestData.Anc(view).IsEnabled && service.Actions.Count == 0,
                "The pushed ready snapshot enables A without user input or any SET.");
        }
        finally { window.Close(); }
    }

    private static async Task MultipleDevicesAsync()
    {
        var service = await CatalogService.CreateAsync(connected: true);
        await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = WindowFor(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = view.FindControl<ListBox>("DevicePicker")!;
            var item = picker.GetVisualDescendants().OfType<ListBoxItem>().Single(control => control.DataContext is UiDevice { Identity: "b" });
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            CheckSelection(view, vm, "b");
            var selects = service.Selections.Count;
            service.Catalog(service.Devices.Reverse().Select(device => device with { Connected = false, Name = device.Name + " updated" }).ToArray());
            Dispatcher.UIThread.RunJobs();
            CheckSelection(view, vm, "b");
            picker.SetCurrentValue(ListBox.SelectedItemProperty, null);
            Dispatcher.UIThread.RunJobs();
            CheckSelection(view, vm, "b");
            service.Catalog(service.Devices.Select(device => device with { Color = 3 }).ToArray());
            Dispatcher.UIThread.RunJobs();
            CheckSelection(view, vm, "b");
            Check(service.Selections.Count == selects && service.Actions.Count == 0,
                "Internal empty selection and catalog replacements never become user switches.");
        }
        finally { window.Close(); }
    }

    private static async Task BackendRestartAsync()
    {
        var service = await CatalogService.CreateAsync(connected: true, preferB: true);
        await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = WindowFor(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            CheckSelection(view, vm, "b");
            service.Restart(); Dispatcher.UIThread.RunJobs();
            CheckSelection(view, vm, "new-b");
            Check(vm.PresentationCached && !vm.HasVerifiedSnapshot && vm.FeatureSections.Count > 0,
                "New backend identities restore the preferred device's readonly presentation.");
            service.Ready("new-b"); Dispatcher.UIThread.RunJobs();
            Check(vm.HasVerifiedSnapshot && !vm.PresentationCached && service.Actions.Count == 0,
                "The new backend enables control only after its ready snapshot arrives.");
        }
        finally { window.Close(); }
    }

    private static async Task CachedControlsAsync(string output, List<object> receipts)
    {
        var service = await CatalogService.CreateAsync(connected: true);
        await using var vm = new MainViewModel(service, new NoConfirm());
        service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = WindowFor(view);
        try
        {
            window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
            service.Offline("a"); Dispatcher.UIThread.RunJobs();
            Check(vm.PresentationCached && vm.PresentationSampleTime is not null && vm.AncModes.Count > 0 && vm.EqEntries.Count > 0,
                "Offline presentation retains modes, EQ entries and cache timestamp.");
            Check(!FeatureTestData.Anc(view).IsEnabled && !FeatureTestData.Switch(view, "game").IsEnabled
                && !view.FindControl<ItemsControl>("BandEditor")!.IsEnabled && !vm.CanFitTest && !vm.CanSetupHearing,
                "All device operations are disabled without hiding the last controls.");
            Check(view.FindControl<Control>("CachedSettingsNotice")!.IsVisible, "The offline cache notice is visible.");
            foreach (var dark in new[] { false, true })
            foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
            {
                Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                window.Width = size.Width; window.Height = size.Height;
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame() ?? throw new Exception("Missing offline frame");
                var name = $"offline-cached-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}.png";
                var path = Path.Combine(output, name);
                using (var stream = new FileStream(path, FileMode.CreateNew)) frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                receipts.Add(new { scenario = "offline-cached", theme = dark ? "dark" : "light", width = size.Width,
                    height = size.Height, file = name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
            }
            await vm.FeatureSections.SelectMany(section => section.Controls).Single(control => control.Id == "game").SubmitAsync();
            await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Actions.Count == 0, "Offline commands do not queue or submit writes.");
            var updated = service.ReadySnapshot("a");
            updated.FeatureSections = [new("firmware-new", "新固件功能", false,
                [new("new-control", "新增功能", "Switch", 1, CanWrite: true)])];
            updated.Firmware = "0.8.7-test";
            service.Push(updated); Dispatcher.UIThread.RunJobs();
            Check(vm.HasVerifiedSnapshot && vm.Firmware == "0.8.7-test"
                && vm.FeatureSections.SelectMany(section => section.Controls).Select(control => control.Id).SequenceEqual(["new-control"]),
                "Fresh firmware features replace the cache, including removal of previous controls.");
            Check(!view.FindControl<Control>("CachedSettingsNotice")!.IsVisible, "Fresh state hides the offline cache notice.");
            Check(service.Actions.Count == 0, "Reconnect does not replay offline actions.");
        }
        finally { window.Close(); }
    }

    private static Window WindowFor(MainView view) => new() { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
    private static void CheckSelection(MainView view, MainViewModel vm, string identity)
    {
        var picker = view.FindControl<ListBox>("DevicePicker")!;
        var canonical = vm.Devices.Single(device => device.Identity == identity);
        Check(ReferenceEquals(vm.SelectedDevice, canonical) && ReferenceEquals(picker.SelectedItem, canonical),
            "VM and ListBox select the current device record: " + identity);
        Check(picker.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is UiDevice device && device.Identity == identity).IsSelected,
            "Selected device highlight survives: " + identity);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class NoConfirm : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false); }

    private sealed class CatalogService : IHeadsetEventService
    {
        private string _backend = "catalog-backend";
        private readonly Dictionary<string, UiSnapshot> _snapshots = [];
        public IReadOnlyList<UiDevice> Devices { get; private set; } = [];
        public List<string> Selections { get; } = [];
        public List<UiAction> Actions { get; } = [];
        public event Action<BackendEvent>? EventReceived;
        public static async Task<CatalogService> CreateAsync(bool connected, bool preferB = false)
        {
            var service = new CatalogService();
            service.Devices = [new("a", "Headset A", connected), new("b", "Headset B", connected) { IsPreferredSelection = preferB }];
            await using var preview = new PreviewService();
            var devices = await preview.EnumerateAsync(CancellationToken.None);
            var template = FeatureTestData.Decorate(await preview.ConnectAsync(devices[0].Identity, CancellationToken.None));
            foreach (var device in service.Devices)
            {
                var snapshot = Clone(template);
                snapshot.Identity = device.Identity; snapshot.DeviceName = device.Name;
                snapshot.BackendInstanceId = service._backend; snapshot.DeviceGeneration = 1; snapshot.Revision = 1;
                snapshot.ControlReady = connected; snapshot.Connected = connected;
                snapshot.ConnectionState = connected ? ConnectionState.Connected : ConnectionState.Disconnected;
                snapshot.MonitoringState = connected ? MonitoringState.Monitoring : MonitoringState.WaitingForConnection;
                snapshot.PresentationCached = !connected; snapshot.PresentationSampleTime = snapshot.SampleTime;
                service._snapshots.Add(device.Identity, snapshot);
            }
            return service;
        }
        public void Catalog(IReadOnlyList<UiDevice> devices)
        {
            Devices = devices;
            EventReceived?.Invoke(new() { Kind = "catalog", BackendInstanceId = _backend, Devices = devices });
        }
        public UiSnapshot ReadySnapshot(string identity)
        {
            var snapshot = Clone(_snapshots[identity]);
            snapshot.Revision++; snapshot.ControlReady = true; snapshot.Connected = true;
            snapshot.ConnectionState = ConnectionState.Connected; snapshot.MonitoringState = MonitoringState.Monitoring;
            snapshot.PresentationCached = false; return snapshot;
        }
        public void Ready(string identity) => Push(ReadySnapshot(identity));
        public void Offline(string identity)
        {
            var snapshot = Clone(_snapshots[identity]); snapshot.DeviceGeneration++; snapshot.Revision++;
            snapshot.ControlReady = false; snapshot.Connected = false; snapshot.ConnectionState = ConnectionState.Disconnected;
            snapshot.MonitoringState = MonitoringState.WaitingForConnection;
            snapshot.PresentationCached = true; snapshot.PresentationSampleTime = snapshot.SampleTime;
            snapshot.ObservationFresh = false;
            Push(snapshot);
        }
        public void Restart()
        {
            EventReceived?.Invoke(new() { Kind = "unavailable", Message = "backend restarted" });
            _backend = "new-catalog-backend";
            var old = _snapshots.ToArray(); _snapshots.Clear();
            foreach (var (identity, value) in old)
            {
                var snapshot = Clone(value); snapshot.Identity = "new-" + identity; snapshot.BackendInstanceId = _backend;
                snapshot.DeviceGeneration = 1; snapshot.Revision = 1; snapshot.Connected = false; snapshot.ControlReady = false;
                snapshot.ConnectionState = ConnectionState.Disconnected; snapshot.PresentationCached = true;
                snapshot.PresentationSampleTime = snapshot.SampleTime;
                _snapshots.Add(snapshot.Identity, snapshot);
            }
            Catalog(Devices.Select(device => device with { Identity = "new-" + device.Identity, Connected = false }).ToArray());
            foreach (var snapshot in _snapshots.Values.ToArray()) Push(snapshot);
        }
        public void Push(UiSnapshot snapshot)
        { _snapshots[snapshot.Identity] = snapshot; EventReceived?.Invoke(new() { Kind = "snapshot", BackendInstanceId = _backend, Snapshot = snapshot }); }
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token) => Task.FromResult(new BackendSubscription
        { BackendInstanceId = _backend, Devices = Devices, Snapshots = _snapshots.Values.ToArray() });
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult(Devices);
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token)
        { Selections.Add(identity); return Task.FromResult(_snapshots[identity]); }
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => Task.FromResult(_snapshots[identity]);
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => Task.FromResult(_snapshots[identity]);
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshots[identity]);
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token)
        { Actions.Add(action); throw new InvalidOperationException("No test operation may send a SET."); }
        public Task AcknowledgeExitAsync(string requestId, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static UiSnapshot Clone(UiSnapshot snapshot) => JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(snapshot))!;
    }
}
