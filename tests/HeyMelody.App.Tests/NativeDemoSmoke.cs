using System.Security.Cryptography;
using System.Text.Json;
using Ava3D;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

/// <summary>Runs the dedicated demo startup in a real Win32 window without a Bluetooth backend.</summary>
internal static class NativeDemoSmoke
{
    private static State _state = new();

    public static int Run(string? output = null)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        _state = new() { Output = output };
        if (output is not null) Directory.CreateDirectory(output);
        var thread = new Thread(() =>
        {
            var oldPath = ThemePreferences.SettingsPathOverride;
            var oldPreference = ModelRenderingPreferences.UseGpuAcceleration;
            _state.PreferencesPath = Path.Combine(output ?? Path.GetTempPath(),
                "unwritten-demo-preferences-" + Guid.NewGuid().ToString("N") + ".json");
            ThemePreferences.SettingsPathOverride = _state.PreferencesPath;
            try
            {
                ModelRenderingPreferences.Reload();
                AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().LogToTrace()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
                Check(!File.Exists(_state.PreferencesPath), "Native demo verification never writes user preferences.");
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
            finally { ThemePreferences.SettingsPathOverride = oldPath; ModelRenderingPreferences.Apply(oldPreference); }
        }) { IsBackground = true, Name = "HeyMelody native demo smoke" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(40))) _state.Failure ??= "Native demo window did not stop within forty seconds.";
        bool success = _state.Failure is null && _state.Completed && _state.Closed;
        var report = new
        {
            success, passed = _state.Passed, windowClosed = _state.Closed,
            nativeBluetoothCalls = 0, calibrationProcessLaunches = _state.CalibrationLaunches,
            headsetOrArtworkCalls = _state.Service.ForbiddenCalls, initialMockEnumerations = _state.Service.Enumerations,
            modelSha256 = DynaudioModelTests.ExpectedSha256, _state.FirstRenderedTriangles, _state.FirstRenderedSceneItems,
            _state.PreferredBackend, _state.ActiveBackend, controlLayouts = _state.ControlLayouts,
            preferencesWritten = _state.PreferencesPath is { } preferencePath && File.Exists(preferencePath),
            screenshots = _state.Screenshots,
            scope = "real Win32 demo startup, model scene and animation, themes, responsive layout, local action guards and dirty close; optional native Ava3D model-frame captures; no Bluetooth backend",
            failure = _state.Failure
        };
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (output is not null)
            File.WriteAllText(Path.Combine(output, "native-demo-receipts.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }

    private sealed class State
    {
        public int Passed, CalibrationLaunches;
        public bool Completed, Closed;
        public string? Failure;
        public string? Output;
        public string? PreferencesPath, PreferredBackend, ActiveBackend;
        public long FirstRenderedTriangles;
        public int FirstRenderedSceneItems;
        public DemoService Service { get; } = new();
        public List<object> Screenshots { get; } = [];
        public List<object> ControlLayouts { get; } = [];
    }

    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new MainWindow(_state.Service, preview: true)
            {
                StartWithDemo = true, ShowActivated = _state.Output is not null,
                WindowStartupLocation = _state.Output is null ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen,
                Topmost = _state.Output is not null
            };
            if (_state.Output is null) window.Position = new(-10000, -10000);
            window.FindControl<MainView>("Page")!.SetModelDemo();
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            window.Opened += (_, _) => _ = VerifyAsync(window, desktop);
            _ = WatchdogAsync(desktop);
        }
    }

