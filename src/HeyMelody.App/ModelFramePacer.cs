namespace HeyMelodyWindows.App;

/// <summary>Limits model requests to 30 or 60 fps, or permits each animation callback without an application cap.</summary>
internal sealed class ModelFramePacer
{
    private long? _lastTimestamp;
    private long _timeCredit;

    internal ModelFrameRate FrameRate { get; private set; } = ModelFrameRate.Unlimited;

    internal ModelFramePacer(ModelFrameRate frameRate = ModelFrameRate.Unlimited) => Reset(frameRate);

    internal void Reset(ModelFrameRate frameRate)
    {
        FrameRate = ModelRenderingPreferences.NormalizeFrameRate(frameRate);
        _lastTimestamp = null;
        // A timestamp can round an exact frame boundary down by less than one TimeSpan tick (100 ns).
        // Retain that one tick as phase credit instead of advancing requests by a millisecond tolerance.
        _timeCredit = (int)FrameRate;
    }

    internal bool ShouldRender(TimeSpan timestamp)
    {
        // The caller invokes this exactly once per Avalonia animation callback; no independent loop is started.
        if (FrameRate == ModelFrameRate.Unlimited) return true;
        if (_lastTimestamp is not { } previous)
        {
            _lastTimestamp = timestamp.Ticks;
            return true;
        }

        _lastTimestamp = timestamp.Ticks;
        if (timestamp.Ticks < previous)
        {
            _timeCredit = (int)FrameRate;
            return false;
        }

        // Integer time credit retains the fractional phase at refresh rates such as 144 and 165 Hz.
        // Int128 also keeps an unusually long pause from overflowing before the missed frames are discarded.
        var credit = ((Int128)timestamp.Ticks - previous) * (int)FrameRate + _timeCredit;
        _timeCredit = (long)(credit % TimeSpan.TicksPerSecond);
        // There is one decision per callback; a long pause never produces a catch-up render loop.
        return credit >= TimeSpan.TicksPerSecond;
    }
}
