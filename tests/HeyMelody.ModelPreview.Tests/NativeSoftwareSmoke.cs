using System.Text.Json;
using Ava3D;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using HeyMelodyWindows.App;

/// <summary>Exercises the real CPU model renderer without requesting a model GPU backend.</summary>
internal static class NativeSoftwareSmoke
{
    private static State _state = null!;
    private sealed class State(string output, Action<bool, string> check)
    {
        internal string Output = output;
        internal Action<bool, string> Check = check;
        internal Exception? Failure;
        internal bool Closed;
        internal object? Receipt;
    }

    internal static object Run(string output, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native software validation requires Windows.");
        _state = new(output, check);
        var thread = new Thread(() =>
        {
            var oldPath = ThemePreferences.SettingsPathOverride;
            var oldPreference = ModelRenderingPreferences.UseGpuAcceleration;
            ThemePreferences.SettingsPathOverride = Path.Combine(output, "unwritten-preferences.json");
            ModelRenderingPreferences.Reload();
            try
            {
                AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().LogToTrace()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error; }
            finally { ThemePreferences.SettingsPathOverride = oldPath; ModelRenderingPreferences.Apply(oldPreference); }
        }) { IsBackground = true, Name = "HeyMelody CPU model validation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(50))) throw new TimeoutException("Native CPU model validation did not close within fifty seconds.");
        File.WriteAllText(Path.Combine(output, "native-software-receipts.json"), JsonSerializer.Serialize(new
        { success = _state.Failure is null, _state.Closed, _state.Receipt, failure = _state.Failure?.ToString() },
            new JsonSerializerOptions { WriteIndented = true }));
        if (_state.Failure is not null) throw new InvalidOperationException("Native CPU model validation failed.", _state.Failure);
        check(_state.Closed, "CPU renderer window closes normally");
        check(!File.Exists(Path.Combine(output, "unwritten-preferences.json")), "Native software check never writes user preferences");
        return _state.Receipt!;
    }

    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            RequestedThemeVariant = ThemeVariant.Light;
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var model = new EncoFree4DynaudioModelView();
            _state.Check(model.Viewport.PreferredBackend == RenderBackendKind.Software,
                "Default model selects software before visual-tree attachment");
            var window = new Window
            {
                Title = "HeyMelody · CPU model validation", Content = model, Width = 640, Height = 580,
                ShowActivated = false, Position = new(-10000, -10000)
            };
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            window.Opened += (_, _) => _ = VerifyAsync(window, model, desktop);
            _ = WatchdogAsync(window, desktop);
        }
    }

    private static async Task VerifyAsync(Window window, EncoFree4DynaudioModelView model,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (model.LoadError is null && (!model.ModelReady || model.Viewport.Info.FramesRendered == 0
                || model.Viewport.Info.Triangles == 0))
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("CPU scene did not render.");
                await Task.Delay(50);
            }
            _state.Check(model.LoadError is null, "Original Dynaudio GLB loads through the CPU renderer");
            _state.Check(model.Viewport.Info.ActiveBackend == RenderBackendKind.Software,
                "Native renderer diagnostics confirm software, even with GPU composition available");
            var sourceTriangles = model.Viewport.Scene!.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount ?? 0);
            _state.Check(sourceTriangles == 94_927 && model.Viewport.Info.SceneItems == 20
                && model.Viewport.Info.Triangles is > 0 and <= 94_927,
                $"CPU scene retains all 20 meshes and 94,927 source triangles; visible triangles={model.Viewport.Info.Triangles}");
            _state.Check(string.IsNullOrEmpty(model.Viewport.Info.Error), "CPU renderer reports no error");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var frame = await model.Viewport.CaptureAsync(new CaptureRequest { Format = CaptureFormat.Rgba8 }, timeout.Token);
            var pixels = frame.Pixels.ToArray();
            var colors = new HashSet<int>();
            for (var offset = 0; offset + 3 < pixels.Length; offset += 4)
                colors.Add(pixels[offset] << 24 | pixels[offset + 1] << 16 | pixels[offset + 2] << 8 | pixels[offset + 3]);
            _state.Check(pixels.Length == frame.Size.Width * frame.Size.Height * 4 && colors.Count > 32,
                "CPU frame contains complete visible model pixels");
            await frame.SaveAsync(Path.Combine(_state.Output, "dynaudio-software.png"), timeout.Token);
            var beforeYaw = model.Motion.Yaw;
            var beforeUpdates = model.CameraUpdateCount;
            var rotationWait = System.Diagnostics.Stopwatch.StartNew();
            while (model.Motion.Yaw == beforeYaw && rotationWait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50);
            _state.Check(model.AnimationActive && model.CameraUpdateCount > beforeUpdates && model.Motion.Yaw > beforeYaw,
                $"Software rotation remains active={model.AnimationActive}; updates={model.CameraUpdateCount}/{beforeUpdates}; yaw={model.Motion.Yaw}/{beforeYaw}");
            _state.Receipt = new
            {
                backend = model.Viewport.Info.ActiveBackend.ToString(), model.Viewport.Info.Renderer,
                model.Viewport.Info.Context, model.Viewport.Info.Device, model.Viewport.Info.Triangles,
                model.Viewport.Info.SceneItems, model.Viewport.Info.Error, model.Viewport.Info.FramesRendered,
                sourceTriangles,
                width = frame.Size.Width, height = frame.Size.Height, distinctColors = colors.Count,
                scope = "Real Win32 compositor and CPU Ava3D pixels, x64 host; ARM64 hardware not available"
            };
            window.Content = null;
            _state.Check(!model.AnimationActive, "Detaching a software model stops its animation");
        }
        catch (Exception error) { _state.Failure = error; }
        finally { window.Close(); desktop.Shutdown(); }
    }

    private static async Task WatchdogAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(45000);
        if (_state.Closed) return;
        _state.Failure ??= new TimeoutException("Native CPU renderer watchdog elapsed.");
        window.Close(); desktop.Shutdown();
    }
}
