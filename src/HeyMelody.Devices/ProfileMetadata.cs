using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Devices;

[JsonConverter(typeof(JsonStringEnumConverter<VersionConditionStatus>))]
public enum VersionConditionStatus { Pass, Fail, Conditional }
public enum VersionDomain { AndroidAppMarker, EarbudFirmware }

/// <summary>Supply versions from their actual domain; the Windows app version is never used.</summary>
public sealed record DeviceVersionContext(string? FirmwareVersion = null, long? AndroidAppVersionMarker = null);
public sealed record FirmwareConditionIssue(string SourceField, JsonElement Minimum, string? CurrentVersion, VersionConditionStatus Status);
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
    public VersionConditionStatus EvaluateForControl(DeviceVersionContext? versions = null) => VersionConditionEvaluator.EvaluateForControl(this, versions);
    public IReadOnlyList<FirmwareConditionIssue> GetFirmwareIssues(DeviceVersionContext? versions = null) => VersionConditionEvaluator.GetFirmwareIssues(this, versions);
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
    internal EqualizerMode(JsonElement source, int protocolIndex, int modeType, int? tag, int? summaryId, VersionConditions conditions,
        string sourceArray = "equalizerMode") : base(source)
    {
        ProtocolIndex = protocolIndex; ModeType = modeType; Tag = tag; SummaryId = summaryId; Conditions = conditions;
        SourceArray = sourceArray; Order = ProfileJson.Int(source, "order") ?? 0;
        ColorId = ProfileJson.Int(source, "colorId") is >= 0 and <= byte.MaxValue ? (byte)ProfileJson.Int(source, "colorId")!.Value : null;
    }
    public int ProtocolIndex { get; }
    public int ModeType { get; }
    public int? Tag { get; }
    public int? SummaryId { get; }
    public VersionConditions Conditions { get; }
    public string SourceArray { get; }
    public int Order { get; }
    public byte? ColorId { get; }
    public VersionConditionStatus EvaluateConditions(DeviceVersionContext? versions = null) => EqualizerVersionConditionEvaluator.Evaluate(this, versions);
    public VersionConditionStatus EvaluateConditionsForControl(DeviceVersionContext? versions = null) => EqualizerVersionConditionEvaluator.EvaluateForControl(this, versions);
    public IReadOnlyList<FirmwareConditionIssue> GetFirmwareIssues(DeviceVersionContext? versions = null) => EqualizerVersionConditionEvaluator.GetFirmwareIssues(this, versions);
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
    internal static bool Property(JsonElement element, string name, out JsonElement value)
    { value = default; return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value); }
    internal static int? Int(JsonElement element, string name) => Property(element, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
    internal static string? Text(JsonElement element, string name) => Property(element, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static IReadOnlyList<int> Ints(JsonElement element, string name) => System.Array.AsReadOnly(Array(element, name)
        .Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int n) && n >= 0).Select(x => x.GetInt32()).ToArray());
    internal static IEnumerable<JsonElement> Array(JsonElement element, string name) => Property(element, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    internal static IEnumerable<NoiseReductionMode> NoiseModes(JsonElement parent, string name, int depth = 0)
    {
        if (depth >= 32) yield break;
        foreach (var x in Array(parent, name))
            if (Int(x, "protocolIndex") is >= 0 and <= byte.MaxValue && Int(x, "modeType") is >= 0)
                yield return new(x, Int(x, "protocolIndex")!.Value, Int(x, "modeType")!.Value, NoiseModes(x, "childrenMode", depth + 1), Conditions(x));
    }
    internal static IReadOnlyList<EqualizerMode> EqualizerModes(JsonElement functions, string name) => System.Array.AsReadOnly(Array(functions, name)
        .Where(x => Int(x, "protocolIndex") is >= 0 and <= byte.MaxValue && Int(x, "modeType") is >= 0)
        .Select(x => new EqualizerMode(x, Int(x, "protocolIndex")!.Value, Int(x, "modeType")!.Value,
            Int(x, "tag"), Int(x, "summaryId"), Conditions(x), name)).ToArray());
    internal static VersionConditions Conditions(JsonElement x)
    {
        var requirements = new List<VersionRequirement>();
        foreach (string name in new[] { "minVersion", "minAppVersion", "minAppVersionCode" })
            if (Property(x, name, out var v)) requirements.Add(new(name, VersionDomain.AndroidAppMarker, v));
        foreach (string name in new[] { "minFirmVersion", "minFirmware", "minFirmwareVersion" })
            if (Property(x, name, out var v)) requirements.Add(new(name, VersionDomain.EarbudFirmware, v));
        return new(x, requirements);
    }
}
