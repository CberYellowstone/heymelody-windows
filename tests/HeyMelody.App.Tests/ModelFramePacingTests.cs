using HeyMelodyWindows.App;

/// <summary>Deterministic request pacing, independent of a GPU, wall-clock delays or a physical display.</summary>
internal static class ModelFramePacingTests
{
    internal static int Run()
    {
        var passed = 0;
        foreach (var refreshRate in new[] { 60, 120, 144, 165, 240 })
            foreach (var frameRate in new[] { ModelFrameRate.Fps30, ModelFrameRate.Fps60, ModelFrameRate.Unlimited })
                Run($"{Label(frameRate)} pacing follows {refreshRate} Hz animation ticks without exceeding its selected cap",
                    () => Cadence(refreshRate, frameRate));
        Run("unlimited permits each animation callback without starting or draining a separate loop", UnlimitedCallbacks);
        Run("changing the frame-rate target clears the previous fractional allowance", ChangeRate);
        Run("pausing and resuming starts a fresh pacing phase without a backlog", Resume);
        Run("a long animation stall drops missed frames and preserves subframe phase", LongPause);
        Run("a backwards clock resets phase instead of producing an early frame", ClockRegression);
        Run("changing animation cadence retains the selected fixed frame rate", ChangingCadence);
        Run("very long timestamp ranges cannot overflow time credit", LongTimestampRange);
        return passed;

        void Run(string name, Action action)
        {
            action();
            ++passed;
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Cadence(int refreshRate, ModelFrameRate frameRate)
    {
        const int seconds = 10;
        var pacer = new ModelFramePacer(frameRate);
        var frames = pacer.ShouldRender(TimeSpan.Zero) ? 1 : 0;
        for (var tick = 1; tick <= refreshRate * seconds; ++tick)
        {
            // Integer timestamps reproduce the 83333/83334 tick alternation of a 120 Hz clock.
            var timestamp = TimeSpan.FromTicks((long)tick * TimeSpan.TicksPerSecond / refreshRate);
            var render = pacer.ShouldRender(timestamp);
            if (render) ++frames;
            if (frameRate == ModelFrameRate.Unlimited)
                Check(render, "Unlimited must permit every animation callback, including rates above 120 Hz.");
            else
                Check(!pacer.ShouldRender(timestamp), "A capped rate cannot drain a second frame at the same timestamp.");
        }
        var expected = (frameRate == ModelFrameRate.Unlimited ? refreshRate : Math.Min(refreshRate, (int)frameRate)) * seconds + 1;
        Check(frames == expected,
            $"Expected {expected} requests including the initial frame, got {frames}; no rounded-up or catch-up rate is allowed.");
    }

    private static void UnlimitedCallbacks()
    {
        var pacer = new ModelFramePacer();
        Check(pacer.FrameRate == ModelFrameRate.Unlimited, "The default pacing mode has no application frame-rate cap.");
        foreach (var timestamp in new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromHours(1), TimeSpan.FromMinutes(1) })
            Check(pacer.ShouldRender(timestamp), "Every explicit callback receives one unlimited decision; clock cadence does not impose a cap.");
    }

    private static void ChangeRate()
    {
        var pacer = new ModelFramePacer(ModelFrameRate.Fps60);
        Check(pacer.ShouldRender(TimeSpan.Zero), "A newly active view may render its first frame immediately.");
        Check(!pacer.ShouldRender(TimeSpan.FromMilliseconds(15)), "An incomplete 60 fps interval waits.");
        pacer.Reset(ModelFrameRate.Fps30);
        Check(pacer.ShouldRender(TimeSpan.FromSeconds(20)), "Changing rate permits one new initial frame.");
        Check(!pacer.ShouldRender(TimeSpan.FromSeconds(20) + TimeSpan.FromMilliseconds(7)),
            "Credit from the faster rate cannot leak into the slower choice.");
        Check(pacer.ShouldRender(TimeSpan.FromSeconds(20) + TimeSpan.FromTicks(333333)),
            "The new 30 fps interval remains available at its quantized boundary.");
        pacer.Reset(ModelFrameRate.Unlimited);
        Check(pacer.ShouldRender(TimeSpan.FromSeconds(20)) && pacer.ShouldRender(TimeSpan.FromSeconds(20) + TimeSpan.FromTicks(1)),
            "Switching to unlimited immediately removes the previous application cap.");
        pacer.Reset(ModelFrameRate.Fps60);
        Check(pacer.ShouldRender(TimeSpan.FromSeconds(30)), "Switching back to a capped rate permits one initial frame.");
        Check(!pacer.ShouldRender(TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(1)),
            "Unlimited mode cannot leak allowance into a capped rate.");
        Check(pacer.ShouldRender(TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(166666)),
            "The new 60 fps phase starts without an unlimited backlog.");
        pacer.Reset((ModelFrameRate)240);
        Check(pacer.FrameRate == ModelFrameRate.Unlimited, "An unknown target is normalized to unlimited.");
    }

    private static void Resume()
    {
        var pacer = new ModelFramePacer(ModelFrameRate.Fps60);
        Check(pacer.ShouldRender(TimeSpan.Zero), "The initial active frame is allowed.");
        Check(!pacer.ShouldRender(TimeSpan.FromMilliseconds(15)), "The current interval is incomplete before pausing.");
        pacer.Reset(ModelFrameRate.Fps60);
        var resumed = TimeSpan.FromHours(1);
        Check(pacer.ShouldRender(resumed), "Resuming allows exactly one immediate frame.");
        Check(!pacer.ShouldRender(resumed) && !pacer.ShouldRender(resumed + TimeSpan.FromMilliseconds(1)),
            "Neither the hidden duration nor the old fractional allowance produces catch-up requests.");
        Check(pacer.ShouldRender(resumed + TimeSpan.FromTicks(166666)), "The resumed view uses a fresh 60 fps interval.");
    }

    private static void LongPause()
    {
        var pacer = new ModelFramePacer(ModelFrameRate.Fps60);
        Check(pacer.ShouldRender(TimeSpan.Zero), "The first frame is allowed.");
        Check(!pacer.ShouldRender(TimeSpan.FromMilliseconds(5)), "A subframe elapsed duration is retained.");
        var resumed = TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(5);
        Check(pacer.ShouldRender(resumed), "A long stall produces a single request.");
        Check(!pacer.ShouldRender(resumed), "Missed frames cannot be drained at the same timestamp.");
        Check(!pacer.ShouldRender(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(15)),
            "Whole missed intervals are discarded instead of becoming a backlog.");
        Check(pacer.ShouldRender(TimeSpan.FromSeconds(10) + TimeSpan.FromTicks(166666)),
            "Only the original subframe phase remains after the stall.");
    }

    private static void ClockRegression()
    {
        var pacer = new ModelFramePacer(ModelFrameRate.Fps60);
        Check(pacer.ShouldRender(TimeSpan.FromSeconds(1)), "The fixture begins at a nonzero timestamp.");
        Check(!pacer.ShouldRender(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(15)), "The old phase is incomplete.");
        Check(!pacer.ShouldRender(TimeSpan.FromMilliseconds(500)), "A backwards timestamp cannot render.");
        Check(!pacer.ShouldRender(TimeSpan.FromMilliseconds(501)), "Old phase is cleared on clock regression.");
        Check(pacer.ShouldRender(TimeSpan.FromMilliseconds(500) + TimeSpan.FromTicks(166666)),
            "Normal cadence resumes from the new clock origin.");
    }

    private static void ChangingCadence()
    {
        foreach (var frameRate in new[] { ModelFrameRate.Fps30, ModelFrameRate.Fps60, ModelFrameRate.Unlimited })
        {
            var pacer = new ModelFramePacer(frameRate);
            var timestamp = TimeSpan.Zero;
            var requests = pacer.ShouldRender(timestamp) ? 1 : 0;
            var expected = 1;
            foreach (var refreshRate in new[] { 144, 60, 165, 120, 240 })
            {
                var origin = timestamp;
                for (var tick = 1; tick <= refreshRate * 2; ++tick)
                {
                    timestamp = origin + TimeSpan.FromTicks((long)tick * TimeSpan.TicksPerSecond / refreshRate);
                    if (pacer.ShouldRender(timestamp)) ++requests;
                }
                expected += (frameRate == ModelFrameRate.Unlimited ? refreshRate : Math.Min(refreshRate, (int)frameRate)) * 2;
                Check(requests == expected, "Changing display cadence preserves the selected cap or unlimited callback behavior.");
            }
        }
    }

    private static void LongTimestampRange()
    {
        var pacer = new ModelFramePacer(ModelFrameRate.Fps60);
        Check(pacer.ShouldRender(TimeSpan.MinValue), "The pacing clock may use any TimeSpan origin.");
        Check(pacer.ShouldRender(TimeSpan.MaxValue), "Even an extreme elapsed range yields just one request.");
        Check(!pacer.ShouldRender(TimeSpan.MaxValue), "An extreme elapsed range cannot leave whole frames queued.");
    }

    private static string Label(ModelFrameRate frameRate) => frameRate == ModelFrameRate.Unlimited ? "unlimited" : $"{(int)frameRate} fps";

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
