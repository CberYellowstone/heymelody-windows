using HeyMelody.Rendering.Filament;

namespace HeyMelodyWindows.App;

/// <summary>One load generation, ending at its first presented frame. Overlapping stages are not additive.</summary>
internal readonly record struct ModelLoadTimings(
    int Generation,
    FilamentBackend Backend,
    double AttachToLoadStartMilliseconds,
    double RetirementWaitMilliseconds,
    double GateWaitMilliseconds,
    double ReadVerifyMilliseconds,
    bool CacheHit,
    double RuntimePreparationMilliseconds,
    double ProbeWaitMilliseconds,
    double EngineCreateMilliseconds,
    double ModelLoadMilliseconds,
    double FirstFrameMilliseconds,
    double TotalMilliseconds,
    FilamentProbeDisposition ProbeDisposition);
