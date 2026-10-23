using HeyMelodyWindows.Artwork;

namespace HeyMelodyWindows.App;

public sealed record AnimatedWebpVisual(ArtworkFile File, object Fallback);

public sealed class DeviceAnimatedWebpView(AnimatedWebpVisual visual) : AnimatedWebpView(visual.File,
    () => DeviceVisuals.DefaultTemplate.Build(visual.Fallback),
    visual.Fallback is FileBitmapVisual ? () => DeviceVisuals.DefaultTemplate.Build(visual.Fallback) : null,
    DeviceVisuals.CreateLoadingIndicator);
