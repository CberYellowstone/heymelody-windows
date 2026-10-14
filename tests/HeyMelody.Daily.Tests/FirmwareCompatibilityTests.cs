using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;

internal static class FirmwareCompatibilityTests
{
    private static JsonObject Source() => JsonNode.Parse("""
        {"id":"06EC10","name":"Firmware compatibility fixture","supportSpp":true,
         "uuid":"00001101-0000-1000-8000-00805f9b34fb","function":{
         "wearDetection":1,"equalizer":1,"fitDetection":1,"hearingEnhancement":1,"earScan":1,
         "equalizerMode":[{"protocolIndex":0,"modeType":1},{"protocolIndex":1,"modeType":2}],
         "noiseReductionMode":[
          {"protocolIndex":0,"modeType":1,"childrenMode":[{"protocolIndex":3,"modeType":1}]},
          {"protocolIndex":1,"modeType":5,"childrenMode":[{"protocolIndex":4,"modeType":4},{"protocolIndex":7,"modeType":7}]},
          {"protocolIndex":2,"modeType":2,"childrenMode":[{"protocolIndex":8,"modeType":2}]}]}}
        """)!.AsObject();
    private static DeviceProfile Profile(Action<JsonObject>? change = null)
    {
        var source = Source(); change?.Invoke(source);
        return DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "firmware-compatibility-fixture");
    }
    private static byte[] Firmware(string version) => [0, 3, .. Encoding.UTF8.GetBytes($"1,2,{version},2,2,{version},3,2,101")];
    private static Fake Replies(string firmware = "115")
    {
        var fake = new Fake(); fake.Features[4] = 0; fake.ReadReplies[0x0105] = Firmware(firmware); return fake;
    }
    private static DailyFeatureControl Wear(DailySnapshot snapshot) => snapshot.FeatureSections.SelectMany(x => x.Controls).Single(x => x.Id == "wear");
    private static string[] CompatibilityWarnings(DailySnapshot snapshot) => snapshot.Warnings.Where(x => x.StartsWith("固件兼容提示：", StringComparison.Ordinal)).ToArray();
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("low firmware keeps raw evidence and permits supported observed daily controls and calibration", async () =>
        {
            var profile = Profile(source => source["minFirmVersion"] = 999); var fake = Replies();
            await using var client = new DailyClient(fake, _ => profile);
            var state = await client.RefreshAsync();
            Check(profile.Conditions.Evaluate(new("115", 17006004)) == VersionConditionStatus.Fail, "Raw firmware evidence was rewritten.");
            Check(state.Connected && state.ProfileVerified && state.Error is null && Wear(state).CanWrite
                && state.Capabilities.Anc && state.Capabilities.Preset, "Firmware-only requirements disabled observed controls.");
            var warnings = CompatibilityWarnings(state);
            Check(warnings.Length == 1 && warnings[0].Contains("999") && warnings[0].Contains("115")
                && warnings[0].Contains("佩戴贴合度测试") && warnings[0].Contains("黄金听感测试")
                && !warnings[0].Contains("已启用"), "Firmware diagnostics lost requirements, observations or affected workflows.");
            var calibration = client.CalibrationContext("fixture", 1);
            Check(calibration.Fit && calibration.HearingV2 && calibration.EarScan, "Firmware-only requirements disabled calibration declarations.");
            Check((await client.ApplyFeatureAsync("wear", 1)).Success, "Observed wear switch was blocked by low firmware.");
            Check((await client.ApplyAncModeAsync(1)).Success && (await client.ApplyAncAsync(1, 4)).Success,
                "Observed ANC parent or child was blocked by low firmware.");
            Check((await client.ApplyPresetAsync(1)).Success, "Observed preset was blocked by low firmware.");
            await client.RequestAsync(0x0405, [1], default);
            Check(fake.Writes.Count == 5 && fake.Writes.Last().Command == 0x0405, "Explicit calibration request was not submitted once.");
        });
        await test("ANC parent child and selected EQ requirements are warning-only and deduplicated", async () =>
        {
            var profile = Profile(source =>
            {
                source["function"]!["noiseReductionMode"]![1]!["minFirmVersion"] = 999;
                source["function"]!["noiseReductionMode"]![1]!["childrenMode"]![0]!["minFirmVersion"] = 999;
                source["function"]!["equalizerMode"]![1]!["minFirmwareVersion"] = 999;
            });
            var fake = Replies(); await using var client = new DailyClient(fake, _ => profile);
            var state = await client.RefreshAsync();
            Check(state.AncOptions.Any(x => x.Primary == 1 && x.Level == 4) && state.Presets.Any(x => x.ProtocolId == 1), "Firmware-only options were filtered out.");
            var warnings = CompatibilityWarnings(state);
            Check(warnings.Length == 1 && warnings[0].Contains("降噪模式") && warnings[0].Contains("深度降噪")
                && warnings[0].Contains("调音预设 1"), "Identical firmware issues were not grouped by affected controls.");
            Check((await client.ApplyAncAsync(1, 4)).Success && (await client.ApplyPresetAsync(1)).Success && fake.Writes.Count == 2,
                "Per-option firmware requirements still blocked explicit settings.");
        });
        await test("App requirements still filter ANC and EQ options despite their firmware diagnostics", async () =>
        {
            var profile = Profile(source =>
            {
                source["function"]!["noiseReductionMode"]![1]!["minAppVersion"] = 17006005;
                source["function"]!["noiseReductionMode"]![1]!["minFirmVersion"] = 999;
                source["function"]!["equalizerMode"]!.AsArray().RemoveAt(1);
                source["function"]!["equalizerModeByVersion"] = JsonNode.Parse("""
                    [{"protocolIndex":1,"modeType":2,"minAppVersion":17006005,"minFirmwareVersion":999}]
                    """);
            });
            var fake = Replies(); await using var client = new DailyClient(fake, _ => profile);
            var state = await client.RefreshAsync();
            Check(state.ProfileVerified && !state.AncOptions.Any(x => x.Primary == 1) && !state.Presets.Any(x => x.ProtocolId == 1), "App requirements were relaxed with firmware requirements.");
            Check(!(await client.ApplyAncModeAsync(1)).SubmissionAttempted && !(await client.ApplyPresetAsync(1)).SubmissionAttempted
                && fake.Writes.Count == 0, "App-inapplicable option reached SET.");
        });
        await test("unknown firmware emits conditional facts while unknown states and command absence remain hard gates", async () =>
        {
            var profile = Profile(source => source["minFirmwareVersion"] = "999");
            var fake = Replies("unknown"); fake.Features[4] = 0x7E;
            fake.ReadReplies[0x010C] = [0, 1, 1, 5]; fake.ReadReplies[0x010F] = [0];
            await using var client = new DailyClient(fake, _ => profile); var state = await client.RefreshAsync();
            Check(state.ProfileVerified && state.Error is null && !Wear(state).CanWrite && !state.Capabilities.Anc && !state.Capabilities.Preset,
                "Unknown state became permission after firmware relaxation.");
            Check(CompatibilityWarnings(state).Single().Contains("无法核对") && CompatibilityWarnings(state).Single().Contains("unknown"),
                "Conditional firmware requirement was reported as known incompatible firmware.");
            Check(!(await client.ApplyFeatureAsync("wear", 1)).SubmissionAttempted && !(await client.ApplyAncModeAsync(1)).SubmissionAttempted
                && !(await client.ApplyPresetAsync(1)).SubmissionAttempted && fake.Writes.Count == 0, "Unknown state triggered SET.");
            var noCommands = Replies(); noCommands.CapabilityOverride = new byte[9];
            await using var unavailable = new DailyClient(noCommands, _ => profile); state = await unavailable.RefreshAsync();
            Check(!Wear(state).CanWrite && !(await unavailable.ApplyFeatureAsync("wear", 1)).SubmissionAttempted
                && noCommands.Writes.Count == 0, "Missing command bitmap was bypassed.");
        });
        await test("profile App conditions still block calibration while firmware-only conditions do not", async () =>
        {
            var profile = Profile(source => { source["minAppVersion"] = 17006005; source["minFirmVersion"] = 999; });
            var fake = Replies(); await using var client = new DailyClient(fake, _ => profile); var state = await client.RefreshAsync();
            var context = client.CalibrationContext("fixture", 1);
            Check(!state.ProfileVerified && state.Error is not null && !context.Fit && !context.HearingV2, "App-inapplicable calibration context was enabled.");
            bool rejected = false;
            try { await client.RequestAsync(0x0405, [1], default); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && fake.Writes.Count == 0, "Calibration ignored missing verified identity or App requirements.");
        });
        await test("fresh and scoped firmware diagnostics rebuild without sticky or duplicate warnings", async () =>
        {
            var profile = Profile(source => source["minFirmVersion"] = 999); var fake = Replies();
            await using var client = new DailyClient(fake, _ => profile); var low = await client.RefreshAsync();
            Check(CompatibilityWarnings(low).Length == 1, "Missing initial low-firmware warning.");
            var scoped = await client.RefreshAsync(DailyRefreshScope.Anc);
            Check(CompatibilityWarnings(scoped).SequenceEqual(CompatibilityWarnings(low)), "Scoped read lost or duplicated current firmware diagnostics.");
            fake.ReadReplies[0x0105] = Firmware("1000"); var recovered = await client.RefreshAsync(DailyRefreshScope.Information);
            Check(CompatibilityWarnings(recovered).Length == 0 && Wear(recovered).CanWrite, "Successful fresh firmware read retained an old warning.");
            Check(fake.Writes.Count == 0, "Firmware reevaluation submitted a setting.");
        });
        await test("unimplemented configured personal noise does not claim firmware compatibility support", async () =>
        {
            var profile = Profile(source => source["function"]!["personalNoise"] = new JsonObject { ["minFirmVersion"] = 999 });
            var fake = Replies(); await using var client = new DailyClient(fake, _ => profile); var state = await client.RefreshAsync();
            Check(CompatibilityWarnings(state).Length == 0 && fake.Writes.Count == 0, "Unimplemented private noise configuration gained a warning or a setting.");
        });
        await test("observed colour rebuilds applicable EQ firmware diagnostics without queries settings or duplication", async () =>
        {
            var profile = Profile(source => source["function"]!["equalizerModeByColor"] = JsonNode.Parse("""
                [{"protocolIndex":7,"modeType":46,"colorId":7,"minFirmwareVersion":999}]
                """));
            var fake = Replies(); fake.Color = null;
            await using var client = new DailyClient(fake, _ => profile); var initial = await client.RefreshAsync();
            Check(!initial.Presets.Any(preset => preset.ProtocolId == 7) && CompatibilityWarnings(initial).Length == 0,
                "Unknown colour selected an inapplicable preset or firmware requirement.");
            int requests = fake.Requests.Count;
            var coloured = await client.SetObservedColorAsync("06ec10", 7);
            Check(coloured.Presets.Any(preset => preset.ProtocolId == 7) && CompatibilityWarnings(coloured).Single().Contains("调音预设 7"),
                "Verified colour did not add the applicable firmware-only EQ option and warning.");
            var repeated = await client.SetObservedColorAsync("06ec10", 7);
            Check(CompatibilityWarnings(repeated).SequenceEqual(CompatibilityWarnings(coloured)), "Repeated colour update duplicated firmware diagnostics.");
            var changed = await client.SetObservedColorAsync("06EC10", 8);
            Check(!changed.Presets.Any(preset => preset.ProtocolId == 7) && CompatibilityWarnings(changed).Length == 0,
                "Changed colour retained an inapplicable preset or obsolete firmware requirement.");
            Check(fake.Requests.Count == requests && fake.Writes.Count == 0, "Colour diagnostics queried or wrote the headset.");
        });
        await test("local game and native gesture firmware requirements warn without bypassing App or supported choices", async () =>
        {
            DeviceProfile Local(bool appGate) => Profile(source =>
            {
                source["function"]!["gameSoundList"] = JsonNode.Parse("""
                    [{"type":0},{"type":3,"minFirmVersion":999}]
                    """);
                source["function"]!["control"] = JsonNode.Parse("""
                    [{"ear":2,"action":17,"button":0,"support":68,"minFirmVersion":999}]
                    """);
                if (appGate)
                {
                    source["function"]!["gameSoundList"]![1]!["minAppVersion"] = 17006005;
                    source["function"]!["control"]![0]!["minAppVersion"] = 17006005;
                }
            });
            var fake = Replies(); fake.Features[0x27] = 0;
            await using var client = new DailyClient(fake, _ => Local(false)); var state = await client.RefreshAsync();
            var warnings = CompatibilityWarnings(state);
            Check(warnings.Length == 1 && warnings[0].Contains("游戏音效") && warnings[0].Contains("耳机操控"),
                "Local game/gesture firmware issues were not collected and grouped.");
            Check((await client.ApplyFeatureAsync("game-sound", 3)).Success
                && (await client.ApplyFeatureAsync("gesture:1:0:2", 1)).Success
                && fake.Writes.Count == 2 && fake.Writes[1].Payload.SequenceEqual(new byte[] {1,1,0,2,1}),
                "Local firmware conditions disabled supported native choices or changed their wire payload.");
            Check(!(await client.ApplyFeatureAsync("game-sound", 7)).SubmissionAttempted
                && !(await client.ApplyFeatureAsync("gesture:1:0:2", 99)).SubmissionAttempted && fake.Writes.Count == 2,
                "Firmware relaxation authorized undeclared game/gesture values.");
            var gated = Replies(); gated.Features[0x27] = 0;
            await using var blocked = new DailyClient(gated, _ => Local(true)); state = await blocked.RefreshAsync();
            Check(CompatibilityWarnings(state).Length == 0
                && !(await blocked.ApplyFeatureAsync("game-sound", 3)).SubmissionAttempted
                && !(await blocked.ApplyFeatureAsync("gesture:1:0:2", 1)).SubmissionAttempted && gated.Writes.Count == 0,
                "Local App conditions were relaxed or irrelevant firmware diagnostics were emitted.");
        });
        await test("legacy OnePlus native gesture mask and malformed firmware diagnostics retain bounded facts", async () =>
        {
            var legacy = Profile(source =>
            {
                source["name"] = "OnePlus Buds Z";
                source["function"]!["control"] = JsonNode.Parse("""
                    [{"ear":2,"action":17,"button":0,"support":64,"minFirmVersion":999}]
                    """);
            });
            var fake = Replies(); await using var client = new DailyClient(fake, _ => legacy);
            var state = await client.RefreshAsync();
            Check(CompatibilityWarnings(state).Single().Contains("耳机操控")
                && (await client.ApplyFeatureAsync("gesture:1:0:2", 5)).Success
                && fake.Writes.Single().Payload.SequenceEqual(new byte[] {1,1,0,2,5}),
                "Legacy previous-track mask lost its firmware warning or exact native route.");
            foreach (JsonNode minimum in new JsonNode[]
                { new JsonObject { ["privatePayload"] = new string('x', 2000) }, JsonValue.Create(new string('9', 120))! })
            {
                var malformed = Profile(source => source["minFirmwareVersion"] = minimum.DeepClone());
                await using var diagnostic = new DailyClient(Replies(), _ => malformed); state = await diagnostic.RefreshAsync();
                string warning = CompatibilityWarnings(state).Single();
                Check(state.ProfileVerified && Wear(state).CanWrite && warning.Contains("无法核对")
                    && !warning.Contains("privatePayload") && !warning.Contains(new string('9', 81))
                    && !warning.Contains(new string('x', 81)), "Firmware warning dumped malformed resource contents or changed control permission.");
            }
        });
    }
}
