using System.Globalization;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    private static string? FormatFirmwareDisplay(IReadOnlyList<FirmwareEntry> entries, string? productType)
    {
        // HeyMelody EarphoneUtils Q.z: only running versions,
        // in left/right/case order. Hardware, backup and vendor versions are not display components.
        string? Running(int component) => entries.LastOrDefault(x => x.DeviceType == component && x.VersionType == 2)?.Version;
        static bool Numeric(string? value) => value is { Length: > 0 } && value.All(c => c is >= '0' and <= '9');
        if (productType is "S" or "N")
        {
            string? running = Running(1);
            return Numeric(running) && int.TryParse(running, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? string.Join(".", number.ToString("D3", CultureInfo.InvariantCulture).Select(c => c.ToString())) : null;
        }
        if (productType is not ("T1" or "T2" or "O1")) return null;
        string?[] components = [Running(1), Running(2), Running(3)];
        if (!components.Any(Numeric)) return null; // Retain an already formatted running version through Host's fallback.
        return string.Join(".", components.Select(x => Numeric(x) ? x : "0"));
    }
}
