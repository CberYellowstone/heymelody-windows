using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Host;

public sealed partial class NativeHeadsetService
{
    private readonly CalibrationHistoryStore _hearingHistory = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "hearing"));
    private CalibrationEngine? _calibration;
    private CalibrationDeviceContext? _calibrationContext;
    private UiCalibrationState? _calibrationState;
    private long _calibrationRevision;
    private Task _calibrationBackground = Task.CompletedTask;
    private IReadOnlyList<UiHearingProfile> _hearingProfiles = [];
    private int? _currentHearingUid;
    private bool _suppressCalibrationEvents, _savingHearing;
    private readonly object _hearingRefreshGate = new();
    private CancellationTokenSource? _hearingRefreshLifetime;
    private Task _hearingRefreshTask = Task.CompletedTask;
    private bool _hearingRefreshRunning, _hearingRefreshNeeded;
    public event Action<UiCalibrationState>? CalibrationChanged;

    private async Task InitializeCalibrationAsync(CancellationToken token)
    {
        if (_client is null || !_client.IsConnected) return;
        if (_stableKey.Length == 0 && _selectedAddress is { } address) _stableKey = DeviceWhitelistStore.KeyFor(address);
        _calibrationContext = _client.CalibrationContext(_stableKey, _generation);
        _calibration = new(_client, _calibrationContext, _hearingHistory);
        _calibration.Changed += EngineChanged;
        _hearingRefreshLifetime = new();
        _client.Notification += OnHearingNotification;
        if (CalibrationAvailable("hearing"))
        {
            try { await ReadHearingProfilesAsync(token); }
            catch (Exception) when (!token.IsCancellationRequested) { _hearingProfiles = []; }
        }
    }
    private void OnHearingNotification(Packet packet)
    {
        if (packet.Command != 0x0503 || !CalibrationAvailable("hearing") || _savingHearing
            || _calibrationState is { Stage: not ("Completed" or "Failed" or "Cancelled") }) return;
        try { if (!NotificationParsers.ParseComponentPairs(packet.Payload).ContainsKey(0x0B)) return; }
        catch (FormatException) { return; }
        lock (_hearingRefreshGate)
        {
            if (_hearingRefreshLifetime is not { IsCancellationRequested: false } lifetime || _client is not { } client) return;
            _hearingRefreshNeeded = true;
            if (_hearingRefreshRunning) return;
            _hearingRefreshRunning = true;
            // Never query on the Bluetooth reader callback. A phone's feature push
            // invalidates saved-profile metadata even when the enabled value is unchanged.
            _hearingRefreshTask = Task.Run(async () =>
            {
                while (true)
                {
                    lock (_hearingRefreshGate)
                    {
                        if (!_hearingRefreshNeeded || lifetime.IsCancellationRequested)
                        { _hearingRefreshRunning = false; return; }
                        _hearingRefreshNeeded = false;
                    }
                    try
                    {
                        if (_calibrationState is { Stage: not ("Completed" or "Failed" or "Cancelled") }) continue;
                        await ReadHearingProfilesAsync(lifetime.Token);
                        await client.RefreshAsync(Daily.DailyRefreshScope.Features, lifetime.Token);
                    }
                    catch (Exception) { /* Keep existing profiles; an explicit refresh can retry the GET. */ }
                }
            });
        }
    }
    private bool CalibrationAvailable(string kind)
    {
        if (_calibrationContext is null || _client is null) return false;
        var c = _client.CalibrationContext(_stableKey, _generation);
        bool Commands(params ushort[] ids) => ids.All(c.Commands.Contains);
        return kind == "fit" ? c.Fit && c.Events.Contains(4) && Commands(0x0107, 0x0405)
            : (c.HearingV1 || c.HearingV2) && Commands(0x0107, 0x040D, 0x040E, 0x0115, 0x0403)
                && (!c.EarScan || c.Events.Contains(14) && Commands(0x011E, 0x0415));
    }
    internal UiCalibrationState CreateCalibration(string handle, string kind)
    {
        if (kind is not ("fit" or "hearing") || !CalibrationAvailable(kind) || handle.Length != 32)
            throw new InvalidOperationException("当前设备不支持此测试，或必要的状态通知不可用。");
        if (_calibrationState is { Stage: not ("Completed" or "Failed" or "Cancelled") })
            throw new InvalidOperationException("请先完成或取消当前测试。");
        _calibrationState = new() { Handle = handle, Identity = _selected!, Generation = _generation,
            Revision = ++_calibrationRevision, Workflow = kind, Stage = "Ready",
            HearingVersion = _calibrationContext!.HearingV2 ? "V2" : "V1", SupportsEarScan = _calibrationContext.EarScan };
        return _calibrationState;
    }
    internal UiCalibrationState GetCalibration(string handle) => _calibrationState is { } state && state.Handle == handle
        ? state : throw new InvalidOperationException("此测试已结束或连接已变化。");
    internal async Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token)
    {
        var state = GetCalibration(handle);
        if (revision != state.Revision) throw new InvalidOperationException("测试进度已变化，请按当前步骤操作。");
        if (_calibration is null) throw new InvalidOperationException("耳机连接已变化。");
        switch (input.Action)
        {
            case "start":
                if (state.Stage is not ("Ready" or "Failed") && !(state.Workflow == "fit" && state.Stage == "Completed")) throw new InvalidOperationException("测试已经启动。");
                if (state.Workflow == "fit") StartBackground(() => _calibration.RunFitAsync());
                else if (state.SupportsEarScan) StartBackground(() => _calibration.StartEarScanAsync());
                else await _calibration.BeginHearingAsync(_calibrationContext!.HearingV2 ? HearingVersion.V2 : HearingVersion.V1, token);
                break;
            case "scan":
                if (state.Workflow != "hearing" || !state.SupportsEarScan || state.Stage is not ("Ready" or "ScanCompleted")) throw new InvalidOperationException();
                StartBackground(() => _calibration.StartEarScanAsync()); break;
            case "answer": await _calibration.AnswerAsync(input.Heard ?? throw new InvalidDataException(), token); break;
            case "level": await _calibration.SetLevelAsync(input.LevelIndex ?? throw new InvalidDataException(), input.Released, token); break;
            case "next":
                if (state.Stage == "ScanCompleted") await _calibration.BeginHearingAsync(_calibrationContext!.HearingV2 ? HearingVersion.V2 : HearingVersion.V1, token);
                else await _calibration.ContinueAsync(token);
                break;
            case "previous": await _calibration.PreviousAsync(token); break;
            case "save":
                int? replace = null;
                var history = await _hearingHistory.ReadAsync(_stableKey, token);
                if (history.Count >= 10 && input.AllowReplacement)
                    replace = history.Where(x => x.Uid != _currentHearingUid).OrderBy(x => x.SavedAtUtc).FirstOrDefault()?.Uid;
                _savingHearing = true;
                try
                {
                    await _calibration.SaveHearingAsync(input.Name ?? "", input.AllowReplacement, token, replace);
                    await EnableHearingAsync(true, token);
                    await ReadHearingProfilesAsync(token);
                    if (_client is not null)
                    {
                        var after = Map(await _client.RefreshAsync(Daily.DailyRefreshScope.Features, token));
                        SnapshotChanged?.Invoke(after);
                        if (after.HearingEnabled != true) throw new InvalidOperationException("配置已保存，启用状态尚未确认，请刷新后核实。");
                    }
                    _savingHearing = false; EngineChanged(_calibration.State);
                }
                catch (Exception) when (_calibration.State.Stage == CalibrationStage.Completed)
                {
                    // The profile write finished; a failed enable/readback must not offer
                    // the user a second save that could replace another device profile.
                    _calibrationState = GetCalibration(handle) with { Stage = "Completed", Revision = ++_calibrationRevision,
                        Message = "配置已保存，启用状态尚未确认。请回到主界面刷新并选择配置。" };
                    CalibrationChanged?.Invoke(_calibrationState);
                }
                finally { _savingHearing = false; }
                break;
            case "cancel": return await CancelCalibrationAsync(handle, token);
            default: throw new InvalidDataException();
        }
        return GetCalibration(handle);
    }
    private void StartBackground(Func<Task<CalibrationSnapshot>> action)
    {
        if (!_calibrationBackground.IsCompleted) throw new InvalidOperationException("测试正在进行。");
        _calibrationBackground = RunAsync();
        async Task RunAsync()
        {
            try { await action(); }
            catch (Exception) { /* The engine publishes the failure and stops its own audio. */ }
        }
    }
    private void EngineChanged(CalibrationSnapshot snapshot)
    {
        if (_suppressCalibrationEvents || _calibrationState is not { } old) return;
        string stage = snapshot.Stage.ToString();
        if (_savingHearing && snapshot.Stage == CalibrationStage.Completed) stage = "Saving";
        if (snapshot.Workflow == CalibrationWorkflow.EarScan)
            stage = snapshot.Stage == CalibrationStage.Completed ? "ScanCompleted" : snapshot.Stage == CalibrationStage.Measuring ? "Scanning" : stage;
        else if (snapshot.Stage == CalibrationStage.Paused && snapshot.StepIndex == 12) stage = "Results";
        _calibrationState = old with { Revision = ++_calibrationRevision, Stage = stage, Uid = snapshot.Uid,
            Side = snapshot.Side, FrequencyType = snapshot.FrequencyType, StepIndex = snapshot.StepIndex, StepCount = snapshot.StepCount,
            LevelIndex = snapshot.LevelIndex, Decibels = snapshot.Decibels, RawStatus = snapshot.RawStatus,
            Message = CalibrationMessage(snapshot), FitResults = snapshot.FitResults.Select(x => new UiFitResult(x.Key, x.Value)).ToArray(),
            HearingLevels = snapshot.HearingLevels.Select(x => new UiHearingPoint(x.Side, x.Type, x.Decibels)).ToArray() };
        CalibrationChanged?.Invoke(_calibrationState);
    }
    private static string CalibrationMessage(CalibrationSnapshot state) => state.RawStatus switch
    { 0x09 => "另一台设备正在使用耳机，请断开它后重试。", 0x0F => "耳机音频通道被占用，请停止播放后重试。",
      0x10 => "耳机电量不足，请充电至 20% 以上后重试。", _ => state.Stage == CalibrationStage.Failed ? "测试未能完成，请确认双耳佩戴和连接状态后重试。" : "" };
    internal async Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
    {
        var state = GetCalibration(handle);
        if (_calibration is not null && state.Stage != "Ready")
        { try { await _calibration.CancelAsync(token); } catch (Exception) { } }
        _calibrationState = GetCalibration(handle) with { Stage = "Cancelled", Revision = ++_calibrationRevision };
        CalibrationChanged?.Invoke(_calibrationState); return _calibrationState;
    }
    internal async Task<IReadOnlyList<UiHearingProfile>> ReadHearingProfilesAsync(CancellationToken token)
    {
        if (!CalibrationAvailable("hearing") || _client is null || _calibrationContext is null) return [];
        var history = await _hearingHistory.ReadAsync(_stableKey, token);
        var profiles = history.Select(x => new UiHearingProfile(x.Uid, x.Name, false,
            (x.Version == HearingVersion.V2 ? _calibrationContext.HearingV2 : _calibrationContext.HearingV1)
                && (!_calibrationContext.EarScan || x.ScanData?.Length > 0))).ToList();
        _currentHearingUid = null;
        try
        {
            var current = CalibrationCodecs.ParseCurrentHearing((await _client.RequestAsync(0x0115, [], token)).Payload);
            if (current.Levels.Count > 0)
            {
                _currentHearingUid = current.Uid;
                bool complete = current.Levels.Count == 12;
                if (_calibrationContext.EarScan)
                {
                    var scan = CalibrationCodecs.ParseCurrentEarScan((await _client.RequestAsync(0x011E, [], token)).Payload);
                    complete &= scan.Uid == current.Uid && scan.Data.Length > 0;
                }
                profiles.RemoveAll(x => x.Uid == current.Uid);
                profiles.Insert(0, new(current.Uid, current.Name.Length > 0 ? current.Name : "耳机中的当前配置", true, complete));
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        _hearingProfiles = profiles;
        return profiles;
    }
    internal async Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token)
    {
        if (_client is null || _calibration is null || !CalibrationAvailable("hearing")) throw new InvalidOperationException("黄金听感不可用。");
        bool submitted = false;
        _suppressCalibrationEvents = true;
        try
        {
            if (uid.HasValue)
            {
                var profiles = await ReadHearingProfilesAsync(token);
                if (!profiles.Any(x => x.Uid == uid && x.CanApply)) throw new InvalidOperationException("该配置不完整或已不可用。");
                if (_currentHearingUid == uid) await _calibration.RestoreAsync(uid.Value, token);
                else
                {
                    var entry = (await _hearingHistory.ReadAsync(_stableKey, token)).FirstOrDefault(x => x.Uid == uid)
                        ?? throw new InvalidOperationException("该配置已不在本地记录中。");
                    submitted = true; await _calibration.ApplyHistoryAsync(entry, true, token);
                }
            }
            submitted = true; await EnableHearingAsync(uid.HasValue, token);
            await ReadHearingProfilesAsync(token);
            var after = Map(await _client.RefreshAsync(Daily.DailyRefreshScope.Features, token));
            bool matches = after.HearingEnabled == uid.HasValue && (!uid.HasValue || _currentHearingUid == uid);
            SnapshotChanged?.Invoke(after);
            return new() { BackendReceiptReceived = true, TargetSubmitted = submitted, SubmissionAttempted = submitted,
                Accepted = true, ReadbackMatches = matches, ConnectionState = after.ConnectionState, After = after,
                Message = matches ? "黄金听感设置已完成。" : "耳机已接受请求，请刷新确认当前配置。" };
        }
        catch (InvalidOperationException) when (!submitted) { throw; }
        catch (Exception)
        {
            return new() { BackendReceiptReceived = true, SubmissionAttempted = submitted, AcknowledgementUnknown = submitted,
                PartialEffects = submitted, FailureKind = ApplyFailure.TransportUnknown, ConnectionState = ConnectionEvidence(_last),
                Message = "听感设置结果未确认，请刷新后核实，后台不会重复提交。" };
        }
        finally { _suppressCalibrationEvents = false; }
    }
    private async Task EnableHearingAsync(bool enabled, CancellationToken token)
    {
        var reply = await _client!.RequestAsync(0x0403, [0x0B, enabled ? (byte)1 : (byte)0], token);
        CalibrationCodecs.RequireSuccess(reply.Payload);
    }
    internal async Task DisposeCalibrationAsync()
    {
        Task refresh;
        lock (_hearingRefreshGate)
        {
            _hearingRefreshLifetime?.Cancel(); _hearingRefreshNeeded = false;
            refresh = _hearingRefreshTask;
        }
        if (_client is not null) _client.Notification -= OnHearingNotification;
        try { await refresh; } catch { }
        _hearingRefreshLifetime?.Dispose(); _hearingRefreshLifetime = null;
        if (_calibration is null) return;
        var engine = _calibration; _calibration = null;
        try { await engine.CancelAsync(); } catch { }
        engine.Changed -= EngineChanged; await engine.DisposeAsync();
        try { await _calibrationBackground; } catch { }
        _calibrationState = null; _calibrationContext = null; _hearingProfiles = [];
    }
}
