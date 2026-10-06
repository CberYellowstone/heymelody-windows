using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    private DailySession? _session;
    private CommandCapabilities? _commands;
    private DeviceProfile? _profile;
    private async Task DropSessionAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
        _session = null; _commands = null; _profile = null;
    }
    private async Task EnsureSessionAsync(CancellationToken token)
    {
        if (_session is { Usable: true, Remaining: >= 32 }) return;
        await DropSessionAsync();
        _session = new(await _connector.OpenAsync(token));
    }
    private async Task<DailySnapshot> RefreshCoreAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed) return new() { Error = "客户端已关闭。" };
            return await ReadFreshAsync(token);
        }
        finally { _gate.Release(); }
    }
    private async Task<DailySnapshot> ReadFreshAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await EnsureSessionAsync(token);
                return await ReadSessionAsync(token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await DropSessionAsync();
                if (attempt == 0) continue;
            }
            catch (Exception) { await DropSessionAsync(); break; }
        }
        return new() { ObservedAtUtc = DateTimeOffset.UtcNow, Error = "设备状态读取失败，请确认耳机连接后刷新。", Connected = false };
    }
    private async Task<byte[]?> GetAsync(ushort command, byte[] payload, DailySnapshot snapshot, CancellationToken token)
    {
        var reply = await _session!.RequestAsync(command, payload, token);
        if (!reply.Matched || !_session.Usable) throw new IOException();
        if (reply.Status != 0)
        {
            snapshot.Warnings.Add($"查询 0x{command:X4} 被设备拒绝。");
            return null;
        }
        return reply.Payload;
    }
    private async Task<DailySnapshot> ReadSessionAsync(CancellationToken token)
    {
        var s = new DailySnapshot { ObservedAtUtc = DateTimeOffset.UtcNow, Connected = true };
        byte[] identity = await GetAsync(0x0103, [], s, token) ?? throw new IOException();
        var product = QueryParsers.Parse(0x0103, identity);
        if (!product.Success) throw new FormatException();
        s.ProductId = product.Data["productId"] as string;
        _profile = DeviceModelResolver.FindByProductId(s.ProductId!);
        byte[] cap = await GetAsync(0x0100, [], s, token) ?? throw new IOException();
        if (cap.Length < 2 || cap.Length > 512) throw new FormatException();
        _commands = CommandCapabilities.Parse(cap.AsSpan(1));
        if (_profile is null)
        {
            s.Error = "当前型号尚无已核实的配置，日常设置不可用。";
            return s;
        }
        s.ProfileVerified = true; s.DeviceName = _profile.Name;
        s.EqualizerFrequencies = _profile.CustomEqFrequencies.Select(x => checked((ushort)x)).ToArray();
        s.Presets = _profile.EqualizerModes.Select(x => new PresetOption(checked((byte)x.ProtocolIndex), x.ModeType,
            x.ModeType switch { 42 => "丹拿原声", 43 => "丹拿高解析", 28 => "纯享人声", 46 => "清亮高音", 29 => "澎湃低音", _ => $"预设 {x.ProtocolIndex}" })).ToList();
        s.AncOptions = _profile.NoiseReductionModes.SelectMany(p => p.Children.Select(c =>
            new AncOption((byte)p.ProtocolIndex, (byte)c.ProtocolIndex, c.ModeType, c.ModeType switch {
                1 => "关闭降噪", 2 => "通透", 7 => "智能切换", 4 => "深度降噪", 8 => "中度降噪", 3 => "轻度降噪",
                11 => "更少噪音", 12 => "标准", 13 => "更多噪音", _ => $"降噪档位 {c.ProtocolIndex}"
            }))).ToList();
        async Task Parse(ushort cmd, byte[] request, Action<byte[]> action)
        {
            if (!_commands.Supports(cmd)) return;
            byte[]? payload = await GetAsync(cmd, request, s, token);
            if (payload is null) return;
            try { action(payload); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            { s.Warnings.Add($"查询 0x{cmd:X4} 的布局未通过校验，状态保留未知。"); }
        }
        await Parse(0x0105, [], data =>
        {
            var reply = QueryParsers.Parse(0x0105, data);
            if (!reply.Success) throw new FormatException();
            var entries = (List<FirmwareEntry>)reply.Data["firmwareEntries"]!;
            s.FirmwareVersion = entries.FirstOrDefault(x => x.DeviceType == 1 && x.VersionType == 2)?.Version;
        });
        await Parse(0x0114, [], data =>
        {
            var reply = QueryParsers.Parse(0x0114, data);
            if (!reply.Success) throw new FormatException();
            s.CodecRaw = (byte)reply.Data["codecRaw"]!;
        });
        // Feature IDs are selected explicitly; no notification registration or implicit setter is sent.
        await Parse(0x010D, [5, 0x11, 0x06, 0x28, 0x18, 0x1B], data => s.RawFeatures = DailyParsers.ParseFeatures(data));
        await Parse(0x0106, [], data => s.Battery = DailyParsers.ParseBattery(data));
        await Parse(0x010C, [1, 1], data => s.Anc = DailyParsers.ParseAnc(data, _profile));
        await Parse(0x010F, [], data => s.PresetId = DailyParsers.ParsePreset(data));
        await Parse(0x0122, [], data =>
        { s.CustomEqualizers = DailyParsers.ParseEqualizers(data); s.CustomEqualizersRead = true; });
        bool legacySpatialReported = s.RawFeatures.TryGetValue(0x1B, out byte legacySpatial) && legacySpatial <= 1;
        bool useModernSpatial = _commands.Supports(0x012A) && (_commands.Supports(0x0422) || !legacySpatialReported);
        if (useModernSpatial)
            await Parse(0x012A, [], data => s.SpatialMode = DailyParsers.ParseSpatial(data));
        else if (legacySpatialReported) s.SpatialMode = legacySpatial;
        bool? Feature(byte id) => s.RawFeatures.TryGetValue(id, out byte value) ? value switch { 0 => false, 1 => true, _ => null } : null;
        var resolved = CapabilityResolver.Resolve(_profile, _commands, s.RawFeatures);
        s.DualConnection = Feature(0x11); s.HiRes = Feature(0x18);
        s.GameMode = resolved.GameMode.FeatureId.HasValue ? Feature(resolved.GameMode.FeatureId.Value) : null;
        bool Commands(ushort read, ushort write) => _commands.Supports(read) && _commands.Supports(write);
        s.Capabilities = new DailyCapabilities
        {
            Battery = s.Battery is not null,
            Anc = Commands(0x010C, 0x0404) && s.Anc?.Primary is not null,
            Preset = Commands(0x010F, 0x0406) && s.PresetId is not null,
            CustomEqualizer = Commands(0x0122, 0x0418) && s.CustomEqualizersRead,
            DualConnection = Commands(0x010D, 0x0403) && s.DualConnection is not null,
            GameMode = Commands(0x010D, 0x0403) && s.GameMode is not null,
            HiRes = Commands(0x010D, 0x0403) && s.HiRes is not null,
            SpatialMode = s.SpatialMode is not null && (useModernSpatial ? Commands(0x012A, 0x0422) : Commands(0x010D, 0x0403)),
            SpatialHiResMutex = _profile.SpatialHiResMutex == 1,
            GameFeatureId = resolved.GameMode.FeatureId,
            SpatialRoute = useModernSpatial ? "modern" : legacySpatialReported ? "legacy" : "unavailable"
        };
        s.ObservedAtUtc = DateTimeOffset.UtcNow;
        return s;
    }
}
