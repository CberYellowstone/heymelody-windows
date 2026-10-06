using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Devices;

[JsonConverter(typeof(JsonStringEnumConverter<VersionConditionStatus>))]
public enum VersionConditionStatus { Pass, Fail, Conditional }
public enum VersionDomain { AndroidAppMarker, EarbudFirmware }

/// <summary>Supply versions from their actual domain; the Windows app version is never used.</summary>
public sealed record DeviceVersionContext(string? FirmwareVersion = null, long? AndroidAppVersionMarker = null);
public sealed record SourceProvenance(string ArchivePath, string ArchiveMember, string Sha256);

/// <summary>Detached source JSON preserves all unprojected fields, including future conditions.</summary>
public abstract class SourceMetadata
{
    private readonly JsonElement _rawSource;
    protected SourceMetadata(JsonElement source) => _rawSource = source.Clone();
    public JsonElement RawSource => _rawSource.Clone();
}

public sealed class VersionRequirement
{
    internal VersionRequirement(string sourceField, VersionDomain domain, JsonElement minimum)
    { SourceField = sourceField; Domain = domain; Minimum = minimum.Clone(); }
    public string SourceField { get; }
    public VersionDomain Domain { get; }
    public JsonElement Minimum { get; }
}

public sealed class VersionConditions : SourceMetadata
{
    internal VersionConditions(JsonElement source, IEnumerable<VersionRequirement> requirements) : base(source)
    { Requirements = Array.AsReadOnly(requirements.ToArray()); }
    public IReadOnlyList<VersionRequirement> Requirements { get; }
    public long? MinimumAndroidAppMarker => Requirements.Where(x => x.Domain == VersionDomain.AndroidAppMarker)
        .Select(x => x.Minimum.ValueKind == JsonValueKind.Number && x.Minimum.TryGetInt64(out long n) ? (long?)n : null)
        .Max();
    public JsonElement? MinimumFirmware => Requirements.FirstOrDefault(x => x.Domain == VersionDomain.EarbudFirmware)?.Minimum.Clone();
    public static VersionConditions FromSource(JsonElement source) => ProfileJson.Conditions(source);
    public VersionConditionStatus Evaluate(DeviceVersionContext? versions = null) => VersionConditionEvaluator.Evaluate(this, versions);
}

public sealed class NoiseReductionMode : SourceMetadata
{
    internal NoiseReductionMode(JsonElement source, int protocolIndex, int modeType, IEnumerable<NoiseReductionMode> children, VersionConditions conditions)
        : base(source)
    { ProtocolIndex = protocolIndex; ModeType = modeType; Children = Array.AsReadOnly(children.ToArray()); Conditions = conditions; }
    public int ProtocolIndex { get; }
    public int ModeType { get; }
    public IReadOnlyList<NoiseReductionMode> Children { get; }
    public VersionConditions Conditions { get; }
}

public sealed class EqualizerMode : SourceMetadata
{
    internal EqualizerMode(JsonElement source, int protocolIndex, int modeType, int? tag, int? summaryId, VersionConditions conditions) : base(source)
    { ProtocolIndex = protocolIndex; ModeType = modeType; Tag = tag; SummaryId = summaryId; Conditions = conditions; }
    public int ProtocolIndex { get; }
    public int ModeType { get; }
    public int? Tag { get; }
    public int? SummaryId { get; }
    public VersionConditions Conditions { get; }
}

public sealed class GameSoundMode : SourceMetadata
{
    internal GameSoundMode(JsonElement source, int type, IEnumerable<string> packageNames, VersionConditions conditions) : base(source)
    { Type = type; PackageNames = Array.AsReadOnly(packageNames.ToArray()); Conditions = conditions; }
    public int Type { get; }
    public IReadOnlyList<string> PackageNames { get; }
    public VersionConditions Conditions { get; }
}

public sealed class MultiConnectFunction : SourceMetadata
{
    internal MultiConnectFunction(JsonElement source, string functionType, VersionConditions conditions) : base(source)
    { FunctionType = functionType; Conditions = conditions; }
    public string FunctionType { get; }
    public VersionConditions Conditions { get; }
}

internal static class ProfileJson
{
    internal static int? Int(JsonElement element, string name) => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
    internal static IReadOnlyList<int> Ints(JsonElement element, string name) => System.Array.AsReadOnly(Array(element, name).Select(x => x.GetInt32()).ToArray());
    internal static IEnumerable<JsonElement> Array(JsonElement element, string name) => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    internal static NoiseReductionMode NoiseMode(JsonElement x) => new(x, x.GetProperty("protocolIndex").GetInt32(), x.GetProperty("modeType").GetInt32(), Array(x, "childrenMode").Select(NoiseMode), Conditions(x));
    internal static VersionConditions Conditions(JsonElement x)
    {
        var requirements = new List<VersionRequirement>();
        foreach (string name in new[] { "minVersion", "minAppVersion", "minAppVersionCode" })
            if (x.TryGetProperty(name, out var v)) requirements.Add(new(name, VersionDomain.AndroidAppMarker, v));
        foreach (string name in new[] { "minFirmVersion", "minFirmware", "minFirmwareVersion" })
            if (x.TryGetProperty(name, out var v)) requirements.Add(new(name, VersionDomain.EarbudFirmware, v));
        return new(x, requirements);
    }
}
