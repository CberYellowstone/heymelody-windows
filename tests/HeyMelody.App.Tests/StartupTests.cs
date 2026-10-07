using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class StartupTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    public static int Run(HeadlessUnitTestSession session)
    {
        Run("an effective startup whitelist is queried once and does not launch management", EffectiveStartupAsync);
        Run("saving an initial device selection causes exactly one subsequent enumeration", EmptySavedAsync);
        Run("cancelling an empty startup selection returns no devices", EmptyCancelledAsync);
        Run("unpairing after a save keeps startup in management until an effective list exists", SavedUnpairedAsync);
        Run("an enumeration failure propagates without launching the manager", EnumerationFailureAsync);
        Run("manager failures propagate rather than impersonating a saved or cancelled selection", ManagerFailureAsync);
        return 6;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    // FAAppWindow requires real Win32 handles for repeated construction and destruction.
    // These cases run only in the explicit native runner, never in the headless session.
    internal static async Task RunNativeWindowCasesAsync(Action<MainWindow> registerWindow, Action<string> recordPass)
    {
        await Run("startup enumeration failure shows recovery and never opens management automatically", StartupRecoveryAsync);
        await Run("cancelling manual manager recovery restores cached devices without reconnecting", ManualRecoveryAsync);
        await Run("closing during startup enumeration cancels the lookup without launching management", StartupEnumerationClosingAsync);
        await Run("closing while the initial manager is open cancels that manager and disposes once", StartupManagerClosingAsync);
        await Run("closing during manual management cancels the manager without restoring a closed page", ManualManagerClosingAsync);

        async Task Run(string name, Func<Action<MainWindow>, Task> test)
        {
            await test(registerWindow);
            recordPass(name);
        }
    }

    private static async Task EffectiveStartupAsync()
    {
        var service = new Service(); var manager = new Manager();
        var devices = await new DeviceStartupCoordinator(service, manager).ResolveAsync(default);
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync(devices);
        Check(ReferenceEquals(devices, service.EffectiveDevices) && ReferenceEquals(vm.Devices, devices)
            && service.Enumerations == 1 && manager.Calls == 0 && service.Connections == 0
            && vm.SelectedDevice?.Connected == false,
            "An already effective offline selection initializes the main VM without a second lookup or connection.");
    }

    private static async Task EmptySavedAsync()
    {
        var service = new Service();
        service.Enumerate = (count, _) => Task.FromResult<IReadOnlyList<UiDevice>>(count == 1 ? [] : service.EffectiveDevices);
        var manager = new Manager((_, _) => Task.FromResult(DeviceManagerOutcome.Saved));
        var devices = await new DeviceStartupCoordinator(service, manager).ResolveAsync(default);
        Check(devices?.Count == 1 && manager.Calls == 1 && service.Enumerations == 2 && service.Connections == 0,
            "Only a real save permits the coordinator to recheck the initial empty list.");
    }

    private static async Task EmptyCancelledAsync()
    {
        var service = new Service { Enumerate = (_, _) => Task.FromResult<IReadOnlyList<UiDevice>>([]) };
        var manager = new Manager();
        Check(await new DeviceStartupCoordinator(service, manager).ResolveAsync(default) is null
            && service.Enumerations == 1 && manager.Calls == 1,
            "Cancellation without an effective old list tells the main application to exit without another lookup.");
    }

    private static async Task SavedUnpairedAsync()
    {
        var service = new Service();
        service.Enumerate = (count, _) => Task.FromResult<IReadOnlyList<UiDevice>>(count <= 2 ? [] : service.EffectiveDevices);
        var manager = new Manager((_, _) => Task.FromResult(DeviceManagerOutcome.Saved));
        var devices = await new DeviceStartupCoordinator(service, manager).ResolveAsync(default);
        Check(devices?.Count == 1 && service.Enumerations == 3 && manager.Calls == 2 && service.Connections == 0,
            "A saved device unpaired before the lookup cannot let an empty main page start; a subsequent save is revalidated.");
    }

    private static async Task EnumerationFailureAsync()
    {
        var service = new Service
            { Enumerate = (_, _) => Task.FromException<IReadOnlyList<UiDevice>>(new IOException("test-enumeration-failure")) };
        var manager = new Manager();
        await ThrowsAsync<IOException>(() => new DeviceStartupCoordinator(service, manager).ResolveAsync(default));
        Check(service.Enumerations == 1 && manager.Calls == 0,
            "An unavailable paired-record lookup must not be treated as an empty whitelist.");
    }

    private static async Task ManagerFailureAsync()
    {
        var service = new Service { Enumerate = (_, _) => Task.FromResult<IReadOnlyList<UiDevice>>([]) };
        var manager = new Manager((_, _) => Task.FromException<DeviceManagerOutcome>(new FileNotFoundException("test-manager-missing")));
        await ThrowsAsync<FileNotFoundException>(() => new DeviceStartupCoordinator(service, manager).ResolveAsync(default));
        Check(service.Enumerations == 1 && manager.Calls == 1,
            "A failed editor process requires explicit recovery and does not trigger an automatic second launch.");
    }

    private static async Task StartupRecoveryAsync(Action<MainWindow> registerWindow)
    {
        var service = new Service
            { Enumerate = (_, _) => Task.FromException<IReadOnlyList<UiDevice>>(new IOException("test-enumeration-failure")) };
        var manager = new Manager();
        var window = new MainWindow(service, preview: false, manager);
        registerWindow(window);
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var dialog = await FindDialogAsync(window, "无法读取设备列表");
            Check(manager.Calls == 0 && service.Enumerations == 1 && !Page(window).IsEnabled,
                "Startup exposes recovery while preserving the disabled main page and requires an explicit manager choice.");
            dialog.Hide();
            await UntilAsync(() => closed);
            Check(service.Disposals == 1 && service.Connections == 0 && manager.Calls == 0,
                "Dismissing startup recovery exits cleanly without editing the device list or connecting.");
        }
        finally { if (!closed) { window.Close(); await UntilAsync(() => closed); } }
    }

    private static async Task ManualRecoveryAsync(Action<MainWindow> registerWindow)
    {
        var service = new Service();
        var manager = new Manager((_, _) => Task.FromException<DeviceManagerOutcome>(new IOException("test-manager-failure")));
        var window = new MainWindow(service, preview: false, manager);
        registerWindow(window);
        var closed = false; window.Closed += (_, _) => closed = true;
        Task? management = null;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await UntilAsync(() => Page(window).IsEnabled && ViewModel(window).HasDevices);
            await ViewModel(window).ConnectCommand.ExecuteAsync(null);
            management = window.ManageDevicesAsync();
            var dialog = await FindDialogAsync(window, "无法打开设备管理");
            Check(manager.Calls == 1 && service.Disconnections == 1 && !Page(window).IsEnabled,
                "A failed manual launch leaves the prepared session disconnected while asking for explicit retry.");
            dialog.Hide();
            await management.WaitAsync(Deadline);
            Check(Page(window).IsEnabled && ViewModel(window).SelectedDevice?.Identity == "effective-device"
                && !ViewModel(window).HasVerifiedSnapshot && !ViewModel(window).ManagingDevices
                && service.Enumerations == 1 && service.Connections == 1 && service.Submissions == 0,
                "Cancelling retry restores the cached selection and requires an explicit reconnect without enumeration or setters.");
        }
        finally
        {
            if (!closed) { window.Close(); await UntilAsync(() => closed); }
            if (management is not null) await management.WaitAsync(Deadline);
        }
    }

    private static async Task StartupEnumerationClosingAsync(Action<MainWindow> registerWindow)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<IReadOnlyList<UiDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var service = new Service
        {
            Enumerate = async (_, token) =>
            {
                started.TrySetResult();
                try { return await never.Task.WaitAsync(token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
        };
        var manager = new Manager();
        var window = new MainWindow(service, preview: false, manager);
        registerWindow(window);
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await started.Task.WaitAsync(Deadline);
            window.Close();
            await UntilAsync(() => closed && cancelled);
            Check(service.Disposals == 1 && manager.Calls == 0 && !Page(window).IsEnabled
                && !ViewModel(window).HasDevices,
                "Closing cancels the startup lookup and prevents a late initialization or manager launch.");
        }
        finally { if (!closed) { window.Close(); await UntilAsync(() => closed); } }
    }

    private static async Task StartupManagerClosingAsync(Action<MainWindow> registerWindow)
    {
        var service = new Service { Enumerate = (_, _) => Task.FromResult<IReadOnlyList<UiDevice>>([]) };
        var never = new TaskCompletionSource<DeviceManagerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Manager((_, token) => never.Task.WaitAsync(token));
        var window = new MainWindow(service, preview: false, manager);
        registerWindow(window);
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await manager.Started.Task.WaitAsync(Deadline);
            window.Close();
            await UntilAsync(() => closed && manager.Cancelled);
            Check(service.Disposals == 1 && service.Enumerations == 1 && service.Connections == 0
                && !Page(window).IsEnabled && !ViewModel(window).HasDevices,
                "Startup closes after cancelling the external editor and cannot start the main page afterwards.");
        }
        finally { if (!closed) { window.Close(); await UntilAsync(() => closed); } }
    }

    private static async Task ManualManagerClosingAsync(Action<MainWindow> registerWindow)
    {
        var service = new Service();
        var never = new TaskCompletionSource<DeviceManagerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Manager((_, token) => never.Task.WaitAsync(token));
        var window = new MainWindow(service, preview: false, manager);
        registerWindow(window);
        var closed = false; window.Closed += (_, _) => closed = true;
        Task? management = null;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await UntilAsync(() => Page(window).IsEnabled && ViewModel(window).HasDevices);
            await ViewModel(window).ConnectCommand.ExecuteAsync(null);
            management = window.ManageDevicesAsync();
            await manager.Started.Task.WaitAsync(Deadline);
            Check(ViewModel(window).ManagingDevices && !Page(window).IsEnabled && service.Disconnections == 1,
                "Manual management owns a disconnected, disabled main control surface.");
            window.Close();
            await management.WaitAsync(Deadline);
            await UntilAsync(() => closed && manager.Cancelled);
            Check(service.Disposals == 1 && service.Enumerations == 1 && service.Connections == 1
                && service.Submissions == 0 && !ViewModel(window).ManagingDevices && !Page(window).IsEnabled,
                "A cancelled manager cannot restore or re-enumerate a closed client.");
        }
        finally
        {
            if (!closed) { window.Close(); await UntilAsync(() => closed); }
            if (management is not null) await management.WaitAsync(Deadline);
        }
    }

    private static MainView Page(MainWindow window) => window.FindControl<MainView>("Page")!;
    private static MainViewModel ViewModel(MainWindow window) => (MainViewModel)Page(window).DataContext!;

    private static async Task<FAContentDialog> FindDialogAsync(MainWindow window, string title)
    {
        await UntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => Equals(dialog.Title, title)));
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, title));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Startup or management interaction did not finish.");
            await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Manager(Func<int, CancellationToken, Task<DeviceManagerOutcome>>? run = null) : IDeviceManagerProcess
    {
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DeviceManagerOutcome> RunAsync(CancellationToken token)
        {
            Calls++; Started.TrySetResult();
            try { return run is null ? DeviceManagerOutcome.Cancelled : await run(Calls, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Cancelled = true; throw; }
        }
    }

    private sealed class Service : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        public UiDevice[] EffectiveDevices { get; } = [new("effective-device", "离线的已配对耳机", false)];
        public Func<int, CancellationToken, Task<IReadOnlyList<UiDevice>>>? Enumerate { get; set; }
        public int Enumerations, Connections, Disconnections, Disposals, Submissions;
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Enumerations++; return Enumerate?.Invoke(Enumerations, token) ?? Task.FromResult<IReadOnlyList<UiDevice>>(EffectiveDevices); }
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        { Connections++; var snapshot = await _inner.ConnectAsync(identity, token); snapshot.Identity = identity; return snapshot; }
        public Task DisconnectAsync(CancellationToken token) { Disconnections++; return Task.CompletedTask; }
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new InvalidOperationException("Startup tests must not poll.");
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Submissions++; throw new InvalidOperationException("Startup tests must not submit settings."); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
