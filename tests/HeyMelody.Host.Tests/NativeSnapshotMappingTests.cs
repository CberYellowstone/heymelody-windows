using System.Reflection;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Host;

internal static class NativeSnapshotMappingTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
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
