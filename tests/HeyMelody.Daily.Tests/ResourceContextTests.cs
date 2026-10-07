using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;

internal static class ResourceContextTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("calibration transport rechecks firmware conditions while keeping explicit stop available", async () =>
        {
            var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject();
            source["minFirmVersion"] = 100;
            var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "HeyMelody");
            var fake = new Fake(); await using var client = new DailyClient(fake, _ => profile);
            await client.RefreshAsync();
            fake.ReadReplies[0x0105] = [0,3,..Encoding.UTF8.GetBytes("1,2,90,2,2,90,3,2,100")];
            await client.RefreshAsync(DailyRefreshScope.Information);
            Check(!client.CalibrationContext("device-key", 1).HearingV1 && !client.CalibrationContext("device-key", 1).HearingV2);
            try { await client.RequestAsync(0x0403, [0x0B,1], default); throw new Exception("Conditional calibration was submitted."); }
            catch (InvalidOperationException) { }
            Check(fake.Writes.Count == 0);
            await client.RequestAsync(0x0403, [0x0B,0], default);
            Check(fake.Writes.Count == 1 && fake.Writes[0].Payload.SequenceEqual(new byte[] {0x0B,0}));
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
            var fake = new Fake();
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
            Check((await oem.RefreshAsync()).Presets.Select(x => x.ProtocolId).SequenceEqual(new byte[] {9,0}));
        });
    }
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("Effective EQ context regression failed."); }
}
