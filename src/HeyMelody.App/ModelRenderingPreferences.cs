namespace HeyMelodyWindows.App;

/// <summary>Local rendering choice, defaulting to GPU and shared by current and subsequently created model views.</summary>
internal static class ModelRenderingPreferences
{
    private static bool _useGpuAcceleration = ThemePreferences.LoadModelGpuAcceleration();
    internal static bool UseGpuAcceleration => _useGpuAcceleration;
    internal static event EventHandler? Changed;

    internal static void Reload() => Apply(ThemePreferences.LoadModelGpuAcceleration());

    internal static void Apply(bool enabled)
    {
        if (_useGpuAcceleration == enabled) return;
        _useGpuAcceleration = enabled;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
