using System.Globalization;
using System.Text.Json;

namespace HeyMelodyWindows.Devices;

public static class VersionConditionEvaluator
{
    /// <summary>Missing or uninterpretable context is conditional, never a guessed fallback version.</summary>
    public static VersionConditionStatus Evaluate(VersionConditions conditions, DeviceVersionContext? versions = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return Combine(conditions.Requirements.Select(x => x.Domain == VersionDomain.AndroidAppMarker
            ? EvaluateApp(x.Minimum, versions?.AndroidAppVersionMarker)
            : EvaluateFirmware(x.Minimum, versions?.FirmwareVersion)).ToArray());
    }
    /// <summary>Firmware requirements remain diagnostics; actual App requirements still gate controls.</summary>
    public static VersionConditionStatus EvaluateForControl(VersionConditions conditions, DeviceVersionContext? versions = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return Combine(conditions.Requirements.Select(x => x.Domain switch
        {
            VersionDomain.AndroidAppMarker => EvaluateApp(x.Minimum, versions?.AndroidAppVersionMarker),
            VersionDomain.EarbudFirmware => VersionConditionStatus.Pass,
            _ => VersionConditionStatus.Conditional
        }).ToArray());
    }
    public static IReadOnlyList<FirmwareConditionIssue> GetFirmwareIssues(VersionConditions conditions, DeviceVersionContext? versions = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return Array.AsReadOnly(conditions.Requirements.Where(x => x.Domain == VersionDomain.EarbudFirmware)
            .Select(x => new FirmwareConditionIssue(x.SourceField, x.Minimum.Clone(), versions?.FirmwareVersion,
                EvaluateFirmware(x.Minimum, versions?.FirmwareVersion)))
            .Where(x => x.Status != VersionConditionStatus.Pass).ToArray());
    }
    public static VersionConditionStatus Combine(params VersionConditionStatus[] conditions) =>
        conditions.Contains(VersionConditionStatus.Fail) ? VersionConditionStatus.Fail :
        conditions.Contains(VersionConditionStatus.Conditional) ? VersionConditionStatus.Conditional : VersionConditionStatus.Pass;
    private static VersionConditionStatus EvaluateApp(JsonElement minimum, long? current)
    {
        if (minimum.ValueKind != JsonValueKind.Number || !minimum.TryGetInt64(out long marker) || marker < 0)
            return VersionConditionStatus.Conditional;
        return marker == 0 ? VersionConditionStatus.Pass : !current.HasValue || current.Value < 0
            ? VersionConditionStatus.Conditional : current.Value >= marker ? VersionConditionStatus.Pass : VersionConditionStatus.Fail;
    }
    private static VersionConditionStatus EvaluateFirmware(JsonElement? minimum, string? current)
    {
        if (!minimum.HasValue || minimum.Value.ValueKind == JsonValueKind.Null) return VersionConditionStatus.Pass;
        var value = minimum.Value;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long marker) && marker == 0) return VersionConditionStatus.Pass;
        string? required = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        if (required == "0") return VersionConditionStatus.Pass;
        if (!TryParts(required, out var requiredParts) || !TryParts(current, out var currentParts)) return VersionConditionStatus.Conditional;
        // Do not compare a firmware code marker to a dotted display version: that mapping is not evidenced.
        if ((requiredParts.Length == 1) != (currentParts.Length == 1)) return VersionConditionStatus.Conditional;
        for (int i = 0; i < Math.Max(requiredParts.Length, currentParts.Length); i++)
        {
            long expected = i < requiredParts.Length ? requiredParts[i] : 0;
            long actual = i < currentParts.Length ? currentParts[i] : 0;
            if (actual != expected) return actual > expected ? VersionConditionStatus.Pass : VersionConditionStatus.Fail;
        }
        return VersionConditionStatus.Pass;
    }
    private static bool TryParts(string? version, out long[] parts)
    {
        parts = [];
        if (string.IsNullOrEmpty(version)) return false;
        string[] fields = version.Split('.');
        var values = new long[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            if (!long.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        parts = values; return true;
    }
}
