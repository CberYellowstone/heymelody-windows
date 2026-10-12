namespace HeyMelodyWindows.App;

/// <summary>How often the 3D model view may draw while it rotates.</summary>
public enum ModelFrameRate
{
    /// <summary>Every display frame (120 Hz on a 120 Hz monitor).</summary>
    Display = 0,
    Fps60 = 60,
    Fps30 = 30
}

/// <summary>Local rendering choices, defaulting to GPU at the display rate and shared by current and later model views.</summary>
internal static class ModelRenderingPreferences
{
    private static bool _useGpuAcceleration = ThemePreferences.LoadModelGpuAcceleration();
    private static ModelFrameRate _frameRate = ThemePreferences.LoadModelFrameRate();
    internal static bool UseGpuAcceleration => _useGpuAcceleration;
    internal static ModelFrameRate FrameRate => _frameRate;
    internal static event EventHandler? Changed;

    internal static void Reload() => Apply(ThemePreferences.LoadModelGpuAcceleration(), ThemePreferences.LoadModelFrameRate());

    internal static void Apply(bool enabled) => Apply(enabled, _frameRate);

    internal static void Apply(bool enabled, ModelFrameRate frameRate)
    {
        if (_useGpuAcceleration == enabled && _frameRate == frameRate) return;
        _useGpuAcceleration = enabled;
        _frameRate = frameRate;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
