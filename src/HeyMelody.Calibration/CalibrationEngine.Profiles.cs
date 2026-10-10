using System.Security.Cryptography;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Calibration;

public sealed partial class CalibrationEngine
{
    private CalibrationStage _previewReturnStage = CalibrationStage.ResultReady;

    public Task<CalibrationSnapshot> SaveHearingAsync(string name, CancellationToken token = default) => SaveHearingAsync(name, false, token);
    public Task<CalibrationSnapshot> SaveHearingAsync(string name, bool allowReplacement, CancellationToken token = default, int? replaceHistoryUid = null) => LockedAsync(async ct =>
    {
        CalibrationCodecs.ValidateName(name); CalibrationHistoryStore.ValidateName(name); RequireSaveCommands();
        if (State.Workflow is not (CalibrationWorkflow.HearingV1 or CalibrationWorkflow.HearingV2) || State.StepIndex != 12
            || !State.FiltersReady || State.Stage is not (CalibrationStage.ResultReady or CalibrationStage.Previewing))
            throw new InvalidOperationException("完成全部测听并取得完整结果后才能保存。");
        using var linked = Link(ct); ct = linked.Token;
        var savedAt = DateTimeOffset.UtcNow;
        var entry = new CalibrationHistoryEntry(_context.StableKey, State.Uid, name,
            State.Workflow == CalibrationWorkflow.HearingV1 ? HearingVersion.V1 : HearingVersion.V2,
            State.HearingLevels.ToArray(), State.ScanResult?.Data.ToArray(), savedAt,
            State.RestoreDescriptionId ?? await ChooseDescriptionAsync(State.HearingLevels, State.Curves[0].EnhanceType, ct), name);
        if (_history is not null) await _history.CheckSaveAsync(entry, replaceHistoryUid, ct);
        await CheckReplacementAsync(entry.Uid, allowReplacement, ct);
        if (_previewOwned) await StopPreviewCoreAsync(ct);
        Update(s => s with { Stage = CalibrationStage.Saving, PreviewMode = CalibrationPreviewMode.Stopped });
        await SaveAndVerifyAsync(entry, ct);
        if (_history is not null) await _history.SaveAsync(entry, ct, replaceHistoryUid);
        Update(s => s with { Stage = CalibrationStage.Completed, SavedName = name, RestoreDescriptionId = entry.RestoreDescriptionId, CanPreview = true });
    }, token, preservePreflight: true);

