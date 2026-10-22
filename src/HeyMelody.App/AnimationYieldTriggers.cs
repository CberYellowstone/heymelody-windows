using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Artwork;

namespace HeyMelodyWindows.App;

/// <summary>Connects UI events to <see cref="AnimationYield"/> and the GPU janitor so hero animations step aside for them.</summary>
internal static class AnimationYieldTriggers
{
    // Covers the expander's layout pass and the first raster of its new content.
    internal static readonly TimeSpan ExpanderPulse = TimeSpan.FromMilliseconds(600), ScrollPulse = TimeSpan.FromMilliseconds(300);
    private static bool _registered;

    /// <summary>Process-wide, once: any expander opening or closing yields (handlers live as long as the property).</summary>
    internal static void RegisterExpanders()
    {
        if (_registered) return;
        _registered = true;
        FASettingsExpander.IsExpandedProperty.Changed.Subscribe(new Observer(_ => AnimationYield.Pulse(ExpanderPulse)));
        AnimationYield.PulseEnded += (_, _) => GpuJanitor.Request();
    }

    internal static void AttachScroll(ScrollViewer scroll) => scroll.ScrollChanged += (_, _) => AnimationYield.Pulse(ScrollPulse);

    /// <summary>Holds the yield while the window is inactive; released and unhooked when it closes.</summary>
    internal static void AttachWindow(Window window)
    {
        void Activated(object? s, EventArgs e) => AnimationYield.SetHold(window, false);
        void Deactivated(object? s, EventArgs e) { AnimationYield.SetHold(window, true); GpuJanitor.Request(); }
        window.Activated += Activated;
        window.Deactivated += Deactivated;
        window.Closed += (_, _) =>
        {
            window.Activated -= Activated; window.Deactivated -= Deactivated;
            AnimationYield.SetHold(window, false);
        };
    }

    private sealed class Observer(Action<Avalonia.AvaloniaPropertyChangedEventArgs> next) : IObserver<Avalonia.AvaloniaPropertyChangedEventArgs>
    {
        public void OnNext(Avalonia.AvaloniaPropertyChangedEventArgs value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
