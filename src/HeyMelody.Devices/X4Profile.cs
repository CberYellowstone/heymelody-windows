using System.Security.Cryptography;
using System.Text.Json;

namespace HeyMelodyWindows.Devices;

public static class X4Profile
{
    public const string AssetSha256 = "95FCB16E291D5E8C3669745A20907D80C6F21A9D3982DCF1A22587E263DBF0DB";
    public const string ArchivePath = "reference/OPPO_Enco_X4_v116_LHDC_HiRes_Evidence.zip";
    public const string ArchiveMember = "evidence/app_assets/builtin_x4_compatWhiteList.json";
    private static readonly Lazy<DeviceProfile> Profile = new(Read);
    public static DeviceProfile Load() => Profile.Value;
    private static DeviceProfile Read()
    {
        using var stream = typeof(X4Profile).Assembly.GetManifestResourceStream("HeyMelodyWindows.Devices.Assets.oppo-enco-x4.json")
            ?? throw new InvalidDataException("Missing embedded X4 profile.");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != AssetSha256) throw new InvalidDataException("X4 profile provenance hash mismatch.");
        using var document = JsonDocument.Parse(bytes);
        return new(document.RootElement, new(ArchivePath, ArchiveMember, AssetSha256));
    }
}

public static class DeviceModelResolver
{
    public static DeviceProfile? FindByProductId(string? productId) => string.Equals(productId, "06EC10", StringComparison.Ordinal) ? X4Profile.Load() : null;
}
