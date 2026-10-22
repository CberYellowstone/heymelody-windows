using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;

/// <summary>The renderer-to-bitmap copy path, and the animation yield gate and GPU janitor rate limit that need no GPU.</summary>
internal static class FrameCopyTests
{
    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("model frames copy straight into the locked bitmap and match the expected pixels", DirectAsync);
        Run("the managed copy still presents frames when the direct path is unavailable", FallbackAsync);
        Run("an idle model does not lock its bitmap or poll the renderer", NoRequestAsync);
        Run("animation yield pulses extend, hold and notify only on transitions", YieldAsync);
        Run("settings expanders trigger a yield pulse", ExpanderAsync);
        Run("GPU cleanup requests are rate limited", () => { Gate(); return Task.CompletedTask; });
        return 6;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task<(DeviceModelView Model, Window Window, RecordingFilamentRendererFactory Factory)> OpenAsync()
    {
        var factory = new RecordingFilamentRendererFactory();
        var model = new DeviceModelView(new DeviceModelVisual(new ArtworkFile(
            new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", EncoFree4DynaudioModelView.ModelSha256),
            DeviceVisuals.GenericProduct), factory);
        var window = new Window { Content = model, Width = 280, Height = 240 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        await Until(() => model.CaptureLatestFrame(out _) is not null, "The model presents its first frame.");
        return (model, window, factory);
    }

    private static async Task DirectAsync()
    {
        var (model, window, _) = await OpenAsync();
        try
        {
            model.Motion.SetActive(false);
            var fallbacks = model.FallbackFrameCopyCount;
            foreach (var (theme, clear) in new[] { (ThemeVariant.Dark, (byte)32), (ThemeVariant.Light, (byte)243) })
            {
                var direct = model.DirectFrameCopyCount;
                window.RequestedThemeVariant = theme;
                await Until(() => model.DirectFrameCopyCount > direct, "A theme change requests a frame that is copied directly.");
                var pixels = model.CaptureLatestFrame(out var frame)!;
                int center = (frame.Height / 2 * frame.Width + frame.Width / 2) * 4;
                Check(pixels[0] == clear && pixels[1] == clear && pixels[2] == clear && pixels[3] == 255,
                    "The directly copied frame carries the new clear colour: " + pixels[0]);
                Check(pixels[center] == 130 && pixels[center + 1] == 150 && pixels[center + 2] == 180 && pixels[center + 3] == 255,
                    "The directly copied frame carries the model rectangle.");
            }
            Check(model.FallbackFrameCopyCount == fallbacks, "Frames of the bitmap's size never use the managed copy.");
        }
        finally { window.Close(); }
    }

    private static async Task FallbackAsync()
    {
        var (model, window, _) = await OpenAsync();
        try
        {
            model.Motion.SetActive(false);
            model.DirectCopyDisabled = true;
            int directBefore = model.DirectFrameCopyCount;
            var fallbacks = model.FallbackFrameCopyCount;
            window.RequestedThemeVariant = ThemeVariant.Dark;
            await Until(() => model.FallbackFrameCopyCount > fallbacks, "A frame is copied through the managed buffer.");
            var pixels = model.CaptureLatestFrame(out var frame)!;
            Check(model.DirectFrameCopyCount == directBefore, "No frame used the direct path.");
            Check(pixels[0] == 32 && pixels[3] == 255, "The fallback path still produces the right pixels.");
        }
        finally { window.Close(); }
    }

    private static async Task NoRequestAsync()
    {
        var (model, window, factory) = await OpenAsync();
        try
        {
            model.Motion.SetActive(false);
            for (var i = 0; i < 4; i++) await Tick();
            // Polling continues briefly after the last request, in case a later frame is still in flight.
            await Task.Delay(TimeSpan.FromSeconds(.4));
            for (var i = 0; i < 2; i++) await Tick();
            int locks = model.BitmapLockCount, copies = factory.Instances.Single().CopyCalls;
            for (var i = 0; i < 6; i++) await Tick();
            Check(model.BitmapLockCount == locks && factory.Instances.Single().CopyCalls == copies,
                "Without a requested frame the bitmap is not locked and the renderer is not polled.");
        }
        finally { window.Close(); }
    }

    private static async Task YieldAsync()
    {
        AnimationYield.Reset();
        int changes = 0, ended = 0;
        EventHandler onChanged = (_, _) => changes++, onEnded = (_, _) => ended++;
        AnimationYield.Changed += onChanged; AnimationYield.PulseEnded += onEnded;
        try
        {
            Check(!AnimationYield.Active, "The gate starts inactive.");
            var owner = new object();
            AnimationYield.SetHold(owner, true); AnimationYield.SetHold(owner, true);
            Check(AnimationYield.Active && changes == 1, "A hold activates the gate once.");
            AnimationYield.Pulse(TimeSpan.FromMilliseconds(60));
            Check(changes == 1, "A pulse during a hold is not a transition.");
            AnimationYield.SetHold(owner, false);
            Check(AnimationYield.Active && changes == 1, "A pending pulse keeps the gate active after the hold ends.");
            AnimationYield.Pulse(TimeSpan.FromMilliseconds(150));
            await Until(() => ended == 1, "The extended pulse ends.");
            Check(!AnimationYield.Active && changes == 2, "The gate deactivates once, after the longest pulse.");
            AnimationYield.Pulse(TimeSpan.FromMilliseconds(30));
            Check(AnimationYield.Active && changes == 3, "A new pulse reactivates the gate.");
            await Until(() => ended == 2 && !AnimationYield.Active, "The short pulse ends too.");
            Check(changes == 4, "Each activation reports exactly one deactivation.");
        }
        finally { AnimationYield.Changed -= onChanged; AnimationYield.PulseEnded -= onEnded; AnimationYield.Reset(); }
    }

    private static async Task ExpanderAsync()
    {
        AnimationYield.Reset();
        AnimationYieldTriggers.RegisterExpanders();
        var expander = new FASettingsExpander { Header = "x" };
        var window = new Window { Content = expander, Width = 280, Height = 240 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check(!AnimationYield.Active, "Showing an expander does not yield.");
            expander.IsExpanded = true;
            Check(AnimationYield.Active, "Expanding yields animations.");
            expander.IsExpanded = false;
            await Until(() => !AnimationYield.Active, "The yield ends after its pulse.");
        }
        finally { window.Close(); AnimationYield.Reset(); }
    }

    private static void Gate()
    {
        var gate = new CleanupGate(TimeSpan.FromSeconds(10));
        Check(gate.TryAcquire(1_000), "The first request is allowed.");
        Check(!gate.TryAcquire(1_001) && !gate.TryAcquire(10_999), "Requests inside the interval are dropped.");
        Check(gate.TryAcquire(11_000), "A request after the interval is allowed.");
        Check(!gate.TryAcquire(15_000), "The interval restarts at the last allowed request.");
    }

    private static async Task Tick()
    {
        await Task.Delay(40);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, string reason)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) { await Tick(); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }
        Check(condition(), reason);
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
