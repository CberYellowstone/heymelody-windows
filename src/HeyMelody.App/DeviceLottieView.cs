using HeyMelodyWindows.Artwork;
namespace HeyMelodyWindows.App;
// Product artwork: marker-aware playback with the package's detail image as a slow-load poster.
public sealed class DeviceLottieView(LottieVisual visual) : LottieView(visual.File, () => DeviceVisuals.DefaultTemplate.Build(visual.Fallback),
    LottiePresentation.Product, visual.Fallback is FileBitmapVisual ? () => DeviceVisuals.DefaultTemplate.Build(visual.Fallback) : null,
    DeviceVisuals.CreateLoadingIndicator);
