using HeyMelody.Rendering.Filament;

namespace HeyMelodyWindows.App;

/// <summary>How often the 3D model view may draw while it rotates.</summary>
public enum ModelFrameRate
{
    Unlimited = 0,
    Fps30 = 30,
    Fps60 = 60
}

/// <summary>Rendering choices shared by current and later views. Vulkan is the default outside macOS.</summary>
internal static class ModelRenderingPreferences
{
    private static bool _compatibilityMode = ThemePreferences.LoadModelCompatibilityMode();
    private static ModelFrameRate _frameRate = ThemePreferences.LoadModelFrameRate();
    internal static bool CompatibilityMode => _compatibilityMode;
    internal static FilamentBackend Backend => OperatingSystem.IsMacOS() ? FilamentBackend.Metal
        : _compatibilityMode ? FilamentBackend.OpenGL : FilamentBackend.Vulkan;
    internal static ModelFrameRate FrameRate => _frameRate;
    internal static event EventHandler? Changed;

    internal static void Reload() => Apply(ThemePreferences.LoadModelCompatibilityMode(), ThemePreferences.LoadModelFrameRate());

    internal static void Apply(bool enabled) => Apply(enabled, _frameRate);

    internal static void Apply(bool enabled, ModelFrameRate frameRate)
    {
        frameRate = NormalizeFrameRate(frameRate);
        if (_compatibilityMode == enabled && _frameRate == frameRate) return;
        _compatibilityMode = enabled;
        _frameRate = frameRate;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static ModelFrameRate NormalizeFrameRate(ModelFrameRate frameRate) => frameRate switch
    {
        ModelFrameRate.Fps30 => ModelFrameRate.Fps30,
        ModelFrameRate.Fps60 => ModelFrameRate.Fps60,
        _ => ModelFrameRate.Unlimited
    };
}
