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
        // Bits 0/7/8/10/17/34 advertise firmware, switches, ANC, presets, codec and custom EQ.
        // Advertising custom EQ deliberately challenges the profile's absent declaration.
        var fake = new Fake
        {
            ProductId = ProductId,
            ReportModern = false,
            CapabilityOverride = Convert.FromHexString("810502000400000000")
        };
        fake.Features.Clear();
        fake.Features[0x06] = 0;
        fake.Features[0x11] = 0;
        fake.Features[0x18] = 1;
        fake.ReadReplies[0x0105] = [0, 2, .. Encoding.UTF8.GetBytes("1,2,150,2,2,150")];
        return fake;
    }

    private static byte[] Firmware(params string[] entries) =>
        [0, checked((byte)entries.Length), .. Encoding.UTF8.GetBytes(string.Join(',', entries))];

    private static Fake SyntheticV125Replies()
    {
        var fake = SyntheticLegacyReplies();
        fake.ReadReplies[0x0105] = Firmware("1,2,125", "2,2,125", "3,2,105");
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
                check(snapshot.CodecRaw == 8 && fake.Requests.Count(x => x.Command == 0x0114) == 1, context);
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
        await test("Air4 Pro advertised codec reads flagged wire ACK with notifications before and after without SET", async () =>
        {
            foreach (var source in Enum.GetValues<ResourceSource>())
            foreach (int chunkSize in new[] { 1, 4096 })
            {
                var catalog = bundles.GetCatalog(source);
                var resource = catalog.Profiles[ProductId];
                var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                var fake = SyntheticLegacyReplies();
                var connector = new CodecWireFixtureConnector(fake, sequence =>
                    [.. CodecWireFixtureConnector.PresetNotification(sequence), .. CodecWireFixtureConnector.Ack(sequence),
                        .. CodecWireFixtureConnector.PresetNotification(sequence)], chunkSize);
                await using var client = new DailyClient(connector, id => id == ProductId ? profile : null);
                var snapshot = await client.RefreshAsync();
                string context = $"Air4 Pro flagged wire/{source}/{chunkSize}";
                check(snapshot.Connected && snapshot.ProfileVerified && snapshot.Error is null && snapshot.CodecRaw == 8, context);
                check(snapshot.Capabilities.Preset && fake.Requests.Count(x => x.Command == 0x0114) == 1, context);
                check(fake.Opens == 1 && fake.MaximumReaders == 1 && fake.Writes.Count == 0, context);
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
        await test("Air4 Pro v125 reconstructed codec ACK uses current ear firmware and preserves ACK notification routing", async () =>
        {
            foreach (var source in Enum.GetValues<ResourceSource>())
            foreach (byte codec in new byte[] { 1, 2, 8 })
            foreach (int chunkSize in new[] { 1, 4096 })
            {
                var catalog = bundles.GetCatalog(source);
                var resource = catalog.Profiles[ProductId];
                var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                var fake = SyntheticV125Replies();
                var connector = new CodecWireFixtureConnector(fake, sequence =>
                    [.. CodecWireFixtureConnector.PresetNotification(sequence),
                        .. CodecWireFixtureConnector.Air4ProV125Ack(sequence, codec),
                        .. CodecWireFixtureConnector.PresetNotification(sequence)], chunkSize);
                await using var client = new DailyClient(connector, id => id == ProductId ? profile : null);
                var snapshot = await client.RefreshAsync();
                string context = $"Air4 Pro v125 wire/{source}/{codec}/{chunkSize}";
                check(snapshot.Connected && snapshot.ProfileVerified && snapshot.Error is null
                    && snapshot.FirmwareVersion == "125" && snapshot.CodecRaw == codec, context);
                check(snapshot.Capabilities.Preset && fake.Opens == 1 && fake.MaximumReaders == 1
                    && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 0, context);
            }
        });
        await test("Air4 Pro codec exception rejects mixed missing unknown and non-running ear firmware", async () =>
        {
            var resource = bundles.GetCatalog(ResourceSource.HeyMelody).Profiles[ProductId];
            var profile = DeviceProfile.FromResource(resource.Raw, ResourceSource.HeyMelody.ToString());
            foreach (var firmware in new (string Label, byte[] Reply)[]
            {
                ("mixed ears", Firmware("1,2,125", "2,2,150")),
                ("other firmware", Firmware("1,2,150", "2,2,150")),
                ("case only", Firmware("3,2,125")),
                ("all ears absent", Firmware("1,2,000", "2,2,000", "3,2,125")),
                ("non-sentinel zero", Firmware("1,2,125", "2,2,0")),
                ("non-sentinel short zero", Firmware("1,2,125", "2,2,00")),
                ("ear version unknown", Firmware("1,2,unknown", "2,2,125")),
                ("hardware version only", Firmware("1,1,125")),
                ("no versions", [0, 0]), ("rejected", [1]), ("malformed", [0, 1, 0])
            })
            {
                var fake = SyntheticV125Replies(); fake.ReadReplies[0x0105] = firmware.Reply;
                var connector = new CodecWireFixtureConnector(fake, sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence));
                await using var client = new DailyClient(connector, id => id == ProductId ? profile : null);
                var snapshot = await client.RefreshAsync();
                check(!snapshot.Connected && snapshot.Error?.Contains("0x0114") == true
                    && snapshot.Error.Contains("消息长度不匹配") && fake.Opens == 2 && fake.Writes.Count == 0, firmware.Label);
            }
            var noFirmware = SyntheticV125Replies(); noFirmware.CapabilityOverride![0] &= 0xFE;
            await using var noFirmwareClient = new DailyClient(new CodecWireFixtureConnector(noFirmware,
                sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence)), id => id == ProductId ? profile : null);
            var unavailable = await noFirmwareClient.RefreshAsync();
            check(!unavailable.Connected && noFirmware.Requests.All(x => x.Command != 0x0105)
                && noFirmware.Writes.Count == 0, "firmware query not advertised");
        });
        await test("Air4 Pro codec exception rejects another model and unmatched notification or ACK bytes", async () =>
        {
            var otherModel = new Fake(); otherModel.ReadReplies[0x0105] = Firmware("1,2,125", "2,2,125");
            await using (var client = new DailyClient(new CodecWireFixtureConnector(otherModel,
                sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence))))
            {
                var snapshot = await client.RefreshAsync();
                check(!snapshot.Connected && snapshot.Error?.Contains("0x0114") == true
                    && otherModel.Opens == 2 && otherModel.Writes.Count == 0, "other model");
            }
            var resource = bundles.GetCatalog(ResourceSource.HeyMelody).Profiles[ProductId];
            var profile = DeviceProfile.FromResource(resource.Raw, ResourceSource.HeyMelody.ToString());
            foreach (var change in new (string Label, int Offset, byte Value)[]
            {
                ("old sequence", 6, 0xFE), ("notification command", 5, 0x05),
                ("nonzero status", 9, 1), ("unknown codec", 10, 3), ("nonzero codec high byte", 11, 1)
            })
            {
                var fake = SyntheticV125Replies();
                var connector = new CodecWireFixtureConnector(fake, sequence =>
                {
                    byte[] wire = CodecWireFixtureConnector.Air4ProV125Ack(sequence);
                    wire[change.Offset] = change.Value; return wire;
                });
                await using var client = new DailyClient(connector, id => id == ProductId ? profile : null);
                var snapshot = await client.RefreshAsync();
                check(!snapshot.Connected && snapshot.Error?.Contains("0x0114") == true
                    && snapshot.Error.Contains("消息长度不匹配") && fake.Writes.Count == 0, change.Label);
            }
        });
        await test("Current firmware permission is cleared before a refresh and cannot reuse an earlier v125 result", async () =>
        {
            var resource = bundles.GetCatalog(ResourceSource.HeyMelody).Profiles[ProductId];
            var profile = DeviceProfile.FromResource(resource.Raw, ResourceSource.HeyMelody.ToString());
            foreach (byte[] laterReply in new byte[][]
            {
                Firmware("1,2,150", "2,2,150"), [1], [0, 1, 0]
            })
            {
                var fake = SyntheticV125Replies();
                await using var client = new DailyClient(new CodecWireFixtureConnector(fake,
                    sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence)), id => id == ProductId ? profile : null);
                check((await client.RefreshAsync()).Connected, "initial v125");
                fake.ReadReplies[0x0105] = laterReply;
                var snapshot = await client.RefreshAsync(DailyRefreshScope.Information);
                check(!snapshot.Connected && snapshot.Error?.Contains("0x0114") == true
                    && fake.Opens == 2 && fake.Requests.Count(x => x.Command == 0x0114) == 3
                    && fake.Writes.Count == 0, "stale v125 permission");
            }
        });
        await test("A reported right ear running v125 can authorize the codec exception independently of the display field", async () =>
        {
            var resource = bundles.GetCatalog(ResourceSource.HeyMelody).Profiles[ProductId];
            var profile = DeviceProfile.FromResource(resource.Raw, ResourceSource.HeyMelody.ToString());
            var fake = SyntheticV125Replies(); fake.ReadReplies[0x0105] = Firmware("2,2,125", "3,2,105");
            await using var client = new DailyClient(new CodecWireFixtureConnector(fake,
                sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence)), id => id == ProductId ? profile : null);
            var snapshot = await client.RefreshAsync();
            check(snapshot.Connected && snapshot.FirmwareVersion is null && snapshot.CodecRaw == 8
                && fake.Opens == 1 && fake.Writes.Count == 0, "right running firmware only");
        });
        await test("Reconstructed v125 firmware triplets authorize either local ear while an absent peer remains 000", async () =>
        {
            // Nine triplets reconstructed from the GET 0x0105 ARM formatter.
            // These are source-derived fixtures, not hardware captures.
            var firmwareReplies = new[]
            {
                Firmware("1,1,111", "1,2,125", "1,3,125", "2,1,111", "2,2,000", "2,3,000",
                    "3,1,111", "3,2,000", "3,3,000"),
                Firmware("1,1,111", "1,2,000", "1,3,000", "2,1,111", "2,2,125", "2,3,125",
                    "3,1,111", "3,2,000", "3,3,000")
            };
            foreach (var source in Enum.GetValues<ResourceSource>())
            foreach (byte[] reply in firmwareReplies)
            {
                var catalog = bundles.GetCatalog(source);
                var resource = catalog.Profiles[ProductId];
                var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                var fake = SyntheticV125Replies(); fake.ReadReplies[0x0105] = reply;
                await using var client = new DailyClient(new CodecWireFixtureConnector(fake,
                    sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence)), id => id == ProductId ? profile : null);
                var snapshot = await client.RefreshAsync();
                check(snapshot.Connected && snapshot.ProfileVerified && snapshot.CodecRaw == 8
                    && fake.Opens == 1 && fake.Requests.Count(x => x.Command == 0x0114) == 1
                    && fake.Writes.Count == 0, $"reconstructed firmware local ear/{source}");
            }
        });
        await test("A completed codec reply cannot authorize another malformed ACK while the request key still exists", async () =>
        {
            var resource = bundles.GetCatalog(ResourceSource.HeyMelody).Profiles[ProductId];
            var profile = DeviceProfile.FromResource(resource.Raw, ResourceSource.HeyMelody.ToString());
            var fake = SyntheticV125Replies();
            var connector = new CodecWireFixtureConnector(fake, sequence =>
                [.. CodecWireFixtureConnector.Air4ProV125Ack(sequence), .. CodecWireFixtureConnector.Air4ProV125Ack(sequence)],
                chunkSize: 12, holdCodecSend: true);
            await using var client = new DailyClient(connector, id => id == ProductId ? profile : null);
            var snapshot = await client.RefreshAsync();
            check(!snapshot.Connected && snapshot.Error?.Contains("0x0114") == true
                && snapshot.Error.Contains("消息长度不匹配") && fake.Opens == 2 && fake.Writes.Count == 0,
                "completed reply is not pending");
        });
        await test("A silent current firmware query ends the session without reusing earlier codec permission", async () =>
        {
            var resource = bundles.GetCatalog(ResourceSource.HeyMelody).Profiles[ProductId];
            var profile = DeviceProfile.FromResource(resource.Raw, ResourceSource.HeyMelody.ToString());
            var fake = SyntheticV125Replies();
            await using var client = new DailyClient(new CodecWireFixtureConnector(fake,
                sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence)), id => id == ProductId ? profile : null);
            check((await client.RefreshAsync()).Connected, "initial v125 before silent firmware");
            fake.Silent.Add(0x0105);
            var snapshot = await client.RefreshAsync(DailyRefreshScope.Information);
            check(!snapshot.Connected && snapshot.Error?.Contains("0x0105") == true
                && snapshot.Error.Contains("等待回执超时") && fake.Opens == 2
                && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 0,
                "silent current firmware");
        });
    }
}