    private static async Task VerifyAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var page = window.FindControl<MainView>("Page")!;
        var vm = (MainViewModel)page.DataContext!;
        try
        {
            await UntilAsync(() => vm.IsDemoSelected && vm.HasVerifiedSnapshot, "Dedicated startup selects the local demo.");
            Check(vm.Devices.Count == 1 && vm.CanFitTest && vm.CanSetupHearing, "All demo actions are available without a real backend.");
            var hero = page.FindControl<ProductArtwork>("HeroArt")!;
            Check(vm.ProductId == "06C010" && DemoDeviceData.Color == 3 && hero.Visual is EncoFree4DynaudioModelVisual
                && page.FindControl<Grid>("HeroPanel")!.Children.Count == 1,
                "Demo startup selects the Dynaudio model without duplicate artwork captions.");
            await UntilAsync(() => vm.FeatureSections.Sum(section => section.Controls.Count) == 34
                && page.GetVisualDescendants().OfType<FeatureSectionView>().Count() == vm.FeatureSections.Count
                && page.GetVisualDescendants().OfType<FeatureSectionView>().All(section => section.Content is FASettingsExpander),
                "The demo's dynamic section containers materialize before inspecting or expanding their editors.");
            RecordControlLayout(page, vm, "collapsed");
            var anc = page.FindControl<FASettingsExpander>("AncPanel")!;
            Check(anc is { IsVisible: true, IsExpanded: true }
                && page.GetVisualDescendants().OfType<FASettingsExpander>()
                    .Where(expander => !ReferenceEquals(expander, anc)).All(expander => !expander.IsExpanded)
                && !page.FindControl<FASettingsExpander>("OperationLogPanel")!.IsExpanded,
                "ANC starts expanded while the demo's optional groups start collapsed.");
            // The following native checks inspect all editors; explicitly open them
            // after verifying the same collapsed defaults offered to real users.
            await UntilAsync(() => hero.GetVisualDescendants().OfType<EncoFree4DynaudioModelView>().Any(model => model.ModelReady
                && model.Viewport.Info.FramesRendered > 0 && model.Viewport.Info.Triangles > 0),
                "The bundled Dynaudio scene renders on the first native window load.");
            var model = hero.GetVisualDescendants().OfType<DeviceModelView>().Single();
            _state.PreferredBackend = model.Viewport.PreferredBackend.ToString();
            _state.ActiveBackend = model.Viewport.Info.ActiveBackend.ToString();
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Automatic,
                "A fresh native demo session uses the default Automatic renderer preference.");
            Check(model.LoadError is null && model.AnimationActive, "Demo model is ready and its animation is active.");
            _state.FirstRenderedTriangles = model.Viewport.Info.Triangles;
            _state.FirstRenderedSceneItems = model.Viewport.Info.SceneItems;
            Check(model is EncoFree4DynaudioModelView && _state.FirstRenderedSceneItems == 20
                && model.Viewport.Scene!.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount ?? 0) == DynaudioModelTests.ExpectedTriangles
                && _state.FirstRenderedTriangles is > 0 and <= DynaudioModelTests.ExpectedTriangles && string.IsNullOrEmpty(model.Viewport.Info.Error),
                "The first native scene retains all 20 Dynaudio meshes and 94,927 source triangles without a renderer error.");
            await DynaudioModelTests.VerifyAsync();
            Check(_state.Service.Enumerations == 1 && _state.Service.ForbiddenCalls == 0,
                "First-load Dynaudio model integrity and Opaque correction require no headset or artwork request.");
            page.ExpandAllSettings();
            try
            {
                await UntilAsync(() => page.GetVisualDescendants().OfType<FeatureControlView>().Count() == 34,
                    "Every dynamic demo control materializes after its section is explicitly expanded.");
                // Let the native expander animation finish before resizing and
                // inspecting geometry. This is intentionally separate from VM readiness.
                await Task.Delay(350);
            }
            finally { RecordControlLayout(page, vm, "expanded"); }

            foreach (bool dark in new[] { false, true })
            foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
            {
                ((App)Application.Current!).ApplyTheme(dark ? "dark" : "light");
                window.Width = size.Width; window.Height = size.Height;
                await Task.Delay(150);
                Check(window.ActualThemeVariant == (dark ? ThemeVariant.Dark : ThemeVariant.Light), "Demo follows the window theme.");
                Check(page.IsCompact == (page.Bounds.Width < 1080), "Demo follows the responsive breakpoint.");
                DeviceBarTests.CheckBottomBar(page, page);
                var actualControlCount = page.GetVisualDescendants().OfType<FeatureControlView>().Count();
                RecordControlLayout(page, vm, $"{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}");
                Check(actualControlCount == 34,
                    $"Every dynamic demo control is loaded in the native window; actual={actualControlCount}, expected=34.");
                if (_state.Output is not null)
                {
                    window.Activate();
                    await Task.Delay(100);
                    var handle = window.TryGetPlatformHandle();
                    Check(handle is { HandleDescriptor: "HWND" } && handle.Handle != IntPtr.Zero,
                        "Dynaudio model capture runs in an actual Win32 window.");
                    string name = $"demo-dynaudio-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}-3d.png";
                    string path = Path.Combine(_state.Output, name);
                    using var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var captureSize = new PixelSize((int)Math.Round(model.Viewport.Bounds.Width), (int)Math.Round(model.Viewport.Bounds.Height));
                    using var capture = await model.Viewport.CaptureAsync(new CaptureRequest { Format = CaptureFormat.Rgba8, Size = captureSize }, captureTimeout.Token);
                    var pixels = capture.Pixels.ToArray();
                    byte background = dark ? (byte)32 : (byte)243;
                    int foreground = 0;
                    for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                        if (Math.Abs(pixels[pixel] - background) > 12 || Math.Abs(pixels[pixel + 1] - background) > 12
                            || Math.Abs(pixels[pixel + 2] - background) > 12) foreground++;
                    Check(pixels.Length == captureSize.Width * captureSize.Height * 4 && foreground > 1000,
                        "Native renderer capture contains visible Dynaudio geometry rather than a blank model frame.");
                    await capture.SaveAsync(path, captureTimeout.Token);
                    _state.Screenshots.Add(new { theme = dark ? "dark" : "light", requestedWidth = size.Width, requestedHeight = size.Height,
                        width = captureSize.Width, height = captureSize.Height, foregroundPixels = foreground, file = name,
                        source = "native Ava3D model frame; complete UI layout is covered separately by headless screenshots",
                        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                        model.Viewport.Info.Triangles, model.Viewport.Info.SceneItems, model.Viewport.Info.FramesRendered,
                        preferredBackend = model.Viewport.PreferredBackend.ToString(),
                        backend = model.Viewport.Info.ActiveBackend.ToString() });
                }
            }

            page.RequestFitTest = () => { _state.CalibrationLaunches++; return Task.CompletedTask; };
            page.RequestHearingSetup = () => { _state.CalibrationLaunches++; return Task.CompletedTask; };
            page.FindControl<FASettingsExpander>("HearingPanel")!.IsExpanded = true;
            foreach (string label in new[] { "开始测试", "设置黄金听感" })
            {
                var button = page.GetVisualDescendants().OfType<Button>().Single(candidate => Equals(candidate.Content, label));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Check(_state.CalibrationLaunches == 0 && !vm.StatusWarning, "Demo test buttons give local feedback without launching calibration.");
            await vm.AddDemoDeviceAsync();
            Check(vm.Devices.Count == 1, "Adding the demo again retains a single card.");
            await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(vm.GameMode == true, "Demo controls change local state.");
            Check(_state.Service.Enumerations == 1 && _state.Service.ForbiddenCalls == 0,
                "Themes, responsive layout, repeated demo selection and local interactions never request a backend.");
            vm.EqName = "临时演示草稿";
            Check(vm.EqDirty, "The demo EQ editor accepts a local draft.");
            window.Close();
            await UntilAsync(() => _state.Closed, "A dirty demo closes without a confirmation dialog.");
            Check(!vm.IsDemoSelected && !vm.Devices.Any(MainViewModel.IsDemoDevice), "Closing removes the demo from the disposed view model.");
            await using var fresh = new MainViewModel(new UnsupportedPlatformService(), new RejectConfirmation());
            await fresh.InitializeAsync();
            Check(!fresh.HasDevices && !fresh.IsDemoSelected, "A new UI session starts with no demo.");
            _state.Completed = true;
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            if (!_state.Closed) { await vm.DisposeAsync(); window.Close(); }
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static void RecordControlLayout(MainView page, MainViewModel vm, string phase)
    {
        var sections = page.GetVisualDescendants().OfType<FeatureSectionView>().ToArray();
        var controls = page.GetVisualDescendants().OfType<FeatureControlView>().Count();
        _state.ControlLayouts.Add(new
        {
            phase, expectedSections = vm.FeatureSections.Count, actualSections = sections.Length,
            expectedControls = vm.FeatureSections.Sum(section => section.Controls.Count), actualControls = controls,
            sections = sections.Select(section => new
            {
                id = (section.DataContext as FeatureSectionViewModel)?.Id,
                expectedControls = (section.DataContext as FeatureSectionViewModel)?.Controls.Count,
                expanded = (section.Content as FASettingsExpander)?.IsExpanded,
                actualControls = section.GetVisualDescendants().OfType<FeatureControlView>().Count(),
                width = section.Bounds.Width, height = section.Bounds.Height
            }).ToArray()
        });
        Console.WriteLine($"LAYOUT {phase}: sections={sections.Length}/{vm.FeatureSections.Count}, controls={controls}/{vm.FeatureSections.Sum(section => section.Controls.Count)}");
    }

    private static async Task UntilAsync(Func<bool> ready, string message)
    {
        for (int attempt = 0; !ready() && attempt < 200; attempt++) await Task.Delay(25);
        Check(ready(), message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _state.Passed++;
        Console.WriteLine("PASS " + message);
    }

    private static async Task WatchdogAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (_state.Completed) return;
        _state.Failure ??= "Native demo watchdog expired.";
        desktop.Shutdown(1);
    }

    private sealed class RejectConfirmation : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
        public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
    }

    private sealed class DemoService : IHeadsetService, IArtworkService
    {
        public int Enumerations { get; private set; }
        public int ForbiddenCalls { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Enumerations++; return Task.FromResult<IReadOnlyList<UiDevice>>([]); }
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw Unexpected();
        public Task DisconnectAsync(CancellationToken token) => throw Unexpected();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw Unexpected();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw Unexpected();
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token) => throw Unexpected();
        public Task ReleaseArtworkAsync(string leaseId, CancellationToken token) => throw Unexpected();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private Exception Unexpected()
        { ForbiddenCalls++; return new InvalidOperationException("Native demo must not contact headset or artwork services."); }
    }
}
