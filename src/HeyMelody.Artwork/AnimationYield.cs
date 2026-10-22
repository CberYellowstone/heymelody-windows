using Avalonia.Threading;

namespace HeyMelodyWindows.Artwork;

/// <summary>
/// Tells decorative animations (3D model auto-rotation, Lottie) to drop to <see cref="FrameRate"/> while the UI does
/// something latency-sensitive: a settings expander opening, scrolling, or the window being inactive. UI thread only.
/// </summary>
/// <remarks>
/// A pulse is a time window that later pulses extend; holds are persistent. Nothing runs while neither is active, so an
/// idle window has no timer. Changed fires only when <see cref="Active"/> flips.
/// </remarks>
public static class AnimationYield
{
    public const int FrameRate = 30;
    private static readonly HashSet<object> Holds = [];
    private static DispatcherTimer? _timer;
    private static long _until;
    private static bool _pulsing;

    public static bool Active => _pulsing || Holds.Count != 0;
    public static event EventHandler? Changed;
    /// <summary>Raised when a pulse window has run out (not for holds); used to schedule resource cleanup.</summary>
    public static event EventHandler? PulseEnded;

    public static void Pulse(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        bool was = Active;
        _until = Math.Max(_until, Environment.TickCount64 + (long)duration.TotalMilliseconds);
        if (!_pulsing)
        {
            _pulsing = true;
            // One short-lived timer per window: nothing is kept (or bound to a dispatcher) while idle.
            _timer = CreateTimer(duration); _timer.Start();
        }
        if (!was) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetHold(object owner, bool hold)
    {
        bool was = Active;
        if (hold ? !Holds.Add(owner) : !Holds.Remove(owner)) return;
        if (was != Active) Changed?.Invoke(null, EventArgs.Empty);
    }

    private static DispatcherTimer CreateTimer(TimeSpan interval)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) =>
        {
            long left = _until - Environment.TickCount64;
            // Re-arm explicitly: assigning Interval from inside Tick does not restart a timer that is mid-tick.
            if (left > 0) { timer.Stop(); timer.Interval = TimeSpan.FromMilliseconds(left); timer.Start(); return; }
            timer.Stop(); _timer = null; _pulsing = false;
            if (Holds.Count == 0) Changed?.Invoke(null, EventArgs.Empty);
            PulseEnded?.Invoke(null, EventArgs.Empty);
        };
        return timer;
    }

    /// <summary>Test hook: drops all state without raising events.</summary>
    internal static void Reset() { _timer?.Stop(); _timer = null; _pulsing = false; _until = 0; Holds.Clear(); }
}
