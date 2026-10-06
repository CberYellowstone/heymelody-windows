namespace HeyMelodyWindows.Devices;

/// <summary>Command support is metadata only and never authorizes transmitting a setter.</summary>
public sealed record FeatureCapability(
    bool? StaticDeclared,
    ushort ReadCommand,
    ushort WriteCommand,
    bool ProtocolReadSupported,
    bool ProtocolWriteSupported,
    byte? FeatureId,
    byte? RawState,
    bool? State,
    VersionConditionStatus VersionCondition);

public enum GameModeSelection { Unobserved, LegacyReported, ModernReported }

public sealed record DeviceCapabilities(
    string? ProductId,
    VersionConditionStatus ProfileVersionCondition,
    FeatureCapability HiRes,
    FeatureCapability SpatialAudio,
    FeatureCapability LegacySpatialAudio,
    FeatureCapability GameMode,
    FeatureCapability LegacyGameMode,
    FeatureCapability ModernGameMode,
    GameModeSelection GameModeSelection,
    FeatureCapability GameSound,
    FeatureCapability Anc,
    FeatureCapability Equalizer,
    FeatureCapability CustomEqualizer,
    FeatureCapability DualConnection,
    FeatureCapability BassEngine,
    IReadOnlyList<int> SpatialTypes,
    bool? SpatialHiResMutex,
    IReadOnlyList<int> GameSoundTypes,
    IReadOnlyList<int> GameSoundMutexes);
