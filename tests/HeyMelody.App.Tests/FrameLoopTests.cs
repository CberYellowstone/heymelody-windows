using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HeyMelodyWindows.App;

/// <summary>The vsync-aligned 3D rotation loop and its frame-rate preference.</summary>
internal static class FrameLoopTests
{
    internal static int Run(HeadlessUnitTestSession session, string output)
    {
        Run("model frame intervals follow the display, the preference and the CPU renderer's limits", () => { Intervals(); return Task.CompletedTask; });
        Run("the model frame-rate preference persists beside the theme and GPU choices", () => { Preference(output); return Task.CompletedTask; });
        Run("the model loop advances on display frames only and stops while its window is hidden", LoopAsync);
        return 3;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Intervals()
    {
        Check(DeviceModelView.FrameInterval(ModelFrameRate.Display, false, false, 0) == 0
            && DeviceModelView.FrameInterval(ModelFrameRate.Display, false, true, 50) == 0,
            "GPU rotation draws on every display frame, idle or dragged, with no 30/60 cap.");
        Check(Near(DeviceModelView.FrameInterval(ModelFrameRate.Fps60, false, true, 0), 1d / 60)
            && Near(DeviceModelView.FrameInterval(ModelFrameRate.Fps30, false, false, 0), 1d / 30),
            "The 60 and 30 fps preferences become even frame intervals.");
        Check(Near(DeviceModelView.FrameInterval(ModelFrameRate.Display, true, false, 2), 1d / 30)
            && Near(DeviceModelView.FrameInterval(ModelFrameRate.Display, true, true, 5), 1d / 60)
            && Near(DeviceModelView.FrameInterval(ModelFrameRate.Display, true, true, 30), .039)
            && Near(DeviceModelView.FrameInterval(ModelFrameRate.Fps30, true, true, 5), 1d / 30),
            "The CPU renderer keeps 30 fps idle and at most 60 fps while dragged, slowing further for slow frames.");
    }

    private static void Preference(string output)
    {
        var old = ThemePreferences.SettingsPathOverride;
        ThemePreferences.SettingsPathOverride = Path.Combine(output, "frame-rate-" + Guid.NewGuid().ToString("N"), "preferences.json");
        try
        {
            Check(ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Display, "A missing preference follows the display.");
            Check(ThemePreferences.Save("dark", false, ModelFrameRate.Fps30) && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Fps30
                && !ThemePreferences.LoadModelGpuAcceleration(), "The frame rate is saved with the GPU choice.");
            Check(ThemePreferences.Save("light") && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Fps30 && ThemePreferences.Load() == "light",
                "Saving only the theme keeps the frame-rate choice.");
            File.WriteAllText(ThemePreferences.SettingsPathOverride, "{\"modelFrameRate\":\"144\"}");
            Check(ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Display, "Unknown values fall back to the display rate.");
        }
        finally { ThemePreferences.SettingsPathOverride = old; }
    }

    private static async Task LoopAsync()
    {
        var model = new EncoFree4DynaudioModelView();
        var window = new Window { Content = model, Width = 280, Height = 240 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await Until(() => model.AnimationActive, "The loop starts once the model is loaded in a visible window.");
            var before = model.CameraUpdateCount;
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Check(model.CameraUpdateCount == before, "Without display frames the loop draws nothing.");
            // Idle rotation starts 0.65 s after activation; tick through that pause first.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(ModelOrbitMotion.IdleDelaySeconds + .2)) await Tick();
            before = model.CameraUpdateCount;
            for (var frame = 0; frame < 6; frame++) await Tick();
            Check(model.CameraUpdateCount >= before + 4, "Spaced display frames advance the idle rotation; got "
                + (model.CameraUpdateCount - before) + " updates for 6 frames.");
            window.Hide(); Dispatcher.UIThread.RunJobs();
            Check(!model.AnimationActive && !model.Motion.IsActive, "Hiding the window stops the loop.");
            var hidden = model.CameraUpdateCount;
            for (var frame = 0; frame < 3; frame++) await Tick();
            Check(model.CameraUpdateCount == hidden, "A hidden window receives no camera updates.");
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check(model.AnimationActive, "Showing the window resumes the loop.");
        }
        finally { window.Close(); }
    }

    private static async Task Tick()
    {
        // Headless Automatic may use software's 30 fps idle cap. Supply display
        // frames beyond that interval; this checks frame-driven motion and lifetime,
        // while Intervals checks the GPU/CPU refresh-rate policy independently.
        await Task.Delay(40);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, string reason)
    {
        for (var attempt = 0; attempt < 600 && !condition(); attempt++) { await Task.Delay(25); Dispatcher.UIThread.RunJobs(); }
        Check(condition(), reason);
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
