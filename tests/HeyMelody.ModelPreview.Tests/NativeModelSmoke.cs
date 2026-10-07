using System.Security.Cryptography;
using System.Text.Json;
using Ava3D;
using Ava3D.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

internal static class NativeModelSmoke
{
    private static SmokeState _state = null!;

    public static object Run(string output, Action<bool, string> check, bool occlusionOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native model capture requires Windows.");
        _state = new(output, check, occlusionOnly);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().LogToTrace()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error; }
        }) { IsBackground = true, Name = "Enco Free4 real compositor model verification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new TimeoutException("Native model verification did not close within 55 seconds.");
        File.WriteAllText(Path.Combine(output, "native-receipts.json"), JsonSerializer.Serialize(new
        {
            success = _state.Failure is null, _state.FirstFrames, _state.ReadyScenes,
            states = _state.Receipts, failure = _state.Failure?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (_state.Failure is not null) throw new InvalidOperationException("Native model verification failed.", _state.Failure);
        check(_state.Closed, "Native verification window closes normally");
        return new { _state.FirstFrames, _state.ReadyScenes, states = _state.Receipts };
    }

    private sealed class SmokeState(string output, Action<bool, string> check, bool occlusionOnly)
    {
        public string Output { get; } = output;
        public Action<bool, string> Check { get; } = check;
        public bool OcclusionOnly { get; } = occlusionOnly;
        public int FirstFrames;
        public int ReadyScenes;
        public bool Closed;
        public Exception? Failure;
        public object? ModelBounds;
        public List<object> Receipts { get; } = [];
    }

    // Populate the App's actual theme; skip its normal backend/lifetime initialization entirely.
    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            RequestedThemeVariant = ThemeVariant.Light;
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime required.");
            var model = new EncoFree4ModelView();
            model.Viewport.Trace = new FrameTrace(4096);
            model.Viewport.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
                _state.Receipts.Add(new { state = "pointer-release-tunnel", phase = model.Motion.Phase.ToString(), model.Motion.YawVelocity }),
                RoutingStrategies.Tunnel, handledEventsToo: true);
            model.Viewport.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
                _state.Receipts.Add(new { state = "pointer-release-bubble", phase = model.Motion.Phase.ToString(), model.Motion.YawVelocity }),
                RoutingStrategies.Bubble, handledEventsToo: true);
            model.Viewport.PointerCaptureLost += (_, _) =>
                _state.Receipts.Add(new { state = "pointer-capture-lost", phase = model.Motion.Phase.ToString(), model.Motion.YawVelocity });
            model.Viewport.FirstFrameRendered += (_, _) => _state.FirstFrames++;
            model.Viewport.SceneReady += (_, _) => _state.ReadyScenes++;
            var window = new Window
            {
                Title = "Enco Free4 — isolated 3D verification",
                Width = 1000, Height = 700, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(243, 243, 243)),
                Content = new Border { Padding = new Thickness(16), Child = model }
            };
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            var verificationStarted = false;
            window.Opened += (_, _) =>
            {
                // Avalonia can raise Opened again after Hide/Show. The lifecycle
                // checks deliberately do that; run exactly one verification task.
                if (verificationStarted) return;
                verificationStarted = true;
                _ = VerifyAndCloseAsync(window, model, desktop);
            };
            _ = WatchdogAsync(window, desktop);
        }
    }

    private static async Task VerifyAndCloseAsync(Window window, EncoFree4ModelView model,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await WaitAsync(() => model.LoadError is not null || (model.ModelReady && model.Viewport.Info.Triangles > 0
                && model.Viewport.Info.FramesRendered > 0), TimeSpan.FromSeconds(30), "Model scene did not render.");
            _state.Check(model.LoadError is null, "Model loading completes without an error");
            var handle = window.TryGetPlatformHandle();
            _state.Check(handle is not null && handle.HandleDescriptor == "HWND" && handle.Handle != IntPtr.Zero,
                "Model preview runs in a real Win32 window");
            var hwnd = handle!.Handle;
            _state.Check(model.Viewport.Info.Triangles > 0 && model.Viewport.Info.SceneItems > 0,
                "Renderer submits the loaded model geometry");
            _state.Check(model.Viewport.Info.Triangles == 93_875 && model.Viewport.Info.SceneItems == 28,
                "Native renderer receives all 28 expected mesh items and 93,875 triangles");
            _state.Check(string.IsNullOrEmpty(model.Viewport.Info.Error), "Renderer reports no error");

            using (var asset = AssetLoader.Open(new Uri("avares://HeyMelody/Assets/Devices/EncoFree4/model.glb")))
            {
                var hash = Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant();
                _state.Check(hash == "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc",
                    "Embedded model equals the independently verified cloud GLB SHA-256");
            }
            var firstModel = await EncoFree4ModelView.LoadModelAsync();
            var secondModel = await EncoFree4ModelView.LoadModelAsync();
            _state.Check(firstModel.Scene.EnumerateMeshes().Count() == 28, "Existing Ava3D loader produces all 28 expected mesh items");
            _state.ModelBounds = DescribeBounds(firstModel.Root.WorldBounds);
            _state.Check(!ReferenceEquals(firstModel.Scene, secondModel.Scene) && !ReferenceEquals(firstModel.Root, secondModel.Root),
                "Repeated loads create independent mutable scene instances");

            if (_state.OcclusionOnly)
            {
                await NativeModelOcclusionChecks.RunAsync(window, model, _state.Output, _state.Check, _state.Receipts);
                return;
            }

            var before = await CaptureAsync(window, model, hwnd, "light-large-before");
            var initialYaw = model.Motion.Yaw;
            var initialPitch = model.Motion.Pitch;
            var origin = model.Viewport.PointToScreen(new Point(model.Viewport.Bounds.Width / 2, model.Viewport.Bounds.Height / 2));
            var client = NativeWindowCapture.ClientPoint(hwnd, origin);
            NativeWindowCapture.PointerMessage(hwnd, 0x0201, client.X, client.Y, true);
            await Task.Delay(40);
            _state.Check(model.Motion.IsDragging, "Native pointer press starts captured model drag");
            for (var step = 1; step <= 6; step++)
            {
                NativeWindowCapture.PointerMessage(hwnd, 0x0200, client.X + step * 10, client.Y + step * 2, true);
                await Task.Delay(22);
            }
            _state.Check(model.Motion.Yaw - initialYaw < -0.15, "Native right drag decreases yaw in the corrected horizontal direction");
            _state.Check(model.Motion.Pitch - initialPitch > 0.05, "Native downward drag preserves the positive pitch direction");
            var dragged = await CaptureAsync(window, model, hwnd, "light-large-dragged");
            var changed = DifferentPixels(before, dragged);
            _state.Check(changed > 500, "Native drag changes actual rendered 3D pixels");
            _state.Receipts.Add(new { state = "drag-pixel-difference", changedPixels = changed, totalPixels = before.Length / 4 });
            // Image readback may exceed the valid release-sample interval. Renew movement immediately before release.
            NativeWindowCapture.PointerMessage(hwnd, 0x0200, client.X + 65, client.Y + 13, true);
            await Task.Delay(20);
            NativeWindowCapture.PointerMessage(hwnd, 0x0200, client.X + 75, client.Y + 15, true);
            await Task.Delay(20);
            _state.Receipts.Add(new { state = "release-before", phase = model.Motion.Phase.ToString(), model.Motion.YawVelocity });
            NativeWindowCapture.PointerMessage(hwnd, 0x0202, client.X + 75, client.Y + 15, false);
            await Task.Delay(45);
            _state.Check(!model.Motion.IsDragging, "Native pointer release ends capture");
            var releasedVelocity = Math.Abs(model.Motion.YawVelocity);
            _state.Receipts.Add(new { state = "release-after", phase = model.Motion.Phase.ToString(), model.Motion.YawVelocity });
            _state.Check(releasedVelocity > 0.05, "Native release retains drag inertia");
            _state.Check(model.Motion.YawVelocity < -0.05 && model.Motion.PitchVelocity > 0.01,
                "Native right-and-down release retains negative yaw and positive pitch inertia");
            var releasedYaw = model.Motion.Yaw;
            var releasedPitch = model.Motion.Pitch;
            await Task.Delay(350);
            var dampedVelocity = Math.Abs(model.Motion.YawVelocity);
            _state.Check(dampedVelocity < releasedVelocity * 0.35, "Native release inertia damps strongly within 350ms");
            _state.Check(model.Motion.Yaw - releasedYaw < -0.001,
                "Native horizontal inertia continues in the right-drag direction after release");
            _state.Check(model.Motion.Pitch - releasedPitch > 0.001,
                "Native vertical inertia continues in the downward-drag direction after release");
            await CaptureAsync(window, model, hwnd, "light-large-damped");

            // Also hide during capture, covering lost-pointer state and lifecycle together.
            NativeWindowCapture.PointerMessage(hwnd, 0x0201, client.X, client.Y, true);
            await Task.Delay(30);
            _state.Check(model.Motion.IsDragging, "Second native press reacquires the drag");
            model.IsVisible = false;
            await Task.Delay(120);
            _state.Check(!model.AnimationActive && !model.Motion.IsActive && !model.Motion.IsDragging,
                "Hidden preview stops its animation and releases dragging");
            var hiddenYaw = model.Motion.Yaw;
            var hiddenCount = model.CameraUpdateCount;
            var hiddenFrames = model.Viewport.Info.FramesRendered;
            var hiddenRenderedFrames = model.Viewport.Trace!.Frames.Count(frame => !frame.Reused);
            await Task.Delay(350);
            _state.Check(model.Motion.Yaw == hiddenYaw && model.CameraUpdateCount == hiddenCount,
                "Hidden preview causes no camera updates or accumulated rotation");
            var afterHiddenRenderedFrames = model.Viewport.Trace.Frames.Count(frame => !frame.Reused);
            _state.Check(afterHiddenRenderedFrames <= hiddenRenderedFrames + 1,
                "Hidden preview stops submitting continuous 3D frames after settling");
            _state.Receipts.Add(new { state = "hidden", hiddenCount, hiddenFrames, afterFrames = model.Viewport.Info.FramesRendered,
                hiddenRenderedFrames, afterHiddenRenderedFrames });
            NativeWindowCapture.PointerMessage(hwnd, 0x0202, client.X, client.Y, false);
            model.IsVisible = true;
            await WaitAsync(() => model.AnimationActive && model.CameraUpdateCount > hiddenCount,
                TimeSpan.FromSeconds(4), "Shown model did not resume.");
            _state.Check(Math.Abs(model.Motion.Yaw - hiddenYaw) < 0.05, "Shown preview resumes without a hidden-time jump");
            await WaitAsync(() => model.Motion.Phase == ModelOrbitPhase.Idle, TimeSpan.FromSeconds(3), "Motion did not return to idle.");
            await Task.Delay(750);
            var idleYaw = model.Motion.Yaw;
            var idlePitch = model.Motion.Pitch;
            var idleClock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(500);
            var elapsedIdle = idleClock.Elapsed.TotalSeconds;
            var expectedIdle = elapsedIdle * Math.PI / 180 * 20;
            _state.Check(Math.Abs((model.Motion.Yaw - idleYaw) - expectedIdle) <= expectedIdle * 0.2,
                "Native idle restores the OEM twenty degrees per second within timer tolerance");
            _state.Check(model.Motion.Pitch == idlePitch, "Native idle rotation preserves pitch");
            _state.Receipts.Add(new { state = "idle", elapsedIdle, yawChange = model.Motion.Yaw - idleYaw, expectedIdle });

            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
            window.Width = 720; window.Height = 540;
            await Task.Delay(300);
            await CaptureAsync(window, model, hwnd, "dark-compact");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            window.Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
            await Task.Delay(180);
            await CaptureAsync(window, model, hwnd, "light-compact");
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            window.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
            window.Width = 1000; window.Height = 700;
            await Task.Delay(180);
            await CaptureAsync(window, model, hwnd, "dark-large");
            window.Hide();
            await Task.Delay(100);
            _state.Check(!model.AnimationActive && !model.Motion.IsActive, "Hiding the native window stops model animation");
            var hiddenWindowUpdates = model.CameraUpdateCount;
            await Task.Delay(200);
            _state.Check(model.CameraUpdateCount == hiddenWindowUpdates, "Hidden native window makes no camera updates");
            window.Show();
            await WaitAsync(() => model.AnimationActive, TimeSpan.FromSeconds(3), "Native model did not resume after window show.");
            _state.Check(model.Motion.IsActive, "Showing the native window resumes model animation");
            window.Hide();
            await CaptureMainUiAsync();
            window.Show();
            await NativeModelOcclusionChecks.RunAsync(window, model, _state.Output, _state.Check, _state.Receipts);
            window.Close();
            _state.Check(!model.AnimationActive && !model.Motion.IsActive, "Closed preview leaves no animation timer active");
        }
        catch (Exception error) { _state.Failure = error; }
        finally { window.Close(); desktop.Shutdown(); }
    }

    private static async Task CaptureMainUiAsync()
    {
        var main = new MainWindow(new PreviewService(), preview: true)
        {
            Title = "HeyMelody · Enco Free4 三维模型验证", Topmost = true,
            Width = 1280, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var page = main.FindControl<MainView>("Page") ?? throw new InvalidOperationException("MainView missing.");
        page.SetModelDemo();
        try
        {
            main.Show(); main.Activate();
            await WaitAsync(() => page.DataContext is MainViewModel { HasVerifiedSnapshot: true }
                && main.GetVisualDescendants().OfType<EncoFree4ModelView>().Any(),
                TimeSpan.FromSeconds(8), "Main demo UI did not initialize with the preview service.");
            var model = main.GetVisualDescendants().OfType<EncoFree4ModelView>().Single();
            model.Viewport.Trace = new FrameTrace(4096);
            await WaitAsync(() => model.ModelReady && model.Viewport.Info.Triangles > 0,
                TimeSpan.FromSeconds(20), "Main demo hero did not render.");
            _state.Check(page.DataContext is MainViewModel { HasVerifiedSnapshot: true },
                "Actual MainWindow receives the in-memory verified preview snapshot");
            var hwnd = main.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("Main UI HWND missing.");
            double? largeCameraDistance = null;
            foreach (var (width, height, dark) in new[] { (1280, 900, false), (1280, 900, true), (900, 640, false), (900, 640, true) })
            {
                Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                main.Width = width; main.Height = height;
                await Task.Delay(220);
                _state.Check(page.IsCompact == (width < 1080), "Actual MainWindow preserves the responsive layout breakpoint");
                _state.Check(model.Bounds.Width > 100 && model.Bounds.Height > 100,
                    "Actual main hero has a nonempty 3D viewport");
                if (width == 1280) largeCameraDistance = model.Viewport.Camera.Distance;
                else _state.Check(largeCameraDistance is { } distance
                    && Math.Abs(model.Viewport.Camera.Distance - distance) < distance * 0.02,
                    "Wide compact viewport preserves the fitted camera distance");
                await CaptureAsync(main, model, hwnd, $"main-{(dark ? "dark" : "light")}-{width}x{height}");
            }
            main.WindowState = WindowState.Minimized;
            await Task.Delay(100);
            _state.Check(!model.AnimationActive && !model.Motion.IsActive,
                "Minimized actual MainWindow stops the model animation");
            var count = model.CameraUpdateCount;
            await Task.Delay(180);
            _state.Check(model.CameraUpdateCount == count, "Minimized actual MainWindow makes no camera updates");
            main.WindowState = WindowState.Normal;
            await WaitAsync(() => model.AnimationActive, TimeSpan.FromSeconds(3), "Main model did not resume after restore.");
            main.Close();
            await WaitAsync(() => !main.IsVisible, TimeSpan.FromSeconds(3), "Main demo did not close.");
            _state.Check(!model.AnimationActive && !model.Motion.IsActive,
                "Closing the actual MainWindow releases the model animation");
        }
        finally { main.Close(); }
    }

    private static async Task<byte[]> CaptureAsync(Window window, EncoFree4ModelView model, IntPtr hwnd, string name)
    {
        window.Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var frame = await model.Viewport.CaptureAsync(new CaptureRequest { Format = CaptureFormat.Rgba8 }, timeout.Token);
        var pixels = frame.Pixels.ToArray();
        _state.Check(pixels.Length == frame.Size.Width * frame.Size.Height * 4 && pixels.Length > 10000,
            $"{name}: renderer frame is complete RGBA data");
        var unique = CountColors(pixels);
        _state.Check(unique > 32, $"{name}: 3D frame contains visible shaded geometry");
        var foreground = ForegroundBounds(pixels, frame.Size.Width, frame.Size.Height);
        _state.Check(Math.Max(foreground.Width, foreground.Height) >= Math.Min(frame.Size.Width, frame.Size.Height) * 0.28,
            $"{name}: model occupies a useful portion of its viewport");
        _state.Check(foreground.Left > 0 && foreground.Top > 0
            && foreground.Left + foreground.Width < frame.Size.Width && foreground.Top + foreground.Height < frame.Size.Height,
            $"{name}: rendered model stays inside the viewport edges");
        await frame.SaveAsync(Path.Combine(_state.Output, name + "-3d.png"), timeout.Token);
        var native = NativeWindowCapture.Save(hwnd, Path.Combine(_state.Output, name + "-window.png"));
        _state.Receipts.Add(new
        {
            state = name, frameId = frame.FrameId, width = frame.Size.Width, height = frame.Size.Height,
            nativeWidth = native.Width, nativeHeight = native.Height, uniqueColors = unique,
            foreground,
            modelWidth = model.Bounds.Width, modelHeight = model.Bounds.Height,
            viewportWidth = model.Viewport.Bounds.Width, viewportHeight = model.Viewport.Bounds.Height,
            cameraDistance = model.Viewport.Camera.Distance,
            cameraTarget = new { model.Viewport.Camera.Target.X, model.Viewport.Camera.Target.Y, model.Viewport.Camera.Target.Z },
            cameraFieldOfView = model.Viewport.Camera.FieldOfView,
            cameraNear = model.Viewport.Camera.NearPlane, cameraFar = model.Viewport.Camera.FarPlane,
            modelRootWorldBounds = _state.ModelBounds,
            sceneWorldBounds = model.Viewport.Scene is { } scene ? DescribeBounds(scene.WorldBounds) : null,
            model.Motion.Yaw, model.Motion.Pitch, model.Motion.YawVelocity, model.Motion.PitchVelocity,
            phase = model.Motion.Phase.ToString(), model.CameraUpdateCount, model.AnimationActive,
            renderer = model.Viewport.Info.Renderer, context = model.Viewport.Info.Context,
            device = model.Viewport.Info.Device, backend = model.Viewport.Info.ActiveBackend.ToString(),
            model.Viewport.Info.Triangles, model.Viewport.Info.SceneItems, model.Viewport.Info.DrawCalls,
            model.Viewport.Info.FramesRendered, model.Viewport.Info.FramesPerSecond,
            model.Viewport.Info.FrameMilliseconds, model.Viewport.Info.RenderMilliseconds,
            model.Viewport.Info.Error
        });
        return pixels;
    }

    private sealed record Foreground(int Left, int Top, int Width, int Height, int Pixels);

    private static Foreground ForegroundBounds(byte[] pixels, int width, int height)
    {
        var left = width; var top = height; var right = -1; var bottom = -1; var count = 0;
        // The scene background is uniform. Compare against the unobstructed top-left corner.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            if (pixels[offset + 3] == 0 || Math.Abs(pixels[offset] - pixels[0]) + Math.Abs(pixels[offset + 1] - pixels[1])
                + Math.Abs(pixels[offset + 2] - pixels[2]) <= 18) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y); count++;
        }
        return count == 0 ? new(0, 0, 0, 0, 0) : new(left, top, right - left + 1, bottom - top + 1, count);
    }

    private static object DescribeBounds(BoundingBox bounds) => new
    {
        minimum = new { bounds.Min.X, bounds.Min.Y, bounds.Min.Z },
        maximum = new { bounds.Max.X, bounds.Max.Y, bounds.Max.Z },
        center = new { bounds.Center.X, bounds.Center.Y, bounds.Center.Z },
        size = new { bounds.Size.X, bounds.Size.Y, bounds.Size.Z }, bounds.Radius, bounds.IsEmpty
    };

    private static int CountColors(byte[] pixels)
    {
        var colors = new HashSet<int>();
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] != 0)
                colors.Add((pixels[offset] << 16) | (pixels[offset + 1] << 8) | pixels[offset + 2]);
            if (colors.Count >= 8192) break;
        }
        return colors.Count;
    }

    private static int DifferentPixels(byte[] before, byte[] after)
    {
        if (before.Length != after.Length) throw new InvalidOperationException("Drag comparison dimensions changed.");
        var changed = 0;
        for (var offset = 0; offset < before.Length; offset += 4)
            if (Math.Abs(before[offset] - after[offset]) + Math.Abs(before[offset + 1] - after[offset + 1])
                + Math.Abs(before[offset + 2] - after[offset + 2]) + Math.Abs(before[offset + 3] - after[offset + 3]) > 15) changed++;
        return changed;
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException(failure);
            await Task.Delay(30);
        }
    }

    private static async Task WatchdogAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(50000);
        if (_state.Closed) return;
        _state.Failure ??= new TimeoutException("Native model UI watchdog elapsed.");
        window.Close();
        desktop.Shutdown();
    }
}
