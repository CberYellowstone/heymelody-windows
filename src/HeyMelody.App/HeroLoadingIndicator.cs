using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Artwork;

namespace HeyMelodyWindows.App;

/// <summary>
/// The left-hand product area's loading state for every visual kind (artwork lookup, bitmap, Lottie, WebP, 3D model).
/// The ring appears only after <see cref="Delay"/>, so a decode of a few tens of milliseconds does not flash it, and it
/// animates only while it is effectively visible in a shown, non-minimized window (a load finishing under a minimized
/// window must not keep a ring spinning).
/// </summary>
internal sealed class HeroLoadingIndicator : ContentControl
{
    /// <summary>Headless tests set this to zero; their dispatcher clock does not advance on its own.</summary>
    internal static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(200);

    private readonly FAProgressRing _ring = new()
    {
        Width = 48, Height = 48, IsIndeterminate = true, IsActive = false, IsHitTestVisible = false
    };
    private DispatcherTimer? _reveal;
    private VisibilityTracker? _visibility;

    public HeroLoadingIndicator()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        _ring.IsVisible = false;
        Content = _ring;
        AutomationProperties.SetName(this, "正在加载耳机外观");
    }

    /// <summary>True once the delay has passed and the ring is shown.</summary>
    internal bool RingShown => _ring.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _visibility = new VisibilityTracker(this, Refresh);
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visibility?.Dispose();
        _visibility = null;
        Hide();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property.Name == "IsEffectivelyVisible") Refresh();
    }

    private void Refresh()
    {
        if (!CanAnimate) { Hide(); return; }
        if (_ring.IsVisible || _reveal is not null) return;
        if (Delay <= TimeSpan.Zero) { Show(); return; }
        _reveal = new DispatcherTimer { Interval = Delay };
        _reveal.Tick += (_, _) => Show();
        _reveal.Start();
    }

    private void Show()
    {
        StopTimer();
        if (!CanAnimate) return;
        _ring.IsVisible = true;
        _ring.IsActive = true;
    }

    private bool CanAnimate => IsEffectivelyVisible && _visibility is not null
        && TopLevel.GetTopLevel(this) is Window { IsVisible: true } window && window.WindowState != WindowState.Minimized;

    private void Hide()
    {
        StopTimer();
        _ring.IsActive = false;
        _ring.IsVisible = false;
    }

    private void StopTimer() { _reveal?.Stop(); _reveal = null; }
}
