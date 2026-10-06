using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

/// <summary>A real Win32 window with an in-memory service; never starts a Bluetooth worker.</summary>
internal static class NativeSmoke
{
    private static SmokeState _state = new();

    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, reason = "Windows required", nativeBluetoothCalls = 0 }));
            return 2;
        }
        _state = new();
        var uiThread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().LogToTrace()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
        }) { IsBackground = true, Name = "HeyMelody native smoke UI" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        if (!uiThread.Join(TimeSpan.FromSeconds(20))) _state.Failure ??= "Native UI thread did not stop in twenty seconds.";
        var success = _state.Failure is null && _state.Closed && _state.Service.Disposals == 1
            && _state.Service.Submissions == 0 && _state.Checks > 0;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, success, passed = _state.Checks, nativeBluetoothCalls = 0,
            applicationSetSubmissions = _state.Service.Submissions,
            serviceDisposals = _state.Service.Disposals, windowClosed = _state.Closed,
            settingsDialogCompleted = _state.SettingsDialogCompleted,
            scope = "real Win32 FAAppWindow geometry, themes and lifecycle with an in-memory service; no DWM pixel or hardware assertions",
            states = _state.Receipts, failure = _state.Failure
        }));
        return success ? 0 : 1;
    }

    private sealed class SmokeState
    {
        public SmokeService Service { get; } = new();
        public int Checks { get; set; }
        public bool Closed { get; set; }
        public bool SettingsDialogCompleted { get; set; }
        public string? Failure { get; set; }
        public List<object> Receipts { get; } = [];
        public TaskCompletionSource ClosedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Base.Initialize is the App's compiled XAML population method; do not invoke the
    // App's framework completion override, which would create the live backend.
    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime required.");
            var window = new MainWindow(_state.Service, preview: true)
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(-10000, -10000)
            };
            desktop.MainWindow = window;
            window.Closed += (_, _) =>
            {
                _state.Closed = true;
                _state.ClosedSignal.TrySetResult();
            };
            window.Opened += (_, _) => _ = VerifyAndCloseAsync(window, desktop);
            // A separate watchdog also covers an initialization/close failure which
            // otherwise could leave a native test window or its dispatcher running.
            _ = StopOnTimeoutAsync(window, desktop);
        }
    }

    private static async Task VerifyAndCloseAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        Task? settingsTask = null;
        try
        {
            var page = window.FindControl<MainView>("Page") ?? throw new InvalidOperationException("MainView missing.");
            var vm = page.DataContext as MainViewModel ?? throw new InvalidOperationException("View model missing.");
            for (int attempt = 0; !vm.HasVerifiedSnapshot && attempt < 40; attempt++) await Task.Delay(50);
            Check(vm.HasVerifiedSnapshot, "Fake snapshot initializes the real window.");
            var handle = window.TryGetPlatformHandle();
            Check(handle is not null && handle.Handle != IntPtr.Zero && handle.HandleDescriptor == "HWND", "Real Win32 HWND.");
            Check(window.Icon is not null && window.Title == "HeyMelody", "Title and icon loaded.");
            foreach (var dark in new[] { false, true })
            foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
            {
                ((App)Application.Current!).ApplyTheme(dark ? "dark" : "light");
                window.Width = size.Width; window.Height = size.Height;
                await Task.Delay(150);
                Dispatcher.UIThread.RunJobs();
                Check(window.ActualThemeVariant == (dark ? ThemeVariant.Dark : ThemeVariant.Light), "Theme reaches native window.");
                var titleBar = window.GetVisualDescendants().OfType<Panel>().FirstOrDefault(control => control.Name == "DefaultTitleBar");
                Check(titleBar is { IsVisible: true } && titleBar.Bounds.Height > 0, "Managed Windows titlebar is visible.");
                Check(window.TemplateSettings.IsTitleBarContentVisible && window.TitleBar.Height > 0, "Titlebar settings preserve content separation.");
                var pageOrigin = page.TranslatePoint(default, window) ?? throw new InvalidOperationException("View is detached.");
                Check(page.Bounds.Width > 0 && page.Bounds.Height > 0 && pageOrigin.Y >= window.TitleBar.Height - 1, "Content fits below titlebar.");
                Check(page.IsCompact == (page.Bounds.Width < 1080), "Native responsive breakpoint.");
                Check(page.FindControl<ProductArtwork>("HeroArt")?.ShowX4 == true, "Product visual reflects connected X4.");
                var picker = page.FindControl<ListBox>("DevicePicker") ?? throw new InvalidOperationException("Picker missing.");
                var pickerOrigin = picker.TranslatePoint(default, page) ?? throw new InvalidOperationException("Picker is detached.");
                Check(picker.IsVisible && picker.Bounds.Height > 0 && pickerOrigin.Y >= 0
                    && pickerOrigin.Y + picker.Bounds.Height <= page.Bounds.Height + 1, "Picker remains inside native content viewport.");
                _state.Receipts.Add(new
                {
                    theme = dark ? "dark" : "light", requestedWidth = size.Width, requestedHeight = size.Height,
                    clientWidth = window.ClientSize.Width, clientHeight = window.ClientSize.Height,
                    contentWidth = page.Bounds.Width, contentHeight = page.Bounds.Height,
                    titleBarHeight = titleBar!.Bounds.Height, compact = page.IsCompact
                });
            }
            Check(_state.Service.Submissions == 0, "Native smoke never submits settings.");
            settingsTask = page.OpenSettingsDialogAsync();
            await Task.Delay(100);
            Check(!settingsTask.IsCompleted && window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(dialog => Equals(dialog.Title, "应用设置")), "Settings dialog opens on the real owner.");
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            window.Close();
            try { await _state.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { _state.Failure ??= "Native window did not close in five seconds."; }
            if (settingsTask is not null)
            {
                try
                {
                    await settingsTask.WaitAsync(TimeSpan.FromSeconds(2));
                    _state.SettingsDialogCompleted = true;
                    Check(_state.Service.Submissions == 0, "Closing settings leaves no write or pending dialog.");
                }
                catch (Exception error) { _state.Failure ??= error.ToString(); }
            }
            if (_state.Service.Disposals != 1) _state.Failure ??= "Fake service was not disposed exactly once.";
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static async Task StopOnTimeoutAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (_state.Closed) return;
        _state.Failure ??= "Native smoke exceeded twelve seconds.";
        window.Close();
        desktop.Shutdown(1);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _state.Checks++;
    }

    private sealed class SmokeService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        public int Submissions { get; private set; }
        public int Disposals { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => _inner.RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Submissions++;
            throw new InvalidOperationException("Native smoke forbids settings.");
        }
        public ValueTask DisposeAsync() { Disposals++; return _inner.DisposeAsync(); }
    }
}
