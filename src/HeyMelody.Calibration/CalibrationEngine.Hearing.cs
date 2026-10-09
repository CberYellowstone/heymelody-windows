using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Calibration;

public sealed partial class CalibrationEngine
{
    private readonly object _filterGate = new();
    private readonly Dictionary<ushort, byte> _filterSequences = [];
    private readonly Dictionary<ushort, List<Packet>> _pendingFilterPackets = [];
    private readonly HashSet<ushort> _filterRequestsStarted = [];
    public Task<CalibrationSnapshot> BeginHearingAsync(HearingVersion version, CancellationToken token = default) => LockedAsync(async ct =>
    {
        Require(version == HearingVersion.V1 ? _context.HearingV1 : version == HearingVersion.V2 && _context.HearingV2, 0x0109, 0x040D, 0x040E, 0x0116);
        EarScanResult? scan = State.Workflow == CalibrationWorkflow.EarScan && State.Stage == CalibrationStage.Completed ? State.ScanResult : null;
        if (_context.EarScan && scan is null) throw new InvalidOperationException("请先完成耳道扫描，再开始测听。");
        if (_context.EarScan) Require(true, 0x011F);
        NewRun(version == HearingVersion.V1 ? CalibrationWorkflow.HearingV1 : CalibrationWorkflow.HearingV2, scan);
        using var linked = Link(ct); ct = linked.Token;
        await CheckWearAsync(ct);
        if (_context.Commands.Contains(0x0115)) _ = await ReadCurrentOrNoneAsync(ct);
        Update(s => s with { Stage = CalibrationStage.Starting });
        _hearingOwned = true;
        try { await RequestAsync(0x040D, CalibrationCodecs.HearingDetection(true), ct); }
        catch (CalibrationDeviceException) { _hearingOwned = false; throw; }
        ThrowIfInterrupted(); _search = new();
        await Task.Delay(_timings.StartToneDelay, ct);
        await SendFrequencyAsync(version == HearingVersion.V1 ? 0 : -19, 5, ct);
        Update(s => s.Stage == CalibrationStage.Interrupted ? s : s with { Stage = CalibrationStage.AwaitingAnswer });
    }, token);

    public Task<CalibrationSnapshot> AnswerAsync(bool heard, CancellationToken token = default) => LockedAsync(async ct =>
    {
        RequireInput(CalibrationWorkflow.HearingV1);
        ArmIdle();
        using var linked = Link(ct); ct = linked.Token;
        await PauseToneAsync(ct); ThrowIfInterrupted();
        bool finished = _search.Answer(heard);
        Update(s => s with { Decibels = _search.Current, LowerBound = _search.Lower, UpperBound = _search.Upper });
        await Task.Delay(_timings.StepPauseDelay, ct);
        if (finished) await AdvanceAsync(_search.Current, ct);
        else await SendFrequencyAsync(_search.Current, 5, ct);
    }, token);

    public async Task<CalibrationSnapshot> SetLevelAsync(int levelIndex, bool released, CancellationToken token = default)
    {
        if (levelIndex < 0 || levelIndex >= V2Decibels.Count) throw new ArgumentOutOfRangeException(nameof(levelIndex));
        RequireInput(CalibrationWorkflow.HearingV2);
        ArmIdle();
        int step = State.StepIndex; long epoch = Interlocked.Increment(ref _levelEpoch);
        var delay = Link(token); CancelSafely(Interlocked.Exchange(ref _levelDelay, delay));
        Update(s => s with { DesiredLevelIndex = levelIndex, ToneAcknowledged = false });
        try
        {
            if (!released)
            {
                long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastLevelSent);
                if (elapsed < _timings.LevelThrottle.TotalMilliseconds)
                    await Task.Delay(TimeSpan.FromMilliseconds(_timings.LevelThrottle.TotalMilliseconds - elapsed), delay.Token);
            }
            return await LockedAsync(async ct =>
            {
                if (epoch != Interlocked.Read(ref _levelEpoch) || State.StepIndex != step || State.Stage != CalibrationStage.AwaitingAnswer) return;
                await PauseToneAsync(ct); ThrowIfInterrupted();
                if (epoch != Interlocked.Read(ref _levelEpoch)) return;
                await SendFrequencyAsync(V2Decibels[levelIndex], levelIndex, ct, epoch);
            }, delay.Token, transientCancellation: true);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { return State; }
        finally { Interlocked.CompareExchange(ref _levelDelay, null, delay); delay.Dispose(); }
    }

