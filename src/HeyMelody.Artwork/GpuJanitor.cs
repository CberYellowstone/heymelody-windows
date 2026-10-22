using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;

namespace HeyMelodyWindows.Artwork;

/// <summary>Allows one cleanup per interval; the first request always passes.</summary>
public sealed class CleanupGate(TimeSpan interval)
{
    private long _last = long.MinValue;
    public bool TryAcquire(long nowMilliseconds)
    {
        if (_last != long.MinValue && nowMilliseconds - _last < (long)interval.TotalMilliseconds) return false;
        _last = nowMilliseconds; return true;
    }
}

/// <summary>
/// Avalonia only evicts GPU resources when the cache budget is exceeded and never purges unused ones, so a 256 MiB budget
/// would stay full of stale textures. This 1x1 invisible composition visual lends its render pass (the only place a
/// GRContext is reachable) to drop resources unused for 30 s. Requests come from events, never from a timer.
/// </summary>
public sealed class GpuJanitor : Control
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10), MaxAge = TimeSpan.FromSeconds(30);
    private static readonly CleanupGate Gate = new(Interval);
    private static GpuJanitor? _current;
    private CompositionCustomVisual? _visual;

    public GpuJanitor() { Width = 1; Height = 1; IsHitTestVisible = false; Focusable = false; }

    /// <summary>Asks the attached janitor to purge soon; at most one purge per interval. UI thread.</summary>
    public static void Request()
    {
        if (_current?._visual is { } visual && Gate.TryAcquire(Environment.TickCount64)) visual.SendHandlerMessage(Message);
    }

    private static readonly object Message = new();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ElementComposition.GetElementVisual(this) is not { } parent) return;
        _visual = parent.Compositor.CreateCustomVisual(new Handler());
        _visual.Size = new Vector2(1, 1);
        ElementComposition.SetElementChildVisual(this, _visual);
        // The newest window owns cleanup; a recreated window may attach before the old one detaches.
        _current = this;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_current == this) _current = null;
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class Handler : CompositionCustomVisualHandler
    {
        public override void OnMessage(object message) { if (message == Message) Invalidate(); }
        public override void OnRender(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            lease.GrContext?.PurgeUnusedResources((long)MaxAge.TotalMilliseconds);
        }
    }
}
