using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Protocol;
using HeyMelodyWindows.Resources;

internal static class AncPresentationTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        var bundles = new BundledResourceStore();
        await test("X2 and X3 transparency metadata and exact child writes follow each configured source", async () =>
        {
            foreach (var source in Enum.GetValues<ResourceSource>())
            foreach (string productId in new[] { "063C10", "067410" })
            {
                var catalog = bundles.GetCatalog(source);
                var profile = DeviceProfile.FromResource(catalog.Find(productId)!.Raw, source.ToString());
                var connector = new Connector(productId, 8);
                await using var client = new DailyClient(connector, _ => profile);
                var daily = await client.RefreshAsync();
                var modes = await Project(catalog, daily, scratch);
                var transparent = modes.Single(mode => mode.Key == 2);
                Check(transparent.Name == "通透" && transparent.IconKey == "transparency"
                    && transparent.Options.Select(option => option.Name).SequenceEqual(new[] { "通透", "强化人声" })
                    && transparent.Toggle == new UiAncToggle(0x0208, 0x0209, "强化人声", "弱化环境声的同时突出人声"),
                    "Source-backed transparency semantics were inferred from a display name or protocol index");
                foreach (var (level, golden) in new (byte, string)[] { (9, "01010002"), (8, "01010001") })
                {
                    var result = await client.ApplyAncAsync(2, level);
                    Check(result.Success && result.AckAccepted == true && result.ReadbackMatches == true
                        && result.After?.Anc is { Primary: 2 } observed && observed.Level == level
                        && Convert.ToHexString(connector.Writes.Last().Payload) == golden,
                        "Transparency adjustment lost its exact payload or independently reported child");
                }
                connector.ReportAfterSet = 2;
                var parentOnly = await client.ApplyAncAsync(2, 9);
                Check(!parentOnly.Success && parentOnly.AckAccepted == true && parentOnly.ReadbackMatches is null
                    && parentOnly.FailureKind == DailyApplyFailure.ReadbackUnavailable,
                    "A parent-only readback was reported as a confirmed reinforcement setting");
            }
        });
        await test("standalone listening and adaptive modes retain their own descriptions without a transparency toggle", async () =>
        {
            foreach (var source in Enum.GetValues<ResourceSource>())
            foreach (var (productId, key, name) in new[]
            {
                ("062010", 3, "聆听"), ("064010", 3, "聆听"), ("063810", 3, "聆听"), ("065010", 3, "聆听"),
                ("06EC10", 11, "自适应"), ("067814", 11, "自适应"), ("06BC10", 11, "自适应"),
                ("068C10", 11, "自适应"), ("06C010", 11, "自适应"), ("065414", 11, "自适应"),
                ("061410", 2, "弱降噪"), ("061410", 3, "强降噪"), ("060C14", 4, "智能降噪"), ("06F010", 0, "降噪")
            })
            {
                var catalog = bundles.GetCatalog(source);
                var profile = DeviceProfile.FromResource(catalog.Find(productId)!.Raw, source.ToString());
                await using var client = new DailyClient(new Connector(productId, (byte)key), _ => profile);
                var daily = await client.RefreshAsync();
                var mode = (await Project(catalog, daily, scratch)).Single(mode => mode.Key == key);
                Check(mode.Name == name && mode.Toggle is null && mode.Description == (name switch
                {
                    "聆听" => "减弱周围噪音基础上保留人声/广播声",
                    "自适应" => "实时融合主动降噪与通透模式，根据所处环境噪声的变化，控制耳机中的噪声水平",
                    _ => ""
                }),
                    "Standalone listening or adaptive mode was reinterpreted as an on/off feature");
                if (name == "聆听") Check(mode.IconKey == "transparency" && mode.Options.Single().Name == "聆听",
                    "Listening retained an ANC icon or generic level label");
                if (mode.Options.Count == 3) Check(mode.Options.Select(option => option.Name)
                    .SequenceEqual(new[] { "更少噪音", "标准", "更多噪音" }), "Adaptive weights changed their meaning");
            }
        });
        await test("transparency presentation uses configured IDs and falls back for filtered or unfamiliar structures", async () =>
        {
            var raw = JsonNode.Parse(bundles.GetCatalog(ResourceSource.HeyMelody).Find("067410")!.Raw.GetRawText())!.AsObject();
            var modes = raw["function"]!["noiseReductionMode"]!.AsArray();
            var parent = modes.Single(mode => mode!["modeType"]!.GetValue<int>() == 2)!;
            parent["protocolIndex"] = 11;
            var children = parent["childrenMode"]!.AsArray(); children[0]!["protocolIndex"] = 22; children[1]!["protocolIndex"] = 23;
            var daily = new DailySnapshot { ProductId = "067410", AncOptions = [new(11, 22, 2, "通透"), new(11, 23, 6, "强化人声")] };
            async Task<UiAncMode> Mode() => (await Project(Catalog(raw), daily, scratch)).Single();
            Check((await Mode()).Toggle is { OffKey: 0x0B16, OnKey: 0x0B17 }, "Toggle used fixed X2/X3 protocol IDs");
            daily.AncOptions.RemoveAt(1);
            Check((await Mode()).Toggle is null, "A condition-filtered child was silently restored to the toggle");
            daily.AncOptions.Add(new(11, 23, 6, "强化人声"));
            children[1]!["modeType"] = 99;
            Check((await Mode()).Toggle is null, "An unfamiliar child was inferred to be reinforcement");
            children[1]!["modeType"] = 6;
            var first = children[0]!.DeepClone(); children[0] = children[1]!.DeepClone(); children[1] = first;
            Check((await Mode()).Toggle is null, "A reversed source configuration was assigned the old switch direction");
            children[0] = first.DeepClone(); children[1]!["modeType"] = 6; children[1]!["protocolIndex"] = 23;
            parent["modeType"] = 5;
            Check((await Mode()).Toggle is null, "A different primary mode was treated as transparency");
            parent["modeType"] = 2; children.Add(new JsonObject { ["modeType"] = 2, ["protocolIndex"] = 24 });
            Check((await Mode()).Toggle is null, "A multi-adjustment mode was collapsed into a boolean");
            foreach (var source in Enum.GetValues<ResourceSource>())
            foreach (string productId in new[] { "06BC10", "066810" })
            {
                var catalog = bundles.GetCatalog(source);
                var profile = DeviceProfile.FromResource(catalog.Find(productId)!.Raw, source.ToString());
                await using var client = new DailyClient(new Connector(productId, 8), _ => profile);
                Check((await Project(catalog, await client.RefreshAsync(), scratch)).Single(mode => mode.Key == 2).Toggle is null,
                    "A model with only ordinary transparency received reinforcement controls");
            }
        });
        await test("ANC presentation additions roundtrip and older snapshots keep conservative defaults", async () =>
        {
            var mode = Transparent();
            var roundtrip = JsonSerializer.Deserialize<UiAncMode>(JsonSerializer.Serialize(mode, RpcWire.Json), RpcWire.Json)!;
            Check(roundtrip.Description == mode.Description && roundtrip.Toggle == mode.Toggle
                && roundtrip.Options.SequenceEqual(mode.Options), "Additive ANC metadata was lost over IPC");
            var old = JsonSerializer.Deserialize<UiAncMode>("{\"key\":2,\"name\":\"通透\",\"iconKey\":\"transparency\",\"options\":[{\"key\":520,\"name\":\"通透\"}]}", RpcWire.Json)!;
            Check(old.Description == "" && old.Toggle is null, "Old JSON invented feature semantics");
            string key = DeviceWhitelistStore.KeyFor(123);
            var path = Path.Combine(scratch, "anc-presentation-state.json");
            var store = new DeviceStateStore(path);
            var presentation = new DevicePresentation { ProductId = "067410", Firmware = "preserved", SampleTime = DateTimeOffset.UtcNow,
                AncPrimary = 2, Anc = 0x0209, AncModes = [mode], AncOptions = mode.Options.ToArray() };
            store.Remember(key, "HeyMelody", presentation); await store.FlushAsync();
            var restored = new DeviceStateStore(path).Find(key, "HeyMelody")!.ToSnapshot();
            Check(restored.AncModes.Single().Toggle == mode.Toggle && restored.AncModes.Single().Description == mode.Description
                && restored.Anc == 0x0209 && restored.PresentationCached
                && !restored.ControlReady && !restored.CanSetAnc && !restored.CanSetAncMode, "Display metadata restored write authority");
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            var savedMode = saved["devices"]![0]!["presentation"]!["ancModes"]![0]!;
            savedMode.AsObject().Remove("description"); savedMode.AsObject().Remove("toggle");
            File.WriteAllText(path, saved.ToJsonString());
            Check(new DeviceStateStore(path).Find(key, "HeyMelody")?.AncModes.Single() is { Description: "", Toggle: null },
                "Existing display history stopped loading after the additive fields");
            foreach (var invalid in new[]
            {
                mode.Toggle! with { OffKey = 0x0209 }, mode.Toggle! with { OnKey = 0x0109 },
                mode.Toggle! with { OnKey = 0x020A }, mode.Toggle! with { OffKey = -1 },
                mode.Toggle! with { Title = null! }, mode.Toggle! with { Description = null! },
                mode.Toggle! with { Description = new string('x', 4097) }
            })
            {
                var cache = new DeviceStateStore(path);
                cache.Remember(key, "HeyMelody", presentation with { AncModes = [mode with { Toggle = invalid }] });
                await cache.FlushAsync();
                var loaded = new DeviceStateStore(path).Find(key, "HeyMelody");
                Check(loaded?.Firmware == "preserved" && loaded.AncModes.Single().Toggle is null
                    && loaded.AncModes.Single().Options.SequenceEqual(mode.Options), "Invalid switch metadata discarded valid display history");
                savedMode["toggle"] = JsonSerializer.SerializeToNode(invalid, RpcWire.Json);
                File.WriteAllText(path, saved.ToJsonString());
                var reloaded = new DeviceStateStore(path).Find(key, "HeyMelody");
                Check(reloaded?.Firmware == "preserved" && reloaded.AncModes.Single().Toggle is null,
                    "Invalid stored switch metadata was not downgraded during load");
            }
            store = new DeviceStateStore(path);
            store.Remember(key, "HeyMelody", presentation with { AncModes = [mode with { Options = [mode.Options[0]] }] });
            await store.FlushAsync();
            Check(new DeviceStateStore(path).Find(key, "HeyMelody")?.AncModes.Single() is { Toggle: null, Options.Count: 1 },
                "A toggle without both option keys discarded the valid ordinary choice or retained switch metadata");
        });
    }

    private static UiAncMode Transparent() => new(2, "通透", "transparency", [new(0x0208, "通透"), new(0x0209, "强化人声")])
    { Description = "模式说明", Toggle = new(0x0208, 0x0209, "强化人声", "弱化环境声的同时突出人声") };
    private static ResourceCatalog Catalog(JsonObject raw) => ResourceCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(
        new { versionCode = 17006004, whiteList = new[] { raw } }), ResourceSource.HeyMelody, false);
    private static async Task<IReadOnlyList<UiAncMode>> Project(ResourceCatalog catalog, DailySnapshot daily, string scratch)
    {
        await using var service = new NativeHeadsetService(() => throw new InvalidOperationException("Presentation test enumerated Bluetooth"),
            new DeviceWhitelistStore(Path.Combine(scratch, "anc-presentation-whitelist.json")));
        typeof(NativeHeadsetService).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, catalog);
        return (IReadOnlyList<UiAncMode>)typeof(NativeHeadsetService).GetMethod("BuildAncModes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [daily])!;
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class Connector(string productId, byte initial) : IControlConnector
    {
        internal List<Packet> Writes { get; } = [];
        internal byte? ReportAfterSet;
        private byte[] _anc = [0, .. DailyParsers.BuildAnc(initial)];
        public Task<IControlTransport> OpenAsync(CancellationToken token) => Task.FromResult<IControlTransport>(new Transport(this));
        private byte[] Reply(Packet request)
        {
            if (request.Command == 0x0404)
            {
                Writes.Add(request); _anc = ReportAfterSet.HasValue ? [0, .. DailyParsers.BuildAnc(ReportAfterSet.Value)] : [0, .. request.Payload];
                return [0];
            }
            var pid = Convert.FromHexString(productId);
            return request.Command switch
            {
                0x0103 => [0, pid[2], pid[1], pid[0]], 0x010B => [1], 0x0100 => [0, 0, 1],
                0x010D => [0, 0], 0x0106 => [0, 1, 1, 75], 0x010C => _anc,
                _ => throw new InvalidOperationException($"Unexpected memory command {request.Command:X4}")
            };
        }
        private sealed class Transport(Connector owner) : IControlTransport
        {
            private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
            public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                var request = new PacketDecoder().Feed(bytes.Span).Single();
                _received.Writer.TryWrite(PacketEncoder.Encode((ushort)(request.Command | 0x8000), request.Sequence, owner.Reply(request)));
                return Task.CompletedTask;
            }
            public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
            { var bytes = await _received.Reader.ReadAsync(token); bytes.CopyTo(buffer); return bytes.Length; }
            public ValueTask DisposeAsync() { _received.Writer.TryComplete(); return ValueTask.CompletedTask; }
        }
    }
}
