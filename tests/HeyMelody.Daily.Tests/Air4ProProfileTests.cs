using System.Text;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Resources;

internal static class Air4ProProfileTests
{
    private const string ProductId = "067C10";

    private static Fake SyntheticLegacyReplies()
    {
        // Synthetic capabilities and replies, not an Air4 Pro hardware capture.
        // Bits 0/7/8/10/34 advertise firmware, switches, ANC, presets and custom EQ.
        // Advertising custom EQ deliberately challenges the profile's absent declaration.
        var fake = new Fake
        {
            ProductId = ProductId,
            ReportModern = false,
            CapabilityOverride = Convert.FromHexString("810500000400000000")
        };
        fake.Features.Clear();
        fake.Features[0x06] = 0;
        fake.Features[0x11] = 0;
        fake.Features[0x18] = 1;
        fake.ReadReplies[0x0105] = [0, 2, .. Encoding.UTF8.GetBytes("1,2,150,2,2,150")];
        return fake;
    }

    internal static async Task Run(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        var bundles = new BundledResourceStore();
        await test("Air4 Pro original source profiles verify identity and preserve EQ and legacy feature declarations", async () =>
        {
            foreach (var source in Enum.GetValues<ResourceSource>())
            {
                var catalog = bundles.GetCatalog(source);
                var resource = catalog.Profiles[ProductId];
                var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                var fake = SyntheticLegacyReplies();
                await using var client = new DailyClient(fake,
                    id => id == ProductId && resource.CanUseControlTransport ? profile : null);
                var snapshot = await client.RefreshAsync();
                string context = $"Air4 Pro/{source}";
                check(profile.SupportSpp && profile.Uuid == Guid.Parse("0000079A-D102-11E1-9B23-00025B00A5A5"), context);
                check(snapshot.Connected && snapshot.ProfileVerified && snapshot.ProductId == ProductId, context);
                check(snapshot.Capabilities.Preset
                    && snapshot.Presets.Select(x => x.ProtocolId).SequenceEqual(new byte[] { 0, 1, 2 }), context);
                check(!profile.RawFunctions.TryGetProperty("customEqualizer", out _)
                    && !snapshot.Capabilities.CustomEqualizer, context);
                check(snapshot.Capabilities.GameFeatureId == 0x06 && snapshot.GameMode == false
                    && snapshot.Capabilities.GameMode == (source == ResourceSource.HeyMelody), context);
                check(snapshot.DualConnection == false && snapshot.Capabilities.DualConnection
                    && snapshot.HiRes == true && snapshot.Capabilities.HiRes, context);
                check(fake.Writes.Count == 0, context);
            }
        });
        await test("Air4 Pro unknown legacy states do not gain switch capabilities from profiles or command bits", async () =>
        {
            foreach (var source in Enum.GetValues<ResourceSource>())
            {
                var catalog = bundles.GetCatalog(source);
                var resource = catalog.Profiles[ProductId];
                var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                var fake = SyntheticLegacyReplies();
                fake.Features[0x06] = fake.Features[0x11] = fake.Features[0x18] = 3;
                await using var client = new DailyClient(fake, id => id == ProductId ? profile : null);
                var snapshot = await client.RefreshAsync();
                string context = $"Air4 Pro unknown/{source}";
                check(snapshot.Connected && snapshot.ProfileVerified && snapshot.Capabilities.Preset, context);
                check(snapshot.RawFeatures[0x06] == 3 && snapshot.RawFeatures[0x11] == 3
                    && snapshot.RawFeatures[0x18] == 3, context);
                check(snapshot.GameMode is null && snapshot.DualConnection is null && snapshot.HiRes is null, context);
                check(!snapshot.Capabilities.GameMode && !snapshot.Capabilities.DualConnection
                    && !snapshot.Capabilities.HiRes && !snapshot.Capabilities.CustomEqualizer, context);
                check(fake.Writes.Count == 0, context);
            }
        });
    }
}
