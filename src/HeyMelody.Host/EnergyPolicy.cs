namespace HeyMelodyWindows.Host;

internal enum EnergyMode { Default, Eco }

/// <summary>
/// One desired mode for the tray and every child it starts. Eco (EcoQoS plus idle priority) only applies while no
/// window is connected: a startup grace, an active launch hold or a recent disconnect keep Default so that a window
/// opening shortly after never meets a throttled backend. Handlers of <see cref="ModeChanged"/> may run out of order
/// and must read <see cref="Mode"/> (latest wins) rather than trust the argument.
/// </summary>
internal sealed class EnergyPolicy : IDisposable
{
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15), Linger = TimeSpan.FromSeconds(10), LaunchHold = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly ITimer _timer;
    private int _guiCount;
    // End of the latest grace, hold or linger period, as time since construction (monotonic).
    private TimeSpan _defaultUntil = StartupGrace;
    private EnergyMode _mode = EnergyMode.Default;
    private bool _disposed;
    public event Action<EnergyMode>? ModeChanged;

    public EnergyPolicy(TimeProvider time)
    {
        _time = time; _origin = time.GetTimestamp();
        _timer = time.CreateTimer(_ => Update(null), null, StartupGrace, Timeout.InfiniteTimeSpan);
    }

    public EnergyMode Mode { get { lock (_sync) return _mode; } }
    internal int GuiCount { get { lock (_sync) return _guiCount; } }

    public void SetGuiCount(int count) => Update(now =>
    {
        count = Math.Max(0, count);
        if (count == _guiCount) return;
        // Counting down to zero starts the linger; a reconnect inside it simply never reaches Eco.
        if (count == 0) _defaultUntil = Max(_defaultUntil, now + Linger);
        _guiCount = count;
    });

    /// <summary>Called before the tray starts a window: the child inherits the tray's priority class.</summary>
    public void HoldForLaunch() => Update(now => _defaultUntil = Max(_defaultUntil, now + LaunchHold));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private void Update(Action<TimeSpan>? change)
    {
        EnergyMode? changed = null;
        lock (_sync)
        {
            if (_disposed) return;
            var now = _time.GetElapsedTime(_origin);
            change?.Invoke(now);
            var holding = _guiCount > 0 || now < _defaultUntil;
            var next = holding ? EnergyMode.Default : EnergyMode.Eco;
            if (next != _mode) { _mode = next; changed = next; }
            // Only a timed period needs a wake-up; an open window ends it through SetGuiCount.
            _timer.Change(_guiCount == 0 && now < _defaultUntil ? _defaultUntil - now : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        if (changed is { } mode) ModeChanged?.Invoke(mode);
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _timer.Dispose();
    }
}