    public Task<CalibrationSnapshot> ApplyHistoryAsync(CalibrationHistoryEntry entry, CancellationToken token = default) => ApplyHistoryAsync(entry, false, token);
    public Task<CalibrationSnapshot> ApplyHistoryAsync(CalibrationHistoryEntry entry, bool allowReplacement, CancellationToken token = default) => LockedAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.StableKey != _context.StableKey) throw new InvalidOperationException("历史方案属于另一台耳机。");
        if (entry.Uid == 0 && string.Equals(_context.ProductId, "06EC10", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("该旧方案缺少有效标识，无法应用到 Enco X4。请重新测听或选择其他方案。");
        Require(entry.Version == HearingVersion.V1 ? _context.HearingV1 : entry.Version == HearingVersion.V2 && _context.HearingV2, 0x040E, 0x0115);
        RequireSaveCommands(); CalibrationHistoryStore.ValidateName(entry.Name); CalibrationCodecs.ValidateCompleteLevels(entry.Levels);
        if (_context.EarScan && entry.ScanData?.Length is not > 0) throw new InvalidDataException("该耳机需要方案对应的耳道扫描数据。");
        if (entry.RestoreDescriptionId is 0 or > 20) throw new InvalidDataException("历史方案的描述编号无效。");
        entry = entry with { WireName = entry.WireName ?? ChooseWireName(entry.Name, entry.SavedAtUtc) };
        CalibrationCodecs.ValidateName(entry.WireName);
        await CheckReplacementAsync(entry.Uid, allowReplacement, ct);
        NewRun(entry.Version == HearingVersion.V1 ? CalibrationWorkflow.HearingV1 : CalibrationWorkflow.HearingV2, uid: entry.Uid);
        using var linked = Link(ct); ct = linked.Token;
        Update(s => s with { Stage = CalibrationStage.Saving, StepIndex = 12, HearingLevels = Array.AsReadOnly(entry.Levels.ToArray()),
            ScanResult = entry.ScanData is null ? null : new(3, entry.ScanData.ToArray(), entry.Uid), SavedName = entry.Name, RestoreDescriptionId = entry.RestoreDescriptionId });
        if (entry.RestoreDescriptionId is null)
        {
            // Older Windows history omitted description. Obtain it from matching device data or compute from 0116.
            var current = await ReadCurrentOrNoneAsync(ct);
            byte? description = current?.Uid == entry.Uid ? await ReadDescriptionOrNoneAsync(ct) : null;
            if (description is null)
            {
                await LoadCurvesAsync(ct); description = State.RestoreDescriptionId;
            }
            entry = entry with { RestoreDescriptionId = description };
        }
        Update(s => s with { Stage = CalibrationStage.Saving });
        await SaveAndVerifyAsync(entry, ct);
        if (_history is not null) await _history.SaveAsync(entry, ct);
        Update(s => s with { Stage = CalibrationStage.Completed, RestoreDescriptionId = entry.RestoreDescriptionId });
    }, token, preservePreflight: true);

    private void RequireSaveCommands()
    {
        Require(_context.HearingV1 || _context.HearingV2, 0x0411, 0x040E, 0x0403, 0x0115, 0x0118, 0x010D);
        if (_context.EarScan) Require(true, 0x0415, 0x011E);
    }
    private async Task<HearingParameters?> ReadCurrentOrNoneAsync(CancellationToken token)
    {
        var reply = await RequestAsync(0x0115, [], token, requireSuccess: false);
        // X4's handler returns exactly status1 for missing/unusable stored hearing NV.
        if (reply.Payload.AsSpan().SequenceEqual(new byte[] { 1 })) return null;
        return CalibrationCodecs.ParseCurrentHearing(reply.Payload);
    }
    private async Task<byte?> ReadDescriptionOrNoneAsync(CancellationToken token)
    {
        var reply = await RequestAsync(0x0118, [], token, requireSuccess: false);
        if (reply.Payload.AsSpan().SequenceEqual(new byte[] { 1 })) return null;
        return CalibrationCodecs.ParseRestoreDescription(reply.Payload);
    }
    private async Task CheckReplacementAsync(int uid, bool allowReplacement, CancellationToken token)
    {
        var current = await ReadCurrentOrNoneAsync(token);
        if (current is not null && current.Uid != uid && !allowReplacement)
            throw new InvalidOperationException("耳机已有其他方案，请确认替换后保存。");
    }
    private async Task SaveAndVerifyAsync(CalibrationHistoryEntry entry, CancellationToken token)
    {
        byte description = entry.RestoreDescriptionId ?? throw new InvalidDataException("方案缺少描述编号。");
        string wireName = entry.WireName ?? entry.Name;
        await RequestAsync(0x0411, CalibrationCodecs.RestoreDescription(description), token);
        await RequestAsync(0x040E, CalibrationCodecs.Parameters(3, entry.Levels, entry.Uid, wireName), token);
        if (_context.EarScan)
            await RequestAsync(0x0415, CalibrationCodecs.SaveEarScan(entry.ScanData ?? throw new InvalidDataException("缺少耳道扫描数据。"), entry.Uid), token);
        await RequestAsync(0x0403, [0x0B, 1], token);

        var actual = CalibrationCodecs.ParseCurrentHearing((await RequestAsync(0x0115, [], token)).Payload);
        if (actual.Uid != entry.Uid || actual.Name != wireName || !Ordered(actual.Levels).SequenceEqual(Ordered(entry.Levels)))
            throw new InvalidDataException("耳机测听方案读回与保存内容不一致。");
        if (_context.EarScan)
        {
            var scan = CalibrationCodecs.ParseCurrentEarScan((await RequestAsync(0x011E, [], token)).Payload);
            if (scan.Uid != entry.Uid || !scan.Data.SequenceEqual(entry.ScanData!)) throw new InvalidDataException("耳道扫描数据读回不一致。");
        }
        if (CalibrationCodecs.ParseRestoreDescription((await RequestAsync(0x0118, [], token)).Payload) != description)
            throw new InvalidDataException("耳机方案描述读回不一致。");
        var enabled = await RequestAsync(0x010D, [1, 0x0B], token);
        var features = NotificationParsers.ParseComponentPairs(enabled.Payload.AsSpan(1), ignoredId: 0x1C);
        if (!features.TryGetValue(0x0B, out byte value) || value != 1) throw new InvalidDataException("耳机未确认黄金听感已启用。");
    }
    private static IEnumerable<HearingLevel> Ordered(IReadOnlyList<HearingLevel> levels) => levels.OrderBy(x => x.Side).ThenBy(x => x.Type);
    private async Task<byte> ChooseDescriptionAsync(IReadOnlyList<HearingLevel> levels, byte? enhanceType, CancellationToken token)
    {
        if (_history is not null)
        {
            var same = (await _history.ReadAsync(_context.StableKey, token)).FirstOrDefault(x => x.RestoreDescriptionId is > 0 and <= 20 && x.Levels.SequenceEqual(levels));
            if (same?.RestoreDescriptionId is byte existing) return existing;
        }
        if (!enhanceType.HasValue) throw new InvalidDataException("测听结果缺少增强类型。");
        return enhanceType.Value switch { 0 => (byte)RandomNumberGenerator.GetInt32(1, 7), 1 => (byte)RandomNumberGenerator.GetInt32(7, 13), _ => (byte)RandomNumberGenerator.GetInt32(13, 21) };
    }
    private static string ChooseWireName(string name, DateTimeOffset savedAt)
    {
        // Keep the display name in history. The X4 wire slot has 39 bytes, not 40 UTF-16 characters.
        try { CalibrationCodecs.ValidateName(name); return name; }
        catch (ArgumentException) { return savedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture); }
    }

    public Task<CalibrationSnapshot> RestoreAsync(int uid, CancellationToken token = default) => LockedAsync(async ct =>
    {
        Require(_context.HearingV1 || _context.HearingV2, 0x0115);
        var hearing = CalibrationCodecs.ParseCurrentHearing((await RequestAsync(0x0115, [], ct)).Payload);
        if (hearing.Uid != uid) throw new InvalidDataException("当前耳机方案的UID与所选方案不一致。");
        EarScanResult? scan = null;
        if (_context.EarScan)
        {
            scan = CalibrationCodecs.ParseCurrentEarScan((await RequestAsync(0x011E, [], ct)).Payload);
            if (scan.Uid != uid || scan.Data.Length == 0) throw new InvalidDataException("耳道扫描与测听方案UID不一致。");
        }
        byte? description = _context.Commands.Contains(0x0118) ? await ReadDescriptionOrNoneAsync(ct) : null;
        NewRun(_context.HearingV2 ? CalibrationWorkflow.HearingV2 : CalibrationWorkflow.HearingV1, scan, uid);
        Update(s => s with { Stage = CalibrationStage.Completed, StepIndex = 12, HearingLevels = hearing.Levels, SavedName = hearing.Name, RestoreDescriptionId = description });
    }, token);

    public Task<CalibrationSnapshot> PreviewAsync(CalibrationPreviewMode mode, CancellationToken token = default) => LockedAsync(async ct =>
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var linked = Link(ct); ct = linked.Token;
        if (mode == CalibrationPreviewMode.Stopped)
        {
            if (!_previewOwned) return;
            await StopPreviewCoreAsync(ct);
            Update(s => s with { Stage = _previewReturnStage, PreviewMode = mode }); return;
        }
        if (!State.CanPreview || !State.FiltersReady || State.Stage is not (CalibrationStage.ResultReady or CalibrationStage.Completed or CalibrationStage.Previewing))
            throw new InvalidOperationException("完整测听结果尚未准备好，无法试听。");
        Require(true, 0x040E); if (_context.EarScan) Require(true, 0x0415);
        // Both OEM apps overwrite temporary operation=1 parameters while their music stream keeps running.
        // Operation=2 is reserved for ending audition; inserting it during A/B would interrupt the DSP path.
        if (!_previewOwned) _previewReturnStage = State.Stage;
        Update(s => s with { Stage = CalibrationStage.Previewing });
        _previewOwned = true;
        try
        {
            await RequestAsync(0x040E, mode == CalibrationPreviewMode.Enhanced
                ? CalibrationCodecs.Parameters(1, State.HearingLevels, State.Uid) : CalibrationCodecs.Parameters(1, [], 0), ct);
            if (_context.EarScan)
                await RequestAsync(0x0415, CalibrationCodecs.EarScanParameters(1, mode == CalibrationPreviewMode.Enhanced ? State.ScanResult!.Data : [], mode == CalibrationPreviewMode.Enhanced ? State.Uid : 0), ct);
            Update(s => s with { Stage = CalibrationStage.Previewing, PreviewMode = mode });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The Host must silence local music immediately, even if owned device STOP subsequently waits for its ACK.
            Update(s => s with { Stage = CalibrationStage.Cancelling, PreviewMode = CalibrationPreviewMode.Stopped,
                RawStatus = (error as CalibrationDeviceException)?.Status ?? s.RawStatus, Message = "试听效果未确认，正在停止试听。" });
            throw;
        }
    }, token);
    private async Task StopPreviewCoreAsync(CancellationToken token)
    {
        if (!_previewOwned) return;
        Exception? first = null;
        try { await RequestAsync(0x040E, [2], token); } catch (Exception error) { first = error; }
        if (_context.EarScan) try { await RequestAsync(0x0415, [2], token); } catch (Exception error) { first ??= error; }
        if (first is not null) throw first;
        _previewOwned = false;
    }
}
