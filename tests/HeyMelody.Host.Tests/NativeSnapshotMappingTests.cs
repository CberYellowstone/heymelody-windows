using System.Reflection;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Host;

internal static class NativeSnapshotMappingTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await NativeHearingNotificationTests.RunAsync(test, scratch);
        await test("native firmware projection uses full component display and preserves fallback without device I/O", async () =>
        {
            await using var service = new NativeHeadsetService(() => throw new InvalidOperationException("Firmware projection enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, "firmware-projection.json")));
            var map = typeof(NativeHeadsetService).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var daily = new DailySnapshot { Revision = 1, ProductId = "06C010", FirmwareVersion = "137", FirmwareDisplayVersion = "137.137.105" };
            Check(((UiSnapshot)map.Invoke(service, [daily])!).Firmware == "137.137.105" && daily.FirmwareVersion == "137", "Full display replaced the gate version");
            daily.Revision++; daily.FirmwareDisplayVersion = null;
            Check(((UiSnapshot)map.Invoke(service, [daily])!).Firmware == "137", "Raw firmware fallback was lost");
            daily.Revision++; daily.ProductId = "06EC10"; daily.FirmwareVersion = "116"; daily.FirmwareDisplayVersion = "116.116.100";
            Check(((UiSnapshot)map.Invoke(service, [daily])!).Firmware == "116.116.100", "Existing X4 component fixture was truncated");
        });
        await test("native warnings preserve notification guidance and identify failed state reads without disabling other controls", async () =>
        {
            await using var service = new NativeHeadsetService(
                () => throw new InvalidOperationException("Warning projection enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, "warning-projection.json")));
            var map = typeof(NativeHeadsetService).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var daily = new DailySnapshot
            {
                Revision = 1, Connected = true, ProfileVerified = true, GameMode = false,
                Capabilities = new() { GameMode = true },
                Warnings = ["部分状态通知不可用，仍可手动刷新状态。"],
                FeatureSections = [new("settings", "设置", false,
                    [new("game", "游戏低延迟", "switch", 0, null, [], 0, 1, true)])]
            };
            UiSnapshot Project() => (UiSnapshot)map.Invoke(service, [daily])!;
            var projected = Project();
            Check(projected.Warnings.SequenceEqual(daily.Warnings) && projected.CanSetGameMode
                && projected.FeatureSections.Single().Controls.Single().CanWrite,
                "A notification warning lost its manual-refresh guidance or disabled a verified setting");
            daily.Warnings = ["查询 0x0130 被设备拒绝。", "查询 0x0124 的布局未通过校验，状态保留未知。"];
            daily.Revision++;
            projected = Project();
            Check(projected.Warnings.SequenceEqual(new[] {
                "耳机拒绝了提示音音量读取，请手动刷新后重试。", "耳机返回的低音强度无法识别，请手动刷新后重试。" }),
                "Failed reads lost the affected setting or rejection/layout distinction");
            Check(projected.CanSetGameMode, "An unrelated failed read disabled the available game setting");
            daily.Warnings = []; daily.Revision++;
            Check(Project().Warnings.Count == 0, "A successful fresh observation retained an earlier warning");
        });
        await test("native ANC projection preserves a known primary without guessing its adjustment", async () =>
        {
            await using var service = new NativeHeadsetService(
                () => throw new InvalidOperationException("ANC projection enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, "anc-projection.json")));
            var map = typeof(NativeHeadsetService).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var daily = new DailySnapshot
            {
                Revision = 1, Connected = true, ProfileVerified = true, ProductId = "06EC10",
                Anc = DailyParsers.ParseAnc([0, 1, 1, 0, 8], X4Profile.Load()),
                Capabilities = new() { Anc = true },
                AncOptions = [new(11, 12, 11, "更少噪音"), new(11, 13, 12, "标准"), new(11, 14, 13, "更多噪音")]
            };
            UiSnapshot Project() => (UiSnapshot)map.Invoke(service, [daily])!;
            var projected = Project();
            Check(projected is { AncPrimary: 11, Anc: null, CanSetAncMode: true }
                && projected.AncModes is [{ Key: 11, Name: "自适应降噪", IconKey: "adaptive" }],
                "Known parent state or the parent label was replaced by a default adjustment");
            Check(projected.AncOptions.Select(x => x.Key).SequenceEqual(new[] { 0x0B0C, 0x0B0D, 0x0B0E }),
                "Legacy adjustment values were changed by the new parent state");
            daily.Anc = DailyParsers.ParseAnc([0, 1, 1, 0, 0x20], X4Profile.Load()); daily.Revision++;
            projected = Project();
            Check(projected is { AncPrimary: 11, Anc: 0x0B0D, CanSetAnc: true, CanSetAncMode: true },
                "The packed legacy adjustment state no longer survives projection");
            daily.AncOptions = []; daily.Revision++;
            Check(!Project().CanSetAncMode, "Missing active source modes enabled primary selection");
            daily.AncOptions = [new(11, 13, 12, "标准")]; daily.Capabilities.Anc = false; daily.Revision++;
            Check(!Project().CanSetAncMode, "Unavailable ANC capability enabled primary selection");
            daily.Capabilities.Anc = true; daily.Connected = false; daily.Revision++;
            Check(!Project().CanSetAncMode, "Disconnected observation retained primary selection");
            daily.Connected = true; daily.ProfileVerified = false; daily.Revision++;
            Check(!Project().CanSetAncMode, "Unverified profile retained primary selection");
            daily.ProfileVerified = true; daily.Error = "unavailable"; daily.Revision++;
            Check(!Project().CanSetAncMode, "Failed observation retained primary selection");
        });
        await test("native UI revisions preserve metadata enrichment and stale receipt ordering without device I/O", async () =>
        {
            await using var service = new NativeHeadsetService(
                () => throw new InvalidOperationException("Snapshot projection enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, "snapshot-mapping.json")));
            var method = typeof(NativeHeadsetService).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!;
            UiSnapshot Map(DailySnapshot daily) => (UiSnapshot)method.Invoke(service, [daily])!;
            var firstDaily = new DailySnapshot { Revision = 1, Connected = true, ProfileVerified = true, ProductId = "06EC10", GameMode = false };
            var first = Map(firstDaily);
            Check(first.HearingProfiles.Count == 0, "Initial snapshot unexpectedly contained hearing profile metadata");

            Field("_hearingProfiles").SetValue(service, new UiHearingProfile[] { new(7, "已配置听感", true, true) });
            var enriched = Map(firstDaily);
            Check(enriched.Revision > first.Revision && enriched.HearingProfiles is [{ Uid: 7 }],
                "Metadata supplement at the same Daily revision could be swallowed as an old UI update");

            var secondDaily = new DailySnapshot { Revision = 2, Connected = true, ProfileVerified = true, ProductId = "06EC10", GameMode = true };
            var second = Map(secondDaily);
            var staleReceipt = Map(firstDaily);
            Check(second.Revision > enriched.Revision && staleReceipt.Revision == enriched.Revision && staleReceipt.Revision < second.Revision,
                "An older command receipt acquired a newer UI revision");
            Check(staleReceipt.GameMode == false && ReferenceEquals(Field("_last").GetValue(service), secondDaily),
                "Mapping an older receipt replaced the latest device observation");

            for (long revision = 3; revision <= 140; revision++)
                Map(new() { Revision = revision, Connected = true, ProfileVerified = true, ProductId = "06EC10", GameMode = true });
            var cache = (Dictionary<long, long>)Field("_uiRevisions").GetValue(service)!;
            var expiredReceipt = Map(firstDaily);
            Check(cache.Count == 128 && !cache.ContainsKey(1) && expiredReceipt.Revision == 1,
                "Evicted receipts grew the revision cache or were promoted to current updates");
            Check(((DailySnapshot)Field("_last").GetValue(service)!).Revision == 140,
                "An evicted receipt replaced the latest observation");
            var diagnostics = JsonSerializer.SerializeToElement(service.Diagnostics());
            foreach (string name in new[] { "applicationGetSubmissions", "applicationSetSubmissions", "applicationOtherSubmissions" })
                Check(diagnostics.GetProperty(name).GetInt32() == 0, "Snapshot projection submitted a device command");
        });
        await test("native feature projection maps all control kinds and preserves text values and blocked descriptions", async () =>
        {
            await using var service = new NativeHeadsetService(
                () => throw new InvalidOperationException("Feature projection enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, "feature-projection.json")));
            var daily = new DailySnapshot
            {
                Revision = 1, Connected = true, ProfileVerified = true,
                FeatureSections = [new("controls", "耳机功能", true,
                [
                    new("toggle", "开关", "switch", 1, null, [], null, null, true),
                    new("gain", "增益", "slider", 3, "需关闭高清音频", [], -6, 6, false),
                    new("mode", "模式", "radio", 2, null, [new(1, "A"), new(2, "B")], null, null, true),
                    new("configured", "已配置", "combo", 7, "请先创建听感配置", [new(7, "我的听感")], null, null, false),
                    new("name", "名称", "text", null, "已保存的耳机名称", [], null, null, true)
                ])]
            };
            var map = typeof(NativeHeadsetService).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var projected = (UiSnapshot)map.Invoke(service, [daily])!;
            var section = projected.FeatureSections.Single(); var controls = section.Controls;
            Check(section.Id == "controls" && section.Header == "耳机功能" && section.DefaultExpanded,
                "Feature section metadata did not survive the bridge");
            Check(controls.Select(x => x.Kind).SequenceEqual(new[] { "Switch", "Slider", "Radio", "Combo", "Text" }),
                "Real Daily control kinds do not select their UI controls");
            Check(controls[1] is { Value: 3, Minimum: -6, Maximum: 6, CanWrite: false, Text: null, Description: "需关闭高清音频" }
                && controls[3] is { CanWrite: false, Text: null, Description: "请先创建听感配置" },
                "Blocked descriptions were put into editable text values or lost their write exclusion");
            Check(controls[4] is { Text: "已保存的耳机名称", Description: "", CanWrite: true },
                "Text input lost its saved value or interpreted it as a blocked description");
            Check(controls[2].Options!.Select(x => (x.Key, x.Name)).SequenceEqual(new[] { (1, "A"), (2, "B") })
                && controls[3].Options is [{ Key: 7, Name: "我的听感" }], "Feature option identifiers or labels changed");
            daily.Connected = false; daily.Revision++;
            projected = (UiSnapshot)map.Invoke(service, [daily])!;
            Check(projected.FeatureSections.Single().Controls.All(x => !x.CanWrite),
                "A disconnected profile retained writable controls");
        });
    }

    private static FieldInfo Field(string name) => typeof(NativeHeadsetService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
