using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Rendering;
using SkiaSharp;

namespace HeyMelodyWindows.Ui;

/// <summary>Rendering parameters shared by every HeyMelody UI process (compiled into each by source link).</summary>
internal static class RenderingSetup
{
    // Skia's default glyph (strike) cache is about 2 MB; Chinese text uses hundreds of glyphs per size and
    // weight, so a small cache re-rasterises them while scrolling and expanding. This is only a ceiling.
    internal const long FontCacheLimitBytes = 64L * 1024 * 1024;
    // Avalonia's default GPU resource budget is about 28 MiB, below one large hero image plus Lottie layers,
    // WebP frames and the glyph atlas; evicted textures would otherwise be uploaded again.
    internal const long GpuResourceLimitBytes = 256L * 1024 * 1024;

    internal static AppBuilder UseHeyMelodyRendering(this AppBuilder builder)
    {
        SKGraphics.SetFontCacheLimit(FontCacheLimitBytes);
        builder = builder.With(new SkiaOptions { MaxGpuResourceSizeBytes = GpuResourceLimitBytes });
        if (OperatingSystem.IsWindows())
            builder = builder.With(new Win32PlatformOptions
            {
                // These are Avalonia's defaults, pinned on purpose: Mica needs WinUI composition (a low-latency
                // swap chain would drop it), and both composition modes pace frames at the display refresh rate.
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface]
            });
        return builder;
    }
}

/// <summary>Opt-in frame-rate and frame-time overlay for checking 120 Hz behaviour on a real display.</summary>
/// <remarks>
/// Set HEYMELODY_RENDER_DIAGNOSTICS=1 before starting a window, or press Ctrl+Shift+F12 in it. An idle window renders
/// no frames, so the numbers are meaningful only while something animates: an expander, the 3D model or a wizard page.
/// </remarks>
internal static class RenderDiagnostics
{
    internal const string EnvironmentVariable = "HEYMELODY_RENDER_DIAGNOSTICS";
    internal const RendererDebugOverlays Overlays =
        RendererDebugOverlays.Fps | RendererDebugOverlays.RenderTimeGraph | RendererDebugOverlays.LayoutTimeGraph;

    internal static bool Enabled { get; private set; } = Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";
    internal static event EventHandler? Changed;

    internal static void Toggle() => Set(!Enabled);

    internal static void Set(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Applies the current choice to a window and lets Ctrl+Shift+F12 toggle it for every window.</summary>
    internal static void Attach(Window topLevel)
    {
        void Apply() => topLevel.RendererDiagnostics.DebugOverlays = Enabled ? Overlays : RendererDebugOverlays.None;
        void OnChanged(object? sender, EventArgs e) => Apply();
        Apply();
        Changed += OnChanged;
        topLevel.Closed += (_, _) => Changed -= OnChanged;
        topLevel.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.F12 || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return;
            Toggle();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
