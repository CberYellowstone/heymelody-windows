using HeyMelodyWindows.Artwork;
namespace HeyMelodyWindows.App;
public sealed class DeviceLottieView(LottieVisual visual) : LottieView(visual.File, () => DeviceVisuals.DefaultTemplate.Build(visual.Fallback));