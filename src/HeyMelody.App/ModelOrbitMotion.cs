using System;

namespace HeyMelodyWindows.App;

public enum ModelOrbitPhase { Idle, Dragging, Inertia, Paused }

/// <summary>Time-based orbit interaction, independent of the renderer and UI framework.</summary>
public sealed class ModelOrbitMotion
{
    public const double InitialPitch = 15 * Math.PI / 180;
    public const double IdleYawSpeed = 20 * Math.PI / 180;
    public const double Damping = 7.2;
    public const double Sensitivity = .008;
    public const double PitchLimit = 85 * Math.PI / 180;
    public const double MaximumSpeed = 10;
    public const double StopVelocity = .01;
    public const double IdleDelaySeconds = .65;
    public const double ReleaseFreshnessSeconds = .1;
    public const double VelocitySmoothingSeconds = .045;

    private double _lastX, _lastY, _lastSampleTime, _lastMovementTime;
    private double _idleWaitRemaining;
    private bool _discardNextStep;

    public double Yaw { get; private set; }
    public double Pitch { get; private set; }
    public double YawVelocity { get; private set; }
    public double PitchVelocity { get; private set; }
    public bool IsDragging => Phase == ModelOrbitPhase.Dragging;
    public bool IsActive { get; private set; } = true;
    public ModelOrbitPhase Phase { get; private set; } = ModelOrbitPhase.Idle;

    public ModelOrbitMotion() => Pitch = InitialPitch;

    public void BeginDrag(double x, double y, double timeSeconds)
    {
        RequireFinite(x, nameof(x));
        RequireFinite(y, nameof(y));
        RequireTime(timeSeconds, nameof(timeSeconds));
        if (!IsActive) return;
        _lastX = x;
        _lastY = y;
        _lastSampleTime = _lastMovementTime = timeSeconds;
        ClearVelocity();
        _idleWaitRemaining = IdleDelaySeconds;
        Phase = ModelOrbitPhase.Dragging;
    }

    public void MoveDrag(double x, double y, double timeSeconds)
    {
        RequireFinite(x, nameof(x));
        RequireFinite(y, nameof(y));
        RequireTime(timeSeconds, nameof(timeSeconds));
        if (!IsDragging || !IsActive) return;
        RequireMonotonic(timeSeconds);

        // Camera orbit yaw is opposite to the model's apparent horizontal rotation.
        var yawDelta = (_lastX - x) * Sensitivity;
        var nextYaw = Yaw + yawDelta;
        RequireFinite(nextYaw, nameof(x));
        var nextPitch = Math.Clamp(Pitch + (y - _lastY) * Sensitivity, -PitchLimit, PitchLimit);
        var pitchDelta = nextPitch - Pitch;
        var elapsed = timeSeconds - _lastSampleTime;
        Yaw = nextYaw;
        Pitch = nextPitch;

        if (elapsed > 0)
        {
            var sampledYaw = Math.Clamp(yawDelta / elapsed, -MaximumSpeed, MaximumSpeed);
            var sampledPitch = Math.Clamp(pitchDelta / elapsed, -MaximumSpeed, MaximumSpeed);
            LimitSpeed(ref sampledYaw, ref sampledPitch);
            // Continuous-time EMA keeps the response stable as pointer sample frequency changes.
            var blend = 1 - Math.Exp(-elapsed / VelocitySmoothingSeconds);
            YawVelocity += blend * (sampledYaw - YawVelocity);
            PitchVelocity += blend * (sampledPitch - PitchVelocity);
            StopOutwardPitchVelocity();
        }
        else ClearVelocity();

        if (yawDelta != 0 || pitchDelta != 0) _lastMovementTime = timeSeconds;
        _lastX = x;
        _lastY = y;
        _lastSampleTime = timeSeconds;
    }

    public void EndDrag(double timeSeconds)
    {
        RequireTime(timeSeconds, nameof(timeSeconds));
        if (!IsDragging) return;
        RequireMonotonic(timeSeconds);
        if (timeSeconds - _lastMovementTime > ReleaseFreshnessSeconds) ClearVelocity();
        StopOutwardPitchVelocity();
        if (CurrentSpeed > StopVelocity) Phase = ModelOrbitPhase.Inertia;
        else EnterIdle();
    }

