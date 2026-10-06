using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Devices;

public static class CapabilityResolver
{
    public static DeviceCapabilities Resolve(DeviceProfile? profile, CommandCapabilities commands,
        IReadOnlyDictionary<byte, byte> featureStates, DeviceVersionContext? versions = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(featureStates);
        var condition = profile?.Conditions.Evaluate(versions) ?? VersionConditionStatus.Conditional;
        bool? Declared(string name) => profile is null ? null : ProfileJson.Int(profile.RawFunctions, name) is int value ? value > 0 : null;
        FeatureCapability Feature(bool? declared, ushort read, ushort write, byte? featureId = null)
        {
            byte? raw = featureId.HasValue && featureStates.TryGetValue(featureId.Value, out byte value) ? value : null;
            return new(declared, read, write, commands.Supports(read), commands.Supports(write), featureId, raw,
                raw switch { 0 => false, 1 => true, _ => null }, condition);
        }
        bool? spatialDeclared = profile is null ? null : profile.SpatialTypes.Count > 0;
        var legacyGame = Feature(Declared("gameMode"), 0x010D, 0x0403, 0x06);
        var modernGame = Feature(Declared("gameMode"), 0x010D, 0x0403, 0x28);
        // Presence is evidence of a reported route. A bitmap or profile flag alone never selects modern state.
        var selection = featureStates.ContainsKey(0x28) ? GameModeSelection.ModernReported :
            featureStates.ContainsKey(0x06) ? GameModeSelection.LegacyReported : GameModeSelection.Unobserved;
        var game = selection == GameModeSelection.ModernReported ? modernGame : selection == GameModeSelection.LegacyReported
            ? legacyGame : Feature(Declared("gameMode"), 0x010D, 0x0403);
        return new(profile?.ProductId, condition,
            Feature(Declared("highToneQuality"), 0x010D, 0x0403, 0x18),
            Feature(spatialDeclared, 0x012A, 0x0422),
            Feature(spatialDeclared, 0x010D, 0x0403, 0x1B),
            game, legacyGame, modernGame, selection,
            // Dedicated game sound command reports a sound type, not feature 0x28 or a boolean.
            Feature(profile is null ? null : profile.GameSoundModes.Count > 0, 0x012B, 0x0423),
            Feature(profile is null ? null : profile.NoiseReductionModes.Count > 0, 0x010C, 0x0404),
            Feature(Declared("equalizer"), 0x010F, 0x0406),
            Feature(Declared("customEqualizer"), 0x0122, 0x0418),
            Feature(Declared("multiDevicesConnect"), 0x010D, 0x0403, 0x11),
            Feature(Declared("bassEngineSupport"), 0x0124, 0x041B),
            profile?.SpatialTypes ?? Array.AsReadOnly(Array.Empty<int>()),
            profile is null ? null : profile.SpatialHiResMutex switch { 0 => false, 1 => true, _ => null },
            Array.AsReadOnly(profile?.GameSoundModes.Select(x => x.Type).ToArray() ?? []),
            profile?.GameSoundMutexes ?? Array.AsReadOnly(Array.Empty<int>()));
    }
}
