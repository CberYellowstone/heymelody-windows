using HeyMelodyWindows.App;

internal static class MotionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var idle = new ModelOrbitMotion();
        var idleYaw = idle.Yaw;
        idle.Step(2);
        Near(idle.Yaw - idleYaw, Math.PI / 180 * 20 * 2, 1e-10, "Idle restores the OEM twenty degrees per second", check);

        CheckDragDirections(check);

        var motion = Release();
        var initialYaw = motion.Yaw;
        var initialVelocity = motion.YawVelocity;
        check(initialVelocity < -0.1 && motion.PitchVelocity > 0.1 && motion.Phase == ModelOrbitPhase.Inertia,
            "Right-and-down release preserves negative yaw and positive pitch velocity");
        motion.Step(0.25);
        Near(motion.YawVelocity / initialVelocity, Math.Exp(-7.2 * 0.25), 1e-10,
            "Release velocity loses more than 83 percent in a quarter second", check);
        Near(motion.Yaw - initialYaw, initialVelocity / 7.2 * (1 - Math.Exp(-7.2 * 0.25)), 1e-10,
            "Inertia integrates the exponential exactly", check);
        var velocityBeforeCaptureRelease = motion.YawVelocity;
        motion.CancelDrag();
        check(motion.Phase == ModelOrbitPhase.Inertia && motion.YawVelocity == velocityBeforeCaptureRelease,
            "Capture loss after a completed release preserves its inertia");

        foreach (var duration in new[] { 0.45, 1.1, 1.9, 3.5 })
        {
            var whole = Release();
            whole.Step(duration);
            foreach (var rate in new[] { 30, 60, 120, 144, 165, 240 })
            {
                var partitioned = Release();
                var remaining = duration;
                while (remaining > 1e-12)
                {
                    var step = Math.Min(1d / rate, remaining);
                    partitioned.Step(step);
                    remaining -= step;
                }
                Near(partitioned.Yaw, whole.Yaw, 1e-9, $"Yaw is independent of {rate} Hz partition at {duration}s", check);
                Near(partitioned.Pitch, whole.Pitch, 1e-9, $"Pitch is independent of {rate} Hz partition at {duration}s", check);
                Near(partitioned.YawVelocity, whole.YawVelocity, 1e-9, $"Velocity is independent of {rate} Hz partition at {duration}s", check);
                check(partitioned.Phase == whole.Phase, $"Phase is independent of {rate} Hz partition at {duration}s");
            }
            // Display frames arrive unevenly; a jittered 120-165 Hz cadence must still integrate to the same pose.
            var jittered = Release();
            double[] pattern = [1d / 120, 1d / 165, .0071, 1d / 144, .0093];
            var left = duration;
            for (var index = 0; left > 1e-12; index++)
            {
                var step = Math.Min(pattern[index % pattern.Length], left);
                jittered.Step(step);
                left -= step;
            }
            Near(jittered.Yaw, whole.Yaw, 1e-9, $"Yaw is independent of uneven display frames at {duration}s", check);
            Near(jittered.Pitch, whole.Pitch, 1e-9, $"Pitch is independent of uneven display frames at {duration}s", check);
        }

        var stopped = Release();
        stopped.SetActive(false);
        var pausedYaw = stopped.Yaw;
        var pausedPitch = stopped.Pitch;
        stopped.Step(120);
        check(!stopped.IsActive && !stopped.IsDragging && stopped.Phase == ModelOrbitPhase.Paused,
            "Hidden preview enters paused state and cancels dragging");
        Near(stopped.Yaw, pausedYaw, 0, "Hidden preview never accumulates yaw", check);
        Near(stopped.Pitch, pausedPitch, 0, "Hidden preview never accumulates pitch", check);
        Near(stopped.YawVelocity, 0, 0, "Hidden preview releases inertia", check);
        stopped.SetActive(true);
        stopped.Step(0.1);
        check(stopped.IsActive && Math.Abs(stopped.Yaw - pausedYaw) < 0.02,
            "Resume does not integrate the hidden elapsed time");

        var waiting = new ModelOrbitMotion();
        waiting.BeginDrag(0, 0, 0);
        waiting.CancelDrag();
        waiting.Step(0.5);
        Near(waiting.Yaw, 0, 0, "Cancelled drag waits before returning to idle rotation", check);
        waiting.Step(0.2);
        Near(waiting.Yaw, 0.05 * Math.PI / 180 * 20, 1e-10,
            "Idle delay splits a frame at its exact expiry", check);
        var stale = new ModelOrbitMotion();
        stale.BeginDrag(0, 0, 0);
        stale.MoveDrag(40, 0, 0.1);
        stale.EndDrag(0.21);
        check(stale.YawVelocity == 0 && stale.Phase == ModelOrbitPhase.Idle,
            "Release after a stale movement sample has no artificial impulse");

        var bounded = new ModelOrbitMotion();
        bounded.BeginDrag(0, 0, 0);
        bounded.MoveDrag(2, 110, 0.1);
        bounded.EndDrag(0.1);
        bounded.Step(1.5);
        Near(bounded.Pitch, 85 * Math.PI / 180, 1e-10,
            "Pitch inertia stops exactly at the vertical limit", check);
        foreach (var rate in new[] { 30, 60, 120, 144, 165, 240 })
        {
            var partitioned = new ModelOrbitMotion();
            partitioned.BeginDrag(0, 0, 0);
            partitioned.MoveDrag(2, 110, 0.1);
            partitioned.EndDrag(0.1);
            var remaining = 1.5;
            while (remaining > 1e-12)
            {
                var dt = Math.Min(1d / rate, remaining);
                partitioned.Step(dt);
                remaining -= dt;
            }
            Near(partitioned.Yaw, bounded.Yaw, 1e-9, $"Pitch boundary preserves yaw integration at {rate} Hz", check);
            Near(partitioned.Pitch, bounded.Pitch, 1e-9, $"Pitch boundary is independent of {rate} Hz frames", check);
            check(partitioned.Phase == bounded.Phase, $"Pitch boundary phase is independent of {rate} Hz frames");
        }

        var lifecycle = new ModelOrbitMotion();
        var beforeYaw = lifecycle.Yaw;
        lifecycle.MoveDrag(50, 50, 0.1);
        lifecycle.EndDrag(0.1);
        Near(lifecycle.Yaw, beforeYaw, 0, "Pointer movement without capture never rotates the model", check);
        lifecycle.BeginDrag(0, 0, 1);
        lifecycle.MoveDrag(40, 20, 1.1);
        check(lifecycle.IsDragging && Math.Abs(lifecycle.YawVelocity) > 0, "Captured movement records velocity");
        lifecycle.CancelDrag();
        check(!lifecycle.IsDragging && lifecycle.YawVelocity == 0 && lifecycle.PitchVelocity == 0,
            "Lost capture cancels the drag and its impulse");
        lifecycle.BeginDrag(0, 0, 2);
        lifecycle.EndDrag(2);
        check(!lifecycle.IsDragging && lifecycle.YawVelocity == 0 && lifecycle.PitchVelocity == 0,
            "Click without movement has no release impulse");
        lifecycle.BeginDrag(0, 0, 3);
        lifecycle.MoveDrag(1_000_000, 1_000_000, 3.001);
        check(Math.Abs(lifecycle.Pitch) <= 85 * Math.PI / 180 + 1e-10,
            "Extreme drag preserves the pitch limit");
        check(Math.Abs(lifecycle.YawVelocity) <= 10 + 1e-10 && Math.Abs(lifecycle.PitchVelocity) <= 10 + 1e-10,
            "Extreme drag preserves the angular speed limit");
        lifecycle.SetActive(false);
        check(!lifecycle.IsDragging && lifecycle.Phase == ModelOrbitPhase.Paused,
            "Hiding during pointer capture cancels the drag");

        foreach (var dt in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
            Reject(() => new ModelOrbitMotion().Step(dt), $"Invalid elapsed time {dt} is rejected", check);
        foreach (var coordinate in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Reject(() => new ModelOrbitMotion().BeginDrag(coordinate, 0, 0), "Non-finite pointer coordinate is rejected", check);
        Reject(() => new ModelOrbitMotion().BeginDrag(0, 0, -1), "Negative pointer timestamp is rejected", check);
        var backwards = new ModelOrbitMotion();
        backwards.BeginDrag(0, 0, 2);
        Reject(() => backwards.MoveDrag(5, 5, 1), "Backwards pointer timestamp is rejected", check);
    }

    private static ModelOrbitMotion Release()
    {
        var motion = new ModelOrbitMotion();
        motion.BeginDrag(0, 0, 0);
        motion.MoveDrag(40, 10, 0.1);
        motion.EndDrag(0.1);
        return motion;
    }

    private static void CheckDragDirections(Action<bool, string> check)
    {
        (string Name, double X, double Y, int YawSign, int PitchSign)[] directions =
        [
            ("Right", 120, 100, -1, 0), ("Left", 80, 100, 1, 0),
            ("Up", 100, 80, 0, -1), ("Down", 100, 120, 0, 1)
        ];
        foreach (var direction in directions)
        {
            var motion = new ModelOrbitMotion();
            var startYaw = motion.Yaw;
            var startPitch = motion.Pitch;
            motion.BeginDrag(100, 100, 1);
            motion.MoveDrag(direction.X, direction.Y, 1.1);
            if (direction.YawSign == 0)
                Near(motion.Yaw, startYaw, 0, $"{direction.Name} drag leaves horizontal orientation unchanged", check);
            else
                check((motion.Yaw - startYaw) * direction.YawSign > 0.1,
                    $"{direction.Name} drag rotates horizontally in the requested direction");
            if (direction.PitchSign == 0)
                Near(motion.Pitch, startPitch, 0, $"{direction.Name} drag leaves vertical orientation unchanged", check);
            else
                check((motion.Pitch - startPitch) * direction.PitchSign > 0.1,
                    $"{direction.Name} drag preserves the existing vertical direction");

            motion.EndDrag(1.1);
            check(motion.Phase == ModelOrbitPhase.Inertia, $"{direction.Name} release enters inertia");
            check(direction.YawSign == 0 ? motion.YawVelocity == 0 : motion.YawVelocity * direction.YawSign > 0.1,
                $"{direction.Name} release retains the horizontal drag direction");
            check(direction.PitchSign == 0 ? motion.PitchVelocity == 0 : motion.PitchVelocity * direction.PitchSign > 0.1,
                $"{direction.Name} release retains the vertical drag direction");
            var releasedYaw = motion.Yaw;
            var releasedPitch = motion.Pitch;
            motion.Step(0.15);
            check(direction.YawSign == 0 ? motion.Yaw == releasedYaw : (motion.Yaw - releasedYaw) * direction.YawSign > 0.001,
                $"{direction.Name} horizontal inertia continues in the same direction after release");
            check(direction.PitchSign == 0 ? motion.Pitch == releasedPitch : (motion.Pitch - releasedPitch) * direction.PitchSign > 0.001,
                $"{direction.Name} vertical inertia continues in the same direction after release");
        }
    }

    private static void Near(double actual, double expected, double tolerance, string message, Action<bool, string> check) =>
        check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: actual={actual:R}, expected={expected:R}");

    private static void Reject(Action action, string message, Action<bool, string> check)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { check(true, message); return; }
        check(false, message);
    }
}