    /// <summary>Cancels an active gesture without adding momentum; a completed gesture is unchanged.</summary>
    public void CancelDrag()
    {
        if (IsDragging) EnterIdle();
    }

    public void SetActive(bool active)
    {
        if (active == IsActive) return;
        IsActive = active;
        ClearVelocity();
        _idleWaitRemaining = IdleDelaySeconds;
        Phase = active ? ModelOrbitPhase.Idle : ModelOrbitPhase.Paused;
        // The first resumed frame may include all time spent hidden or detached.
        _discardNextStep = active;
    }

    public void Step(double dt)
    {
        RequireTime(dt, nameof(dt));
        if (!IsActive || dt == 0 || IsDragging) return;
        if (_discardNextStep) { _discardNextStep = false; return; }

        while (dt > 0)
        {
            if (Phase == ModelOrbitPhase.Idle)
            {
                var waiting = Math.Min(dt, _idleWaitRemaining);
                _idleWaitRemaining -= waiting;
                dt -= waiting;
                Yaw += IdleYawSpeed * dt;
                return;
            }

            if (Phase != ModelOrbitPhase.Inertia) return;
            StopOutwardPitchVelocity();
            if (CurrentSpeed <= StopVelocity) { EnterIdle(); continue; }

            var stopTime = Math.Log(CurrentSpeed / StopVelocity) / Damping;
            var pitchBoundaryTime = TimeToPitchBoundary();
            var elapsed = Math.Min(dt, Math.Min(stopTime, pitchBoundaryTime));
            var decay = Math.Exp(-Damping * elapsed);
            var integral = (1 - decay) / Damping;
            Yaw += YawVelocity * integral;
            Pitch = Math.Clamp(Pitch + PitchVelocity * integral, -PitchLimit, PitchLimit);
            YawVelocity *= decay;
            PitchVelocity *= decay;
            dt -= elapsed;

            if (elapsed >= pitchBoundaryTime)
            {
                Pitch = Math.CopySign(PitchLimit, PitchVelocity);
                PitchVelocity = 0;
            }
            if (elapsed >= stopTime || CurrentSpeed <= StopVelocity) EnterIdle();
            // Otherwise the remaining time is processed after a pitch boundary, or the frame ends.
        }
    }

    private double CurrentSpeed => Math.Max(Math.Abs(YawVelocity), Math.Abs(PitchVelocity));

    private double TimeToPitchBoundary()
    {
        if (PitchVelocity == 0) return double.PositiveInfinity;
        var distance = PitchVelocity > 0 ? PitchLimit - Pitch : -PitchLimit - Pitch;
        var fraction = distance * Damping / PitchVelocity;
        return fraction >= 0 && fraction < 1
            ? -Math.Log(1 - fraction) / Damping
            : double.PositiveInfinity;
    }

    private void StopOutwardPitchVelocity()
    {
        if ((Pitch >= PitchLimit && PitchVelocity > 0) || (Pitch <= -PitchLimit && PitchVelocity < 0))
            PitchVelocity = 0;
    }

    private void EnterIdle()
    {
        ClearVelocity();
        _idleWaitRemaining = IdleDelaySeconds;
        Phase = IsActive ? ModelOrbitPhase.Idle : ModelOrbitPhase.Paused;
    }

    private void ClearVelocity() => YawVelocity = PitchVelocity = 0;

    private static void LimitSpeed(ref double yaw, ref double pitch)
    {
        var magnitude = Math.Sqrt(yaw * yaw + pitch * pitch);
        if (magnitude <= MaximumSpeed) return;
        yaw *= MaximumSpeed / magnitude;
        pitch *= MaximumSpeed / magnitude;
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "A finite value is required.");
    }

    private static void RequireTime(double value, string name)
    {
        RequireFinite(value, name);
        if (value < 0) throw new ArgumentOutOfRangeException(name, "Time must not be negative.");
    }

    private void RequireMonotonic(double timeSeconds)
    {
        if (timeSeconds < _lastSampleTime)
            throw new ArgumentOutOfRangeException(nameof(timeSeconds), "Drag sample times must not move backwards.");
    }
}
