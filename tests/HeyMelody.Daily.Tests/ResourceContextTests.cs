using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;

internal static class ResourceContextTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("component firmware display preserves numeric gates, order, source type and refresh unknowns", async () =>
        {
            var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject();
            source["id"] = "06C010"; source["type"] = "T1"; source["minFirmVersion"] = 130;
            var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "HeyMelody");
            var fake = new Fake { ProductId = "06C010" };
            // Synthetic Free4 Dynaudio report, not a hardware capture: include unrelated version types and shuffled components.
            fake.ReadReplies[0x0105] = [0,6,..Encoding.UTF8.GetBytes("3,2,105,1,1,9,2,2,137,1,3,99,1,2,137,3,4,999")];
            await using var client = new DailyClient(fake, _ => profile);
            var snapshot = await client.RefreshAsync();
            Check(snapshot.FirmwareVersion == "137" && snapshot.FirmwareDisplayVersion == "137.137.105"
                && VersionConditions.FromSource(JsonSerializer.SerializeToElement(new { minFirmVersion = 130 }))
                    .Evaluate(new(snapshot.FirmwareVersion)) == VersionConditionStatus.Pass);
            snapshot = await client.RefreshAsync(DailyRefreshScope.Battery);
            Check(snapshot.FirmwareDisplayVersion == "137.137.105");
            fake.ReadReplies[0x0105] = [0,2,..Encoding.UTF8.GetBytes("1,2,137,3,2,105")];
            Check((await client.RefreshAsync(DailyRefreshScope.Information)).FirmwareDisplayVersion == "137.0.105");
            fake.ReadReplies[0x0105] = [1];
            var rejected = await client.RefreshAsync(DailyRefreshScope.Information);
            Check(rejected.FirmwareDisplayVersion is null && rejected.FirmwareVersion is null && fake.Writes.Count == 0);
            await using var x4 = new DailyClient(new Fake());
            // Existing confirmed X4 fixture has three running components 116/116/100.
            var existing = await x4.RefreshAsync();
            Check(existing.FirmwareVersion == "116" && existing.FirmwareDisplayVersion == "116.116.100");
        });
        await test("single-device firmware display uses its configured format and preserves an already formatted fallback", async () =>
        {
            foreach (string type in new[] { "S", "N" })
            {
                var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject(); source["type"] = type;
                var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "memory");
                var fake = new Fake(); fake.ReadReplies[0x0105] = [0,1,..Encoding.UTF8.GetBytes("1,2,7")];
                await using var client = new DailyClient(fake, _ => profile);
                var current = await client.RefreshAsync(); Check(current.FirmwareVersion == "7" && current.FirmwareDisplayVersion == "0.0.7");
                fake.ReadReplies[0x0105] = [0,1,..Encoding.UTF8.GetBytes("1,2,1.2.3")];
                current = await client.RefreshAsync(DailyRefreshScope.Information);
                Check(current.FirmwareVersion == "1.2.3" && current.FirmwareDisplayVersion is null);
            }
        });
        await test("calibration transport reports changed firmware requirements while keeping explicit start and stop available", async () =>
        {
            var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject();
            source["minFirmVersion"] = 100;
            var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "HeyMelody");
            var fake = new Fake(); await using var client = new DailyClient(fake, _ => profile);
            await client.RefreshAsync();
            fake.ReadReplies[0x0105] = [0,3,..Encoding.UTF8.GetBytes("1,2,90,2,2,90,3,2,100")];
            var changedFirmware = await client.RefreshAsync(DailyRefreshScope.Information);
            Check(client.CalibrationContext("device-key", 1).HearingV2
                && changedFirmware.Warnings.Any(warning => warning.Contains("固件兼容提示")));
            await client.RequestAsync(0x0403, [0x0B,1], default);
            Check(fake.Writes.Count == 1 && fake.Writes[0].Payload.SequenceEqual(new byte[] {0x0B,1}));
            await client.RequestAsync(0x0403, [0x0B,0], default);
            Check(fake.Writes.Count == 2 && fake.Writes[1].Payload.SequenceEqual(new byte[] {0x0B,0}));
        });
        await test("effective EQ follows source marker firmware and verified BLE color without device writes", async () =>
        {
            var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject();
            var functions = source["function"]!.AsObject();
            functions["equalizerMode"] = JsonNode.Parse("""[{"protocolIndex":0,"modeType":42,"order":3}]""");
            functions["equalizerModeCompat"] = JsonNode.Parse("""[{"protocolIndex":9,"modeType":43,"order":1}]""");
            functions["equalizerModeByVersion"] = JsonNode.Parse("""[{"protocolIndex":5,"modeType":28,"order":0,"minAppVersion":17006003}]""");
            functions["equalizerModeByColor"] = JsonNode.Parse("""[{"protocolIndex":7,"modeType":46,"order":2,"colorId":2,"minFirmVersion":118}]""");
            var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "HeyMelody");
            var fake = new Fake { Color = null }; // the session itself reports no color here
            await using var client = new DailyClient(fake, _ => profile, 17006004);
            var initial = await client.RefreshAsync();
            Check(initial.Presets.Select(x => x.ProtocolId).SequenceEqual(new byte[] {5,9,0}));
            var rejected = await client.ApplyPresetAsync(7);
            Check(!rejected.SubmissionAttempted && fake.Writes.Count == 0);
            fake.ReadReplies[0x0105] = [0,3,..Encoding.UTF8.GetBytes("1,2,119.119.0,2,2,119.119.0,3,2,100")];
            await client.RefreshAsync(DailyRefreshScope.Information);
            int requestCount = fake.Requests.Count;
            var colored = await client.SetObservedColorAsync("06EC10", 2);
            Check(colored.Presets.Select(x => x.ProtocolId).SequenceEqual(new byte[] {5,9,7,0}));
            Check(fake.Requests.Count == requestCount && fake.Opens == 1 && fake.Writes.Count == 0);
            var wrongProduct = await client.SetObservedColorAsync("067410", 2);
            Check(wrongProduct.Presets.All(x => x.ProtocolId != 7));
            await client.SetObservedColorAsync("06EC10", 2);
            Check((await client.ApplyPresetAsync(7)).Success && fake.Writes.Count == 1);
            var other = new Fake();
            await using var oem = new DailyClient(other, _ => profile, 17006002);
            var oemState = await oem.RefreshAsync();
            Check(oemState.Presets.Select(x => x.ProtocolId).SequenceEqual(new byte[] {9,7,0})
                && oemState.Warnings.Any(warning => warning.Contains("固件兼容提示")));
        });
        await test("the session color selects color-conditioned presets without a broadcast", async () =>
        {
            var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject();
            var functions = source["function"]!.AsObject();
            functions["equalizerMode"] = JsonNode.Parse("""[{"protocolIndex":0,"modeType":42,"order":3}]""");
            functions["equalizerModeCompat"] = JsonNode.Parse("[]");
            functions["equalizerModeByVersion"] = JsonNode.Parse("[]");
            functions["equalizerModeByColor"] = JsonNode.Parse("""[{"protocolIndex":7,"modeType":46,"order":2,"colorId":2}]""");
            var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "HeyMelody");
            var fake = new Fake { Color = 2 }; await using var client = new DailyClient(fake, _ => profile, 17006004);
            var snapshot = await client.RefreshAsync();
            Check(snapshot.Color == 2 && snapshot.Presets.Any(x => x.ProtocolId == 7) && fake.Writes.Count == 0);
            var other = new Fake { Color = 1 }; await using var different = new DailyClient(other, _ => profile, 17006004);
            Check((await different.RefreshAsync()).Presets.All(x => x.ProtocolId != 7));
        });
    }
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("Effective EQ context regression failed."); }
}
