using HeyMelodyWindows.App;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;
using HeyMelodyWindows.Windows;

namespace HeyMelodyWindows.Host;

public sealed class NativeHeadsetService : IHeadsetService, IDeviceManagementService
{
    private readonly Func<IReadOnlyList<BluetoothCandidate>> _enumerate;
    private readonly DeviceWhitelistStore _whitelist;
    private readonly Dictionary<ulong, string> _identities = [];
    private readonly Dictionary<string, BluetoothCandidate> _devices = [];
    private DailyClient? _client;
    private string? _selected;
    private ulong? _selectedAddress;
    private string _selectedName = "";
    private DailySnapshot? _last;
    private int _getSubmissions, _setSubmissions, _otherSubmissions;

    public NativeHeadsetService() : this(WindowsBluetooth.EnumeratePaired, DeviceWhitelistStore.CreateDefault()) { }

    internal NativeHeadsetService(Func<IReadOnlyList<BluetoothCandidate>> enumerate, DeviceWhitelistStore whitelist)
    { _enumerate = enumerate; _whitelist = whitelist; }

    public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var allowed = _whitelist.Load();
        var result = EnumerateCandidates(token).Where(device => allowed.Contains(DeviceWhitelistStore.KeyFor(device.Address)))
            .Select(device => new UiDevice(_identities[device.Address], device.Name, device.Connected)).ToArray();
        return Task.FromResult<IReadOnlyList<UiDevice>>(result);
    }

    public Task<UiDeviceCatalog> EnumerateCandidatesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HashSet<string> allowed;
        string? warning = null;
        try { allowed = _whitelist.Load(); }
        catch (InvalidDataException)
        {
            allowed = new(StringComparer.Ordinal);
            warning = "设备选择记录已损坏，请重新选择并保存。";
        }
        var result = EnumerateCandidates(token).Select(device => new UiDeviceCandidate(_identities[device.Address],
            device.Name, device.Connected, allowed.Contains(DeviceWhitelistStore.KeyFor(device.Address)))).ToArray();
        return Task.FromResult(new UiDeviceCatalog(result, warning));
    }

    private IReadOnlyList<BluetoothCandidate> EnumerateCandidates(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paired = _enumerate().Where(WindowsBluetooth.IsAudioCandidate).ToArray();
        token.ThrowIfCancellationRequested();
        _devices.Clear();
        foreach (var device in paired)
        {
            if (!_identities.TryGetValue(device.Address, out string? identity))
                _identities[device.Address] = identity = Guid.NewGuid().ToString("N");
            _devices[identity] = device;
        }
        return paired.OrderByDescending(device => device.Connected)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ThenBy(device => device.Address).ToArray();
    }

    public Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identities.Count is 0 or > 4096 || identities.Any(string.IsNullOrWhiteSpace))
            return Task.FromResult(new UiWhitelistSaveResult(false, "请至少选择一台有效的音频设备。"));
        var requested = identities.Distinct(StringComparer.Ordinal).ToArray();
        // Revalidate the complete selection before replacing the previous file.
        EnumerateCandidates(token);
        if (requested.Any(identity => !_devices.ContainsKey(identity)))
            return Task.FromResult(new UiWhitelistSaveResult(false, "所选设备已解除配对或不再可用，请刷新列表后重新选择。"));
        token.ThrowIfCancellationRequested();
        try
        {
            _whitelist.Save(requested.Select(identity => DeviceWhitelistStore.KeyFor(_devices[identity].Address)));
            return Task.FromResult(new UiWhitelistSaveResult(true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Task.FromResult(new UiWhitelistSaveResult(false, "无法保存设备选择，请重试。")); }
    }

    public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
    {
        if (!_devices.TryGetValue(identity, out var peer)) throw new InvalidOperationException("重新刷新设备列表后再连接。");
        token.ThrowIfCancellationRequested();
        if (!_whitelist.Load().Contains(DeviceWhitelistStore.KeyFor(peer.Address))
            || !_enumerate().Any(device => device.Address == peer.Address && WindowsBluetooth.IsAudioCandidate(device)))
            throw new InvalidOperationException("所选耳机未启用或已解除配对，请重新选择设备。");
        await DisconnectAsync(token);
        _selected = identity; _selectedName = peer.Name; _selectedAddress = peer.Address;
        _getSubmissions = _setSubmissions = _otherSubmissions = 0;
        _client = new DailyClient(new PeerConnector(peer.Address, RecordSubmission));
        return Map(await _client.RefreshAsync(token));
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        var client = _client; _client = null; _selected = null; _selectedName = ""; _selectedAddress = null;
        if (client is not null) await client.DisposeAsync();
    }

    public async Task<UiSnapshot> RefreshAsync(CancellationToken token)
        => await RefreshAsync(RefreshScope.Full, token);

    public async Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (_client is null) throw new InvalidOperationException("请先连接耳机。");
        return Map(await _client.RefreshAsync((DailyRefreshScope)scope, token));
    }

    public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
    {
        if (_client is null) return new() { BackendReceiptReceived = true, FailureKind = ApplyFailure.StateUnavailable,
            ConnectionState = ConnectionState.Unknown, Message = "尚未连接耳机，未发送设置。" };
        DailyApplyResult result;
        switch (action.Kind)
        {
            case "Anc":
                if (action.Value is < 0 or > 65535) return InvalidAction();
                result = await _client.ApplyAncAsync((byte)(action.Value >> 8), (byte)action.Value, token); break;
            case "Equalizer":
                if (action.Value is < 0 or > 255) return InvalidAction();
                result = await _client.ApplyPresetAsync((byte)action.Value, token); break;
            case "CustomSelect":
                if (action.Value is < 1 or > 255) return InvalidAction();
                result = await _client.SelectCustomEqualizerAsync((byte)action.Value, token); break;
            case "CustomEqualizer":
                if (action.EqualizerEntry is not { } entry || entry.Id is < 0 or > 255 ||
                    entry.Bands.Count != 10 || entry.MinimumGain is < -128 or > 127 || entry.MaximumGain is < -128 or > 127 ||
                    entry.Bands.Any(band => band.FrequencyHz is < 1 or > 65535 || band.Gain is < -128 or > 127))
                    return InvalidAction();
                result = await _client.SaveEqualizerAsync(new EqualizerEdit
                {
                    Id = (byte)entry.Id, Name = entry.Name, MinimumGain = (sbyte)entry.MinimumGain,
                    MaximumGain = (sbyte)entry.MaximumGain, Activate = true,
                    Bands = entry.Bands.Select(band => new EqualizerBand((ushort)band.FrequencyHz, (sbyte)band.Gain)).ToList()
                }, token); break;
            case "DualConnection":
                if (action.Value is not (0 or 1)) return InvalidAction();
                result = await _client.SetDualConnectionAsync(action.Value == 1, token); break;
            case "GameMode":
                if (action.Value is not (0 or 1)) return InvalidAction();
                result = await _client.SetGameModeAsync(action.Value == 1, token); break;
            case "Spatial":
                if (action.Value is < 0 or > 2) return InvalidAction();
                result = await _client.SetSpatialModeAsync((byte)action.Value, action.AllowMutexChange, token); break;
            case "HiRes":
                if (action.Value is not (0 or 1)) return InvalidAction();
                result = await _client.SetHiResAsync(action.Value == 1, action.AllowMutexChange, token); break;
            default: return InvalidAction();
        }
        UiSnapshot? after = result.After is null ? null : Map(result.After);
        var connection = after?.ConnectionState ?? ConnectionEvidence(result.Before);
        return new UiApplyResult
        {
            BackendReceiptReceived = true,
            TargetSubmitted = result.TargetSubmitted,
            PrerequisiteSubmitted = result.PrerequisiteSubmitted,
            AckStatus = result.AckStatus,
            ObservedEqualizerId = result.ObservedEqualizerId,
            ConnectionState = connection,
            FailureKind = connection == ConnectionState.Disconnected ? ApplyFailure.Disconnected : (ApplyFailure)result.FailureKind,
            Accepted = result.AckAccepted == true,
            ReadbackMatches = result.ReadbackMatches,
            SubmissionAttempted = result.SubmissionAttempted,
            AcknowledgementUnknown = result.SubmissionAttempted && result.AckAccepted is null,
            PartialEffects = !result.Success && result.PartialEffects.Count != 0,
            Message = ResultMessage(result), After = after
        };
    }

    private UiApplyResult InvalidAction() => new() { BackendReceiptReceived = true,
        FailureKind = ApplyFailure.Validation, ConnectionState = ConnectionEvidence(null), Message = "设置参数无效，未发送设置。" };

    private ConnectionState ConnectionEvidence(DailySnapshot? snapshot)
    {
        if (snapshot is { Connected: true }) return ConnectionState.Connected;
        if (_selectedAddress is not { } address) return ConnectionState.Unknown;
        try
        {
            var peers = _enumerate().Where(x => x.Address == address && x.Authenticated).ToArray();
            return peers.Length == 1 ? peers[0].Connected ? ConnectionState.Connected : ConnectionState.Disconnected
                : ConnectionState.Unknown;
        }
        catch (Exception) { return ConnectionState.Unknown; }
    }

    private static string ResultMessage(DailyApplyResult result)
    {
        if (result.Success && result.ReadbackVerified)
            return result.SubmissionAttempted ? "设置已完成，耳机状态回读一致。" : "耳机已处于所选状态。";
        if (result.PartialEffects.Count != 0)
            return "部分操作已提交。请先查看当前状态，再决定下一步操作。";
        if (result.FailureKind == DailyApplyFailure.Disconnected)
            return "设置时耳机控制连接已中断，已尝试重新读取当前状态。";
        if (!result.SubmissionAttempted)
            return string.IsNullOrWhiteSpace(result.Message) ? "未发送设置。请确认耳机状态、能力与互斥选项后再操作。" : result.Message;
        if (result.AckAccepted == false) return "耳机拒绝了设置，已重新读取当前状态。";
        if (result.AckAccepted is null)
            return result.ReadbackVerified ? "响应未确认，状态回读已匹配。请勿重复发送。" : "响应未确认，设置可能已生效。请刷新状态后再操作。";
        if (result.ReadbackMatches is null) return "耳机已接受请求，状态回读暂时不可用。";
        return "耳机已接受请求，当前状态尚未匹配。请刷新状态确认。";
    }

    private UiSnapshot Map(DailySnapshot snapshot)
    {
        _last = snapshot;
        bool valid = snapshot.Connected && snapshot.ProfileVerified && snapshot.Error is null;
        var caps = snapshot.Capabilities;
        return new UiSnapshot
        {
            Identity = _selected ?? "", DeviceName = _selectedName,
            ProductId = snapshot.ProductId,
            Connected = snapshot.Connected, SupportedModel = snapshot.ProfileVerified,
            ConnectionState = ConnectionEvidence(snapshot),
            Failure = snapshot.Error is null ? null : "未能完整读取耳机状态，请刷新或重新连接。",
            LeftBattery = snapshot.Battery?.Left?.Percent, RightBattery = snapshot.Battery?.Right?.Percent,
            CaseBattery = snapshot.Battery?.Case?.Percent, LeftCharging = snapshot.Battery?.Left?.Charging,
            RightCharging = snapshot.Battery?.Right?.Charging, CaseCharging = snapshot.Battery?.Case?.Charging,
            Anc = snapshot.Anc is { Primary: { } primary, Level: { } level } ? (primary << 8) | level : null,
            Equalizer = snapshot.PresetId, Spatial = snapshot.SpatialMode,
            DualConnection = snapshot.DualConnection, GameMode = snapshot.GameMode, HiRes = snapshot.HiRes,
            CanSetAnc = valid && caps.Anc && snapshot.Anc?.Primary is not null,
            CanSetEqualizer = valid && caps.Preset && snapshot.PresetId is not null,
            CanSetCustomEqualizer = valid && caps.CustomEqualizer && snapshot.CustomEqualizersRead,
            CanSetDualConnection = valid && caps.DualConnection && snapshot.DualConnection is not null,
            CanSetGameMode = valid && caps.GameMode && snapshot.GameMode is not null,
            CanSetSpatial = valid && caps.SpatialMode && snapshot.SpatialMode is not null,
            CanSetHiRes = valid && caps.HiRes && snapshot.HiRes is not null,
            SpatialHiResMutex = caps.SpatialHiResMutex,
            AncOptions = snapshot.AncOptions.Select(option => new UiOption((option.Primary << 8) | option.Level, option.Name)).ToArray(),
            EqualizerOptions = snapshot.Presets.Select(option => new UiOption(option.ProtocolId, option.Name)).ToArray(),
            SpatialOptions = caps.SpatialRoute == "legacy"
                ? [new(0, "关闭"), new(1, "开启")]
                : [new(0, "关闭"), new(1, "固定空间音频"), new(2, "头部跟踪")],
            EqualizerEntries = snapshot.CustomEqualizers.Select(entry => new UiEqEntry(entry.Id, entry.Name,
                entry.MinimumGain, entry.MaximumGain, entry.Selected,
                entry.Bands.Select(band => new UiBand(band.Frequency, band.Gain)).ToArray())).ToArray(),
            Firmware = snapshot.FirmwareVersion, SampleTime = snapshot.ObservedAtUtc,
            Warnings = snapshot.Warnings.Count == 0 ? [] : ["部分耳机状态暂时不可用，相关设置已禁用。"]
        };
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync(CancellationToken.None));

    public object Diagnostics() => new
    {
        observedAtUtc = _last?.ObservedAtUtc, productId = _last?.ProductId,
        profileVerified = _last?.ProfileVerified, connected = _last?.Connected,
        capabilities = _last?.Capabilities, features = _last?.RawFeatures,
        battery = _last?.Battery, anc = _last?.Anc,
        presetId = _last?.PresetId, customEqualizersRead = _last?.CustomEqualizersRead,
        customEqualizerIds = _last?.CustomEqualizers.Select(entry => (int)entry.Id).ToArray(),
        customEqualizerLayouts = _last?.CustomEqualizers.Select(entry => new
        {
            id = entry.Id, entry.MinimumGain, entry.MaximumGain,
            frequencies = entry.Bands.Select(band => (int)band.Frequency).ToArray()
        }).ToArray(),
        equalizerFrequencies = _last?.EqualizerFrequencies, hiRes = _last?.HiRes,
        spatialMode = _last?.SpatialMode, gameMode = _last?.GameMode,
        dualConnection = _last?.DualConnection, warnings = _last?.Warnings,
        applicationGetSubmissions = _getSubmissions, applicationSetSubmissions = _setSubmissions,
        applicationOtherSubmissions = _otherSubmissions,
        evidenceScope = "application-submission-and-peer-readback"
    };

    private void RecordSubmission(ReadOnlyMemory<byte> bytes)
    {
        var decoder = new PacketDecoder();
        foreach (var packet in decoder.Feed(bytes.Span))
        {
            if (packet.Command is >= 0x0100 and < 0x0200) _getSubmissions++;
            else if (packet.Command is >= 0x0400 and < 0x0500) _setSubmissions++;
            else _otherSubmissions++;
        }
        decoder.Complete();
    }

    private sealed class PeerConnector(ulong address, Action<ReadOnlyMemory<byte>> record) : IControlConnector
    {
        private readonly Guid _service = X4Profile.Load().Uuid;
        public async Task<IControlTransport> OpenAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var devices = WindowsBluetooth.EnumeratePaired().Where(device => device.Address == address && device.Authenticated).ToArray();
            if (devices.Length != 1 || !devices[0].Connected) throw new IOException("The selected peer is disconnected.");
            var services = WindowsSdp.FindServices(address, _service).Where(service => service.ServiceId == _service &&
                service.Channel is > 0 and <= 30).DistinctBy(service => service.Channel).ToArray();
            if (services.Length != 1) throw new IOException("A unique Melody service is required.");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            return new Transport(await RfcommConnection.ConnectAsync(address, _service, services[0].Channel, budget.Token), record);
        }
    }

    private sealed class Transport(RfcommConnection connection, Action<ReadOnlyMemory<byte>> record) : IControlTransport
    {
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        { record(bytes); return connection.SendAsync(bytes, token); }
        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => connection.ReadAsync(buffer, token);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
