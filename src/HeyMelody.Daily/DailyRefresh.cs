using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    private DailySession? _session;
    private CommandCapabilities? _commands;
    private DeviceProfile? _profile;
    private DailySnapshot? _latest;
    private readonly Dictionary<DailyRefreshScope, List<string>> _readWarnings = [];

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
    private async Task<DailySnapshot> RefreshCoreAsync(DailyRefreshScope scope, CancellationToken token)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed) return new() { Error = "客户端已关闭。" };
            return await ReadFreshAsync(token, scope);
        }
        finally { _gate.Release(); }
    }
    private async Task<DailySnapshot> ReadFreshAsync(CancellationToken token, DailyRefreshScope scope = DailyRefreshScope.Full)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await EnsureSessionAsync(token);
                // New transports require identity/capability verification. Returned results do
                // not share mutable state with either the cache or previously returned results.
                bool initialize = _commands is null || _latest is not { Connected: true };
                var s = scope == DailyRefreshScope.Full || _latest is not { Connected: true }
                    ? new DailySnapshot() : Clone(_latest);
                s.Connected = true; if (s.ProfileVerified) s.Error = null; s.Warnings = [];
                if (scope == DailyRefreshScope.Full || _latest is not { Connected: true }) _readWarnings.Clear();
                if (initialize || scope is DailyRefreshScope.Full or DailyRefreshScope.Identity)
                    s = await ReadIdentityAsync(s, token);
                if (s.ProfileVerified && scope != DailyRefreshScope.Identity)
                {
                    if (scope == DailyRefreshScope.Full)
                    {
                        foreach (var section in new[] { DailyRefreshScope.Information, DailyRefreshScope.Features,
                            DailyRefreshScope.Battery, DailyRefreshScope.Anc, DailyRefreshScope.Equalizer })
                            await ReadSectionAsync(s, section, token);
                    }
                    else await ReadSectionAsync(s, scope, token);
                }
                ResolveCapabilities(s);
                s.Warnings = _readWarnings.Values.SelectMany(x => x).Distinct().ToList();
                s.ObservedAtUtc = DateTimeOffset.UtcNow;
                _latest = Clone(s);
                return s;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await DropSessionAsync();
                if (attempt == 0) continue;
            }
            catch (Exception) { await DropSessionAsync(); break; }
        }
        _readWarnings.Clear();
        _latest = new() { ObservedAtUtc = DateTimeOffset.UtcNow,
            Error = "设备状态读取失败，请确认耳机连接后刷新。", Connected = false };
        return Clone(_latest);
    }
    private async Task<byte[]?> GetAsync(ushort command, byte[] payload, DailySnapshot s, CancellationToken token)
    {
        var reply = await _session!.RequestAsync(command, payload, token);
        if (!reply.Matched || !_session.Usable) throw new IOException();
        if (reply.Status != 0)
        {
            s.Warnings.Add($"查询 0x{command:X4} 被设备拒绝。");
            return null;
        }
        return reply.Payload;
    }
    private async Task<DailySnapshot> ReadIdentityAsync(DailySnapshot s, CancellationToken token)
    {
        byte[] identity = await GetAsync(0x0103, [], s, token) ?? throw new IOException();
        var product = QueryParsers.Parse(0x0103, identity);
        if (!product.Success) throw new FormatException();
        string productId = (string)product.Data["productId"]!;
        if (s.ProductId is not null && s.ProductId != productId)
        {
            s = new() { Connected = true };
            _readWarnings.Clear();
        }
        s.ProductId = productId;
        _profile = DeviceModelResolver.FindByProductId(productId);
        byte[] cap = await GetAsync(0x0100, [], s, token) ?? throw new IOException();
        if (cap.Length < 2 || cap.Length > 512) throw new FormatException();
        _commands = CommandCapabilities.Parse(cap.AsSpan(1));
        if (_profile is null)
        {
            s.ProfileVerified = false;
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
        _readWarnings[DailyRefreshScope.Identity] = s.Warnings.ToList();
        return s;
    }
    private async Task ReadSectionAsync(DailySnapshot s, DailyRefreshScope scope, CancellationToken token)
    {
        s.Warnings = [];
        async Task Parse(ushort command, byte[] request, Action<byte[]> parse)
        {
            if (!_commands!.Supports(command)) return;
            byte[]? payload = await GetAsync(command, request, s, token);
            if (payload is null) return;
            try { parse(payload); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            { s.Warnings.Add($"查询 0x{command:X4} 的布局未通过校验，状态保留未知。"); }
        }
        switch (scope)
        {
            case DailyRefreshScope.Information:
                s.FirmwareVersion = null; s.CodecRaw = null;
                await Parse(0x0105, [], data => {
                    var reply = QueryParsers.Parse(0x0105, data);
                    if (!reply.Success) throw new FormatException();
                    s.FirmwareVersion = ((List<FirmwareEntry>)reply.Data["firmwareEntries"]!)
                        .FirstOrDefault(x => x.DeviceType == 1 && x.VersionType == 2)?.Version;
                });
                await Parse(0x0114, [], data => {
                    var reply = QueryParsers.Parse(0x0114, data);
                    if (!reply.Success) throw new FormatException();
                    s.CodecRaw = (byte)reply.Data["codecRaw"]!;
                });
                break;
            case DailyRefreshScope.Features:
                s.RawFeatures = []; s.SpatialMode = null;
                // Refresh never generates notifications or setters.
                await Parse(0x010D, [5, 0x11, 0x06, 0x28, 0x18, 0x1B], data => s.RawFeatures = DailyParsers.ParseFeatures(data));
                bool legacy = s.RawFeatures.TryGetValue(0x1B, out byte legacySpatial) && legacySpatial <= 1;
                if (_commands!.Supports(0x012A) && (_commands.Supports(0x0422) || !legacy))
                    await Parse(0x012A, [], data => s.SpatialMode = DailyParsers.ParseSpatial(data));
                else if (legacy) s.SpatialMode = legacySpatial;
                break;
            case DailyRefreshScope.Battery:
                s.Battery = null;
                await Parse(0x0106, [], data => s.Battery = DailyParsers.ParseBattery(data));
                break;
            case DailyRefreshScope.Anc:
                s.Anc = null;
                await Parse(0x010C, [1, 1], data => s.Anc = DailyParsers.ParseAnc(data, _profile!));
                break;
            case DailyRefreshScope.Equalizer:
                s.PresetId = null; s.CustomEqualizers = []; s.CustomEqualizersRead = false;
                await Parse(0x010F, [], data => s.PresetId = DailyParsers.ParsePreset(data));
                await Parse(0x0122, [], data => { s.CustomEqualizers = DailyParsers.ParseEqualizers(data); s.CustomEqualizersRead = true; });
                break;
        }
        _readWarnings[scope] = s.Warnings.ToList();
    }
    private void ResolveCapabilities(DailySnapshot s)
    {
        if (_profile is null || _commands is null || !s.ProfileVerified) { s.Capabilities = new(); return; }
        bool? Feature(byte id) => s.RawFeatures.TryGetValue(id, out byte value) ? value switch { 0 => false, 1 => true, _ => null } : null;
        bool legacy = s.RawFeatures.TryGetValue(0x1B, out byte value) && value <= 1;
        bool modern = _commands.Supports(0x012A) && (_commands.Supports(0x0422) || !legacy);
        var resolved = CapabilityResolver.Resolve(_profile, _commands, s.RawFeatures);
        s.DualConnection = Feature(0x11); s.HiRes = Feature(0x18);
        s.GameMode = resolved.GameMode.FeatureId.HasValue ? Feature(resolved.GameMode.FeatureId.Value) : null;
        bool Commands(ushort read, ushort write) => _commands.Supports(read) && _commands.Supports(write);
        s.Capabilities = new()
        {
            Battery = s.Battery is not null,
            Anc = Commands(0x010C, 0x0404) && s.Anc?.Primary is not null,
            Preset = Commands(0x010F, 0x0406) && s.PresetId is not null,
            CustomEqualizer = Commands(0x0122, 0x0418) && s.CustomEqualizersRead,
            DualConnection = Commands(0x010D, 0x0403) && s.DualConnection is not null,
            GameMode = Commands(0x010D, 0x0403) && s.GameMode is not null,
            HiRes = Commands(0x010D, 0x0403) && s.HiRes is not null,
            SpatialMode = s.SpatialMode is not null && (modern ? Commands(0x012A, 0x0422) : Commands(0x010D, 0x0403)),
            SpatialHiResMutex = _profile.SpatialHiResMutex == 1,
            GameFeatureId = resolved.GameMode.FeatureId,
            SpatialRoute = modern ? "modern" : legacy ? "legacy" : "unavailable"
        };
    }
    private static DailySnapshot Clone(DailySnapshot s) => new()
    {
        ObservedAtUtc = s.ObservedAtUtc, Connected = s.Connected, ProfileVerified = s.ProfileVerified,
        ProductId = s.ProductId, DeviceName = s.DeviceName, FirmwareVersion = s.FirmwareVersion, CodecRaw = s.CodecRaw,
        Battery = s.Battery is null ? null : new() { Left = s.Battery.Left, Right = s.Battery.Right,
            Case = s.Battery.Case, RawComponents = new(s.Battery.RawComponents) },
        Anc = s.Anc is null ? null : new() { Kind = s.Anc.Kind, ModeType = s.Anc.ModeType,
            RawMask = s.Anc.RawMask.ToArray(), Primary = s.Anc.Primary, Level = s.Anc.Level },
        PresetId = s.PresetId, Presets = s.Presets.ToList(), AncOptions = s.AncOptions.ToList(),
        CustomEqualizers = s.CustomEqualizers.Select(x => new CustomEqualizer { Id = x.Id, Name = x.Name,
            Selected = x.Selected, MinimumGain = x.MinimumGain, MaximumGain = x.MaximumGain, Bands = x.Bands.ToList() }).ToList(),
        CustomEqualizersRead = s.CustomEqualizersRead, EqualizerFrequencies = s.EqualizerFrequencies.ToArray(),
        DualConnection = s.DualConnection, GameMode = s.GameMode, SpatialMode = s.SpatialMode, HiRes = s.HiRes,
        RawFeatures = new(s.RawFeatures), Warnings = s.Warnings.ToList(), Error = s.Error,
        Capabilities = new() { Battery = s.Capabilities.Battery, Anc = s.Capabilities.Anc, Preset = s.Capabilities.Preset,
            CustomEqualizer = s.Capabilities.CustomEqualizer, DualConnection = s.Capabilities.DualConnection,
            GameMode = s.Capabilities.GameMode, SpatialMode = s.Capabilities.SpatialMode, HiRes = s.Capabilities.HiRes,
            SpatialHiResMutex = s.Capabilities.SpatialHiResMutex, GameFeatureId = s.Capabilities.GameFeatureId,
            SpatialRoute = s.Capabilities.SpatialRoute }
    };
}
