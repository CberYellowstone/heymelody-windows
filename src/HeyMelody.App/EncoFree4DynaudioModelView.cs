using HeyMelody.Rendering.Filament;

namespace HeyMelodyWindows.App;

/// <summary>Bundled original Enco Free4 Dynaudio artwork used by the temporary layout demo.</summary>
public sealed class EncoFree4DynaudioModelView : DeviceModelView
{
    public const string ModelSha256 = "7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6";
    private static readonly DeviceModelVisual Bundled = new(new ArtworkFile(
        new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", ModelSha256), DeviceVisuals.GenericProduct);
    public EncoFree4DynaudioModelView() : base(Bundled) { }
    public static Task<FilamentModelData> LoadModelAsync() => DeviceModelView.LoadModelAsync(Bundled);
}
