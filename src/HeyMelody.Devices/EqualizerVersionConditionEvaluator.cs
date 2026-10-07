using System.Globalization;
using System.Text.Json;

namespace HeyMelodyWindows.Devices;

/// <summary>The firmware marker used by both APK EqualizerVO implementations, distinct from semantic versions.</summary>
public static class EqualizerVersionConditionEvaluator
{
    public static VersionConditionStatus Evaluate(EqualizerMode mode, DeviceVersionContext? versions = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return VersionConditionEvaluator.Combine(mode.Conditions.Requirements
            .Where(x => x.Domain == VersionDomain.EarbudFirmware || mode.SourceArray == "equalizerModeByVersion")
            .Select(x => x.Domain == VersionDomain.AndroidAppMarker
                ? AppMarker(x.Minimum, versions?.AndroidAppVersionMarker)
                : FirmwareMarker(x.Minimum, versions?.FirmwareVersion)).ToArray());
    }

    public static bool TryGetFirmwareMarker(string? firmware, out int marker)
    {
        marker = 0;
        if (string.IsNullOrEmpty(firmware)) return false;
        string[] fields = firmware.Split('.');
        // Java String.split(regex) drops trailing empty components.
        int fieldCount = fields.Length;
        while (fieldCount > 0 && fields[fieldCount - 1].Length == 0) fieldCount--;
        if (fieldCount == 0) return false;
        if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first)) return false;
        if (fieldCount != 3) { marker = first; return true; }
        if (!int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int second)) return false;
        marker = first == 0 ? second : second == 0 ? first : Math.Min(first, second);
        return true;
    }

    private static VersionConditionStatus AppMarker(JsonElement minimum, long? current)
    {
        if (minimum.ValueKind != JsonValueKind.Number || !minimum.TryGetInt64(out long required) || required < 0)
            return VersionConditionStatus.Conditional;
        return required == 0 ? VersionConditionStatus.Pass : current is null or < 0 ? VersionConditionStatus.Conditional
            : current >= required ? VersionConditionStatus.Pass : VersionConditionStatus.Fail;
    }

    private static VersionConditionStatus FirmwareMarker(JsonElement minimum, string? firmware)
    {
        // The vendor EQ DTO stores this field as an integer; display-semver minima are not inferred.
        if (minimum.ValueKind != JsonValueKind.Number || !minimum.TryGetInt32(out int required) || required < 0)
            return VersionConditionStatus.Conditional;
        return required == 0 ? VersionConditionStatus.Pass : !TryGetFirmwareMarker(firmware, out int current)
            ? VersionConditionStatus.Conditional : current >= required ? VersionConditionStatus.Pass : VersionConditionStatus.Fail;
    }
}
