using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace HeyMelodyWindows.Artwork;

/// <summary>Observes only what decides whether an animation may play: each ancestor's visibility and the window state.</summary>
/// <remarks>
/// Subscribing to every ancestor's general PropertyChanged event ran a handler for each bounds, opacity or transform
/// change above the view, i.e. on every frame of a layout or opacity animation.
/// </remarks>
internal sealed class VisibilityTracker : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    public VisibilityTracker(Visual visual, Action changed)
    {
        var observer = new Observer(changed);
        foreach (var ancestor in visual.GetVisualAncestors())
        {
            _subscriptions.Add(ancestor.GetPropertyChangedObservable(Visual.IsVisibleProperty).Subscribe(observer));
            if (ancestor is Window window) _subscriptions.Add(window.GetPropertyChangedObservable(Window.WindowStateProperty).Subscribe(observer));
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private sealed class Observer(Action changed) : IObserver<AvaloniaPropertyChangedEventArgs>
    {
        public void OnNext(AvaloniaPropertyChangedEventArgs value) => changed();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
