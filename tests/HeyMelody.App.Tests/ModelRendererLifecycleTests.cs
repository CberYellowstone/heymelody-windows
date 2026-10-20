using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Rendering.Filament;
using HeyMelody.Rendering.Tests;
using HeyMelodyWindows.App;

internal static class ModelRendererLifecycleTests
{
    internal static async Task RunAsync(string output)
    {
        var old = ModelRenderingPreferences.CompatibilityMode;
        try
        {
            ModelRenderingPreferences.Apply(false);
            int leases = 0;
            var cacheFactory = new RecordingFilamentRendererFactory();
            var cacheView = View(cacheFactory, () => { Interlocked.Increment(ref leases); return new Lease(() => Interlocked.Decrement(ref leases)); });
            var cacheWindow = new Window { Content = cacheView, Width = 280, Height = 240 };
            try
            {
                cacheWindow.Show();
                await UntilAsync(() => cacheView.CaptureLatestFrame(out _) is not null, "A visible model presents a completed fake frame.");
                var snapshot = cacheView.CaptureLatestFrame(out var firstFrame)!;
                byte firstByte = snapshot[0]; snapshot[0] ^= 255;
                var repeated = cacheView.CaptureLatestFrame(out var repeatedFrame)!;
                Check(repeated[0] == firstByte && repeatedFrame == firstFrame,
                    "Repeated snapshots copy cached pixels without consuming a new native frame or exposing mutable storage.");
                cacheWindow.Width = 190; cacheWindow.Height = 340;
                await UntilAsync(() =>
                {
                    _ = cacheView.CaptureLatestFrame(out var frame);
                    return frame.Width == (int)Math.Ceiling(cacheView.Viewport.Bounds.Width * cacheWindow.RenderScaling)
                        && frame.Height == (int)Math.Ceiling(cacheView.Viewport.Bounds.Height * cacheWindow.RenderScaling)
                        && frame.Width != firstFrame.Width;
                }, "A portrait resize presents only a frame matching the current physical dimensions.");
                Check(Math.Abs(cacheFactory.Instances.Single().Configuration.FitMargin - 1.08f) < .0001,
                    "Portrait views retain the common fit margin; native camera fitting owns the aspect ratio.");
                cacheWindow.Content = null;
                await UntilAsync(() => cacheFactory.Instances.Single().Disposed && leases == 0,
                    "Detaching releases the renderer and every loading/display artwork lease.");
                Check(cacheView.CaptureLatestFrame(out _) is null && cacheView.Viewport.Source is null,
                    "Detached views cannot expose a stale cached frame or bitmap.");
            }
            finally { cacheWindow.Close(); }

            foreach (bool compatibility in new[] { false, true })
            {
                ModelRenderingPreferences.Apply(compatibility);
                var factory = new RecordingFilamentRendererFactory { FailBackend = true };
                var failed = View(factory);
                var window = new Window { Content = failed, Width = 280, Height = 240 };
                try
                {
                    window.Show();
                    await UntilAsync(() => failed.LoadError is not null, "A backend initialization failure reaches static artwork.");
                    bool hint = failed.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("“3D模型渲染API”中选择 OpenGL") == true);
                    Check(hint == (!compatibility && !OperatingSystem.IsMacOS()),
                        "Only an unavailable Vulkan backend recommends manually enabling compatibility.");
                    await TickAsync();
                    Check(ModelRenderingPreferences.CompatibilityMode == compatibility && factory.CreateCalls == 1,
                        "Backend failure does not change the preference or automatically switch backend.");
                    if (!compatibility && !OperatingSystem.IsMacOS())
                    {
                        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing Vulkan error region.");
                        using var stream = new FileStream(Path.Combine(output, "model-vulkan-unavailable.png"), FileMode.CreateNew);
                        frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    }
                }
                finally { window.Close(); }
            }
            ModelRenderingPreferences.Apply(false);
            foreach (string failure in new[] { "create", "load", "native-model", "frame", "backend-frame" })
            {
                var factory = new RecordingFilamentRendererFactory
                { FailCreate = failure == "create", FailLoad = failure == "load", FailFrame = failure == "frame",
                    FailBackendFrame = failure == "backend-frame", FailNativeModel = failure == "native-model" };
                var model = View(factory);
                var window = new Window { Content = model, Width = 280, Height = 240 };
                try
                {
                    window.Show();
                    try { await UntilAsync(() => model.LoadError is not null, "Renderer " + failure + " failure is reported."); }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException($"{failure}: ready={model.ModelReady}, active={model.AnimationActive}, create={factory.CreateCalls}, "
                            + $"instances={factory.Instances.Count}, bounds={model.Viewport.Bounds}, frames={factory.Instances.LastOrDefault()?.FrameRequests}, "
                            + $"error={model.LoadError}", exception);
                    }
                    await UntilAsync(() => factory.Instances.All(instance => instance.Disposed), "Failed native resources are disposed.");
                    Check(!model.ModelReady && !model.AnimationActive && model.CaptureLatestFrame(out _) is null,
                        "Failed rendering stops the frame loop and clears the image rather than keeping stale pixels.");
                    Check(model.GetVisualDescendants().OfType<FASymbolIcon>().Any(), "Renderer failure reaches static artwork without recursing into GLB.");
                    Check(model.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("“3D模型渲染API”中选择 OpenGL") == true)
                        == (failure == "backend-frame" && !OperatingSystem.IsMacOS()),
                        "Vulkan rendering failures suggest compatibility, while component, model and unclassified errors do not.");
                    if (failure == "native-model")
                        Check(model.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "三维模型资源加载失败"),
                            "Native model parsing failure is identified as a resource load failure.");
                }
                finally { window.Close(); }
            }

            using var barrier = new ManualResetEventSlim();
            var delayedFactory = new RecordingFilamentRendererFactory { CreateBarrier = barrier, ObserveCancellation = false };
            var delayed = View(delayedFactory);
            var delayedWindow = new Window { Content = delayed, Width = 280, Height = 240 };
            try
            {
                delayedWindow.Show();
                await UntilAsync(() => delayedFactory.CreateCalls == 1, "A background model load reaches the factory.");
                delayedWindow.Content = null;
                Dispatcher.UIThread.RunJobs();
                barrier.Set();
                await UntilAsync(() => delayedFactory.Instances.Count == 1 && delayedFactory.Instances[0].Disposed,
                    "A renderer whose creation completes after detach is disposed instead of attached.");
                Check(!delayed.ModelReady && !delayed.AnimationActive && delayed.CaptureLatestFrame(out _) is null,
                    "Stale model completion cannot revive a detached viewport or present its pixels.");
            }
            finally { barrier.Set(); delayedWindow.Close(); }

            if (!OperatingSystem.IsMacOS())
            {
                using var cancellationBarrier = new ManualResetEventSlim();
                var cancellableFactory = new RecordingFilamentRendererFactory { CreateBarrier = cancellationBarrier };
                var switching = View(cancellableFactory);
                var switchingWindow = new Window { Content = switching, Width = 280, Height = 240 };
                try
                {
                    switchingWindow.Show();
                    await UntilAsync(() => cancellableFactory.CreateCalls == 1, "Creation reaches a cancellable factory.");
                    ModelRenderingPreferences.Apply(true);
                    await UntilAsync(() => cancellableFactory.CreateCalls == 2, "Switching cancels old creation and starts the selected backend.");
                    ModelRenderingPreferences.Apply(false);
                    await UntilAsync(() => cancellableFactory.CreateCalls == 3, "A second switch cancels the superseded backend.");
                    cancellationBarrier.Set();
                    await UntilAsync(() => switching.ModelReady, "Only the latest selected backend completes loading.");
                    Check(cancellableFactory.Instances.Count == 1
                        && switching.Diagnostics.ActualBackend == FilamentBackend.Vulkan,
                        "Cancelled loads cannot publish a stale renderer or change the current preference.");
                }
                finally { cancellationBarrier.Set(); switchingWindow.Close(); }
            }
        }
        finally { ModelRenderingPreferences.Apply(old); }
    }

    private static DeviceModelView View(RecordingFilamentRendererFactory factory, Func<IDisposable>? lease = null) => new(new DeviceModelVisual(
        new ArtworkFile(new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", GlbSource.DynaudioSha256, lease),
        DeviceVisuals.GenericProduct), factory);

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    internal static async Task UntilAsync(Func<bool> condition, string reason)
    {
        for (int attempt = 0; attempt < 300 && !condition(); attempt++) await TickAsync();
        Check(condition(), reason);
    }
    private static async Task TickAsync()
    {
        await Task.Delay(20);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