    public Task<CalibrationSnapshot> ContinueAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        RequireInput(CalibrationWorkflow.HearingV2);
        if (!State.ToneAcknowledged || State.LevelIndex != State.DesiredLevelIndex || State.Decibels != V2Decibels[State.DesiredLevelIndex])
            throw new InvalidOperationException("请等待当前档位发送成功后，再进入下一项。");
        ArmIdle();
        int db = State.Decibels; CancelLevel();
        using var linked = Link(ct); ct = linked.Token;
        await PauseToneAsync(ct); ThrowIfInterrupted();
        await Task.Delay(_timings.StepPauseDelay, ct);
        await AdvanceAsync(db, ct);
    }, token);

    public Task<CalibrationSnapshot> PreviousAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        if (State.Workflow is not (CalibrationWorkflow.HearingV1 or CalibrationWorkflow.HearingV2)
            || State.StepIndex <= 0 || State.StepIndex >= 12 || State.Stage != CalibrationStage.AwaitingAnswer)
            throw new InvalidOperationException("当前没有可以返回的测听项目。");
        CancelLevel(); using var linked = Link(ct); ct = linked.Token;
        ArmIdle();
        await PauseToneAsync(ct); ThrowIfInterrupted();
        await Task.Delay(_timings.StepPauseDelay, ct);
        int previous = State.StepIndex - 1, db = State.Workflow == CalibrationWorkflow.HearingV1 ? 0 : -19;
        _search = new();
        Update(s => s with { StepIndex = previous, Side = (byte)(previous / 6 + 1), FrequencyType = (byte)(previous % 6 + 1),
            HearingLevels = Array.AsReadOnly(s.HearingLevels.Take(previous).ToArray()), Decibels = db, LevelIndex = 5, DesiredLevelIndex = 5,
            ToneAcknowledged = false, LowerBound = -60, UpperBound = 20 });
        await SendFrequencyAsync(db, 5, ct);
    }, token);

    public Task<CalibrationSnapshot> ResumeAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        if (State.Stage != CalibrationStage.Interrupted || !State.CanResume || !_hearingOwned)
            throw new InvalidOperationException("耳机尚未恢复，暂时无法继续测听。");
        using var linked = Link(ct); ct = linked.Token;
        ArmIdle();
        await CheckWearAsync(ct, publish: false);
        if (State.Stage != CalibrationStage.Interrupted || !State.CanResume) return;
        int db = State.Workflow == CalibrationWorkflow.HearingV2 ? V2Decibels[State.DesiredLevelIndex] : _search.Current;
        Update(s => s with { Stage = CalibrationStage.AwaitingAnswer, CanResume = false, RawStatus = null, Message = null });
        await PauseToneAsync(ct); await SendFrequencyAsync(db, State.DesiredLevelIndex, ct);
    }, token);

    public Task<CalibrationSnapshot> PauseHearingAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        if (State.Workflow is not (CalibrationWorkflow.HearingV1 or CalibrationWorkflow.HearingV2)
            || State.Stage != CalibrationStage.AwaitingAnswer) throw new InvalidOperationException("当前测听不能暂停。");
        CancelLevel(); using var linked = Link(ct);
        ArmIdle();
        await PauseToneAsync(linked.Token); ThrowIfInterrupted();
        Update(s => s with { Stage = CalibrationStage.Interrupted, CanResume = true, Message = "测听已暂停，可继续当前频段。" });
    }, token);

    private void RequireInput(CalibrationWorkflow workflow)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (State.Workflow != workflow || State.Stage != CalibrationStage.AwaitingAnswer || State.StepIndex >= 12)
            throw new InvalidOperationException("当前测听未等待输入，请检查测试状态。");
    }
    private async Task PauseToneAsync(CancellationToken token)
    {
        Update(s => s with { ToneAcknowledged = false });
        await RequestAsync(0x040E, CalibrationCodecs.PauseHearing(), token);
    }
    private async Task SendFrequencyAsync(int db, int index, CancellationToken token, long? epoch = null)
    {
        ThrowIfInterrupted(); int step = State.StepIndex;
        await RequestAsync(0x040E, CalibrationCodecs.Frequency(new(State.Side, State.FrequencyType, checked((sbyte)db))), token);
        ThrowIfInterrupted(); Interlocked.Exchange(ref _lastLevelSent, Environment.TickCount64);
        Update(s => s with { LevelIndex = index, Decibels = db,
            ToneAcknowledged = s.StepIndex == step && (!epoch.HasValue || epoch.Value == Interlocked.Read(ref _levelEpoch)) && s.DesiredLevelIndex == index });
        ArmIdle();
    }
    private async Task AdvanceAsync(int db, CancellationToken token)
    {
        ThrowIfInterrupted(); int next = State.StepIndex + 1;
        _search = new();
        Update(s => s with { HearingLevels = Array.AsReadOnly(s.HearingLevels.Append(new(s.Side, s.FrequencyType, checked((sbyte)db))).ToArray()),
            StepIndex = next, Side = next < 12 ? (byte)(next / 6 + 1) : s.Side, FrequencyType = next < 12 ? (byte)(next % 6 + 1) : s.FrequencyType,
            LowerBound = -60, UpperBound = 20, LevelIndex = 5, DesiredLevelIndex = 5, ToneAcknowledged = false });
        if (next == 12)
        {
            DisarmIdle();
            await StopHearingDetectionAsync(token);
            await Task.Delay(_timings.StartToneDelay, token);
            await LoadCurvesAsync(token); return;
        }
        await Task.Delay(_timings.StepPauseDelay, token);
        await SendFrequencyAsync(State.Workflow == CalibrationWorkflow.HearingV1 ? 0 : -19, 5, token);
    }

    public Task<CalibrationSnapshot> ReadCurvesAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        if (State.HearingLevels.Count != 12 || HasOwnedAudio) throw new InvalidOperationException("请完成并停止测听后，再获取结果。");
        if (_run?.IsCancellationRequested != false) { _run?.Dispose(); _run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); }
        using var linked = Link(ct); await LoadCurvesAsync(linked.Token);
    }, token);

    private async Task LoadCurvesAsync(CancellationToken token)
    {
        Require(_context.HearingV1 || _context.HearingV2, 0x0116);
        CalibrationCodecs.ValidateCompleteLevels(State.HearingLevels);
        if (_context.EarScan && State.ScanResult is not { Data.Length: > 0 }) throw new InvalidDataException("缺少本次测听对应的耳道扫描数据。");
        if (_context.EarScan) Require(true, 0x011F);
        _filters = new(State.Uid); _scanFilters = _context.EarScan ? new(State.Uid) : null;
        _curvesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_filterGate)
        {
            _filterSequences.Clear(); _pendingFilterPackets.Clear(); _filterRequestsStarted.Clear();
            _filterRequestsStarted.Add(0x8116);
        }
        Update(s => s with { Stage = CalibrationStage.LoadingCurves, FiltersReady = false, CanPreview = false, Curves = [], EarScanCurves = [], Message = null });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timings.FilterTimeout);
        try
        {
            RegisterFilterResponse(await RequestAsync(0x0116, CalibrationCodecs.FilterRequest(State.HearingLevels, State.Uid), deadline.Token));
            if (_context.EarScan)
            {
                var scan = State.ScanResult!;
                lock (_filterGate) _filterRequestsStarted.Add(0x811F);
                RegisterFilterResponse(await RequestAsync(0x011F, CalibrationCodecs.EarScanFilterRequest(scan.Data, State.Uid), deadline.Token));
            }
            await _curvesReady.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("未能及时收到完整的双耳测听和耳道扫描结果，请重新获取结果。"); }
        byte description = await ChooseDescriptionAsync(State.HearingLevels, State.Curves[0].EnhanceType, token);
        Update(s => s with { Stage = CalibrationStage.ResultReady, FiltersReady = true, CanPreview = true, RestoreDescriptionId = description,
            HasInterEarDifference = HasInterEarDifference(s.HearingLevels), RawStatus = null, Message = null });
    }
    private void AddFilter(Packet packet)
    {
        if (State.Stage != CalibrationStage.LoadingCurves) return;
        lock (_filterGate)
        {
            if (!_filterRequestsStarted.Contains(packet.Command)) return;
            if (_filterSequences.TryGetValue(packet.Command, out byte sequence))
            {
                if (packet.Sequence != sequence) return;
            }
            else
            {
                // The reader may forward fragments before RequestAsync resumes with its first reply.
                // Keep them unparsed: a previous retry's status1 must not fail the current query.
                if (!_pendingFilterPackets.TryGetValue(packet.Command, out var pending))
                    _pendingFilterPackets.Add(packet.Command, pending = []);
                if (pending.Any(x => x.Sequence == packet.Sequence && x.Payload.AsSpan().SequenceEqual(packet.Payload))) return;
                if (pending.Count >= 256 || pending.Sum(x => (long)x.Payload.Length) + packet.Payload.Length > 1024 * 1024)
                    throw new FormatException("等待结果响应时收到过多分片，请重新获取结果。");
                pending.Add(packet with { Payload = packet.Payload.ToArray() }); return;
            }
        }
        AddFilterCore(packet);
    }
    private void RegisterFilterResponse(Packet reply)
    {
        Packet[] pending;
        lock (_filterGate)
        {
            _filterSequences[reply.Command] = reply.Sequence;
            pending = _pendingFilterPackets.Remove(reply.Command, out var buffered)
                ? buffered.Where(x => x.Sequence == reply.Sequence).ToArray() : [];
        }
        AddFilterCore(reply);
        foreach (var packet in pending) AddFilterCore(packet);
    }
    private void AddFilterCore(Packet packet)
    {
        lock (_filterGate)
        {
            if (State.Stage != CalibrationStage.LoadingCurves || !_filterSequences.TryGetValue(packet.Command, out byte expected)
                || packet.Sequence != expected) return;
            HearingFilterCurve? curve = packet.Command switch
            {
                0x8116 => _filters?.Add(CalibrationCodecs.ParseFilter(packet.Payload)),
                0x811F => _scanFilters?.Add(CalibrationCodecs.ParseEarScanFilter(packet.Payload)), _ => null
            };
            if (curve is null || curve.Uid != State.Uid) return;
            Update(s => curve.SourceCommand == 0x0116 ? s with { Curves = Array.AsReadOnly(new[] { curve }) }
                : s with { EarScanCurves = Array.AsReadOnly(new[] { curve }) });
            if (State.Curves.Count == 1 && (!_context.EarScan || State.EarScanCurves.Count == 1)) _curvesReady?.TrySetResult(true);
        }
    }
    private bool HasInterEarDifference(IReadOnlyList<HearingLevel> levels)
    {
        if (_context.HearingJudgeDb != 1) return false;
        for (byte type = 1; type <= 6; type++)
        {
            int left = V2Decibels.ToList().IndexOf(levels.Single(x => x.Side == 1 && x.Type == type).Decibels);
            int right = V2Decibels.ToList().IndexOf(levels.Single(x => x.Side == 2 && x.Type == type).Decibels);
            if (left >= 0 && right >= 0 && Math.Abs(left - right) >= 3) return true;
        }
        return false;
    }
}
