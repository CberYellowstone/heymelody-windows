using System.Text.Json;

namespace HeyMelodyWindows.Devices;

public sealed class DeviceProfile : SourceMetadata
{
    internal DeviceProfile(JsonElement source, SourceProvenance provenance) : base(source)
    {
        ProductId = source.GetProperty("id").GetString()!;
        Name = source.GetProperty("name").GetString()!;
        Aliases = Array.AsReadOnly(source.GetProperty("children").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray());
        Uuid = Guid.Parse(source.GetProperty("uuid").GetString()!);
        SupportSpp = source.GetProperty("supportSpp").GetBoolean();
        Provenance = provenance;
        Conditions = ProfileJson.Conditions(source);
        var functions = source.GetProperty("function");
        _rawFunctions = functions.Clone();
        HighToneQuality = ProfileJson.Int(functions, "highToneQuality");
        HighAudio = ProfileJson.Int(functions, "highAudio");
        SpatialHiResMutex = ProfileJson.Int(functions, "spatialHiResMutex");
        SpatialTypes = ProfileJson.Ints(functions, "spatialTypes");
        CustomEqFrequencies = ProfileJson.Ints(functions, "customEqFrequency");
        GameMode = ProfileJson.Int(functions, "gameMode");
        GameSoundMutexes = ProfileJson.Ints(functions, "gameSoundMutexes");
        MultiDevicesConnect = ProfileJson.Int(functions, "multiDevicesConnect");
        NoiseReductionModes = Array.AsReadOnly(ProfileJson.Array(functions, "noiseReductionMode").Select(ProfileJson.NoiseMode).ToArray());
        EqualizerModes = Array.AsReadOnly(ProfileJson.Array(functions, "equalizerMode").Select(x => new EqualizerMode(x,
            x.GetProperty("protocolIndex").GetInt32(), x.GetProperty("modeType").GetInt32(), ProfileJson.Int(x,"tag"), ProfileJson.Int(x,"summaryId"), ProfileJson.Conditions(x))).ToArray());
        GameSoundModes = Array.AsReadOnly(ProfileJson.Array(functions, "gameSoundList").Select(x => new GameSoundMode(x,
            x.GetProperty("type").GetInt32(), ProfileJson.Array(x,"packageNameList").Select(n=>n.GetString()!), ProfileJson.Conditions(x))).ToArray());
        MultiConnectFunctions = Array.AsReadOnly(ProfileJson.Array(functions, "multiConnectFunctions").Select(x => new MultiConnectFunction(x,
            x.GetProperty("functionType").GetString()!, ProfileJson.Conditions(x))).ToArray());
    }
    private readonly JsonElement _rawFunctions;
    public string ProductId { get; }
    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public Guid Uuid { get; }
    public bool SupportSpp { get; }
    public SourceProvenance Provenance { get; }
    public VersionConditions Conditions { get; }
    public long? MinimumAndroidAppVersionMarker => Conditions.MinimumAndroidAppMarker;
    public JsonElement RawFunctions => _rawFunctions.Clone();
    public int? HighToneQuality { get; }
    public int? HighAudio { get; }
    public int? SpatialHiResMutex { get; }
    public IReadOnlyList<int> SpatialTypes { get; }
    public IReadOnlyList<int> CustomEqFrequencies { get; }
    public int? GameMode { get; }
    public IReadOnlyList<int> GameSoundMutexes { get; }
    public int? MultiDevicesConnect { get; }
    public IReadOnlyList<NoiseReductionMode> NoiseReductionModes { get; }
    public IReadOnlyList<EqualizerMode> EqualizerModes { get; }
    public IReadOnlyList<GameSoundMode> GameSoundModes { get; }
    public IReadOnlyList<MultiConnectFunction> MultiConnectFunctions { get; }
    public EqualizerMode? FindEqualizerByProtocolId(int protocolId) => EqualizerModes.FirstOrDefault(x => x.ProtocolIndex == protocolId);
    public NoiseReductionMode? FindNoiseReductionByProtocolId(int protocolId) => NoiseReductionModes
        .SelectMany(Flatten).FirstOrDefault(x => x.ProtocolIndex == protocolId);
    private static IEnumerable<NoiseReductionMode> Flatten(NoiseReductionMode mode)
    { yield return mode; foreach (var child in mode.Children.SelectMany(Flatten)) yield return child; }
}
