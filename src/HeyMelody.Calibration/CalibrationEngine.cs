using System.Security.Cryptography;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Calibration;

/// <summary>A single device workflow. It shares the caller's Bluetooth reader; it never creates a second reader.</summary>
public sealed class CalibrationEngine : IAsyncDisposable
{
    public static IReadOnlyList<int> V2Decibels { get; } = Array.AsReadOnly(new[] { -55, -49, -41, -35, -25, -19, -11, -5, 3, 7, 13, 17 });
    private readonly ICalibrationTransport _transport;
    private readonly CalibrationDeviceContext _context;
    private readonly CalibrationHistoryStore? _history;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private CancellationTokenSource? _run, _levelDelay;
    private TaskCompletionSource<IReadOnlyDictionary<byte, byte>>? _fit;
    private TaskCompletionSource<EarScanResult>? _scan;
    private HearingFilterAssembler? _filters;
    private HearingV1Search _search = new();
    private CalibrationSnapshot _state;
    private long _revision, _lastLevelSent;
    private int _desiredLevel = 5, _disposed;
    private int? _baselineUid;
    private bool _started, _stopSent;
    public CalibrationSnapshot State { get { lock (_stateGate) return _state; } }
    public event Action<CalibrationSnapshot>? Changed;

    public CalibrationEngine(ICalibrationTransport transport, CalibrationDeviceContext context, CalibrationHistoryStore? history = null)
    {
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.StableKey)) throw new ArgumentException("Stable device identity is required.", nameof(context));
        _transport = transport; _context = context with { Commands = context.Commands.ToHashSet(), Events = context.Events.ToHashSet() }; _history = history;
        _state = new() { Generation = context.Generation };
        transport.Notification += OnNotification; transport.Disconnected += OnDisconnected;
    }

    public async Task<CalibrationSnapshot> RunFitAsync(CancellationToken token = default)
    {
        Require(_context.Fit, CalibrationCodecs.FitCommand, 0x0107);
        RequireEvent(CalibrationCodecs.FitEvent);
        CancellationTokenSource run; bool created = false;
        await _gate.WaitAsync(token);
        try
        {
            run = NewRun(CalibrationWorkflow.Fit); created = true; _fit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var linked = Link(token);
            await CheckWearAsync(linked.Token);
            Publish(State with { Stage = CalibrationStage.Starting });
            await StartOwnedAsync(CalibrationCodecs.FitCommand, CalibrationCodecs.Fit(true), linked.Token);
            Publish(State with { Stage = CalibrationStage.Measuring });
        }
        catch (Exception error) { if (created) { await CleanupFailedOperationAsync(); Fail(error); } throw; }
        finally { _gate.Release(); }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, run.Token);
            var result = await _fit!.Task.WaitAsync(TimeSpan.FromSeconds(15), linked.Token);
            await _gate.WaitAsync(linked.Token);
            try { EnsureRun(run); Publish(State with { Stage = CalibrationStage.Completed, FitResults = result }); _started = false; return State; }
            finally { _gate.Release(); }
        }
        catch (Exception error)
        {
            await EndFailedRunAsync(run, error); throw;
        }
    }

    public async Task<CalibrationSnapshot> StartEarScanAsync(CancellationToken token = default)
    {
        Require(_context.EarScan, CalibrationCodecs.HearingDetectionCommand, CalibrationCodecs.CurrentEarScanCommand, 0x0107);
        RequireEvent(CalibrationCodecs.EarScanEvent);
        CancellationTokenSource run; bool created = false;
        await _gate.WaitAsync(token);
        try
        {
            run = NewRun(CalibrationWorkflow.EarScan); created = true; _scan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var linked = Link(token); await CheckWearAsync(linked.Token);
            Publish(State with { Stage = CalibrationStage.Starting });
            await StartOwnedAsync(CalibrationCodecs.HearingDetectionCommand, CalibrationCodecs.EarScanDetection(true, State.Uid), linked.Token);
            Publish(State with { Stage = CalibrationStage.Measuring });
        }
        catch (Exception error) { if (created) { await CleanupFailedOperationAsync(); Fail(error); } throw; }
        finally { _gate.Release(); }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, run.Token);
            EarScanResult result = await _scan!.Task.WaitAsync(TimeSpan.FromSeconds(15), linked.Token);
            await _gate.WaitAsync(linked.Token);
            try { EnsureRun(run); Publish(State with { Stage = CalibrationStage.Completed, ScanResult = result }); _started = false; return State; }
            finally { _gate.Release(); }
        }
        catch (Exception error) { await EndFailedRunAsync(run, error); throw; }
    }

    public Task<CalibrationSnapshot> BeginHearingAsync(HearingVersion version, CancellationToken token = default) => LockedAsync(async ct =>
    {
        Require(version == HearingVersion.V1 ? _context.HearingV1 : version == HearingVersion.V2 && _context.HearingV2,
            CalibrationCodecs.HearingDetectionCommand, CalibrationCodecs.HearingParametersCommand, CalibrationCodecs.CurrentHearingCommand, 0x0107);
        EarScanResult? scan = State.Workflow == CalibrationWorkflow.EarScan && State.Stage == CalibrationStage.Completed ? State.ScanResult : null;
        NewRun(version == HearingVersion.V1 ? CalibrationWorkflow.HearingV1 : CalibrationWorkflow.HearingV2, scan);
        using var linked = Link(ct); ct = linked.Token;
        await CheckWearAsync(ct); Publish(State with { Stage = CalibrationStage.Starting });
        _baselineUid = CalibrationCodecs.ParseCurrentHearing((await RequestAsync(CalibrationCodecs.CurrentHearingCommand, [], ct)).Payload).Uid;
        await StartOwnedAsync(CalibrationCodecs.HearingDetectionCommand, CalibrationCodecs.HearingDetection(true), ct);
        _search = new(); _desiredLevel = 5;
        await SendFrequencyAsync(version == HearingVersion.V1 ? 0 : V2Decibels[5], ct);
        Publish(State with { Stage = CalibrationStage.AwaitingAnswer, Decibels = version == HearingVersion.V1 ? 0 : V2Decibels[5] });
    }, token);

    public Task<CalibrationSnapshot> AnswerAsync(bool heard, CancellationToken token = default) => LockedAsync(async ct =>
    {
        RequireInput(CalibrationWorkflow.HearingV1);
        using var linked = Link(ct); ct = linked.Token;
        await PauseAsync(ct);
        bool finished = _search.Answer(heard);
        Publish(State with { Decibels = _search.Current, LowerBound = _search.Lower, UpperBound = _search.Upper });
        if (finished) await AdvanceAsync(_search.Current, ct);
        else
        {
            await SendFrequencyAsync(_search.Current, ct);
            Publish(State with { Stage = CalibrationStage.AwaitingAnswer });
        }
    }, token);

    public async Task<CalibrationSnapshot> SetLevelAsync(int levelIndex, bool released, CancellationToken token = default)
    {
        if (levelIndex < 0 || levelIndex >= V2Decibels.Count) throw new ArgumentOutOfRangeException(nameof(levelIndex));
        RequireInput(CalibrationWorkflow.HearingV2);
        int step = State.StepIndex; Interlocked.Exchange(ref _desiredLevel, levelIndex);
        var delay = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, _run!.Token);
        Interlocked.Exchange(ref _levelDelay, delay)?.Cancel();
        try
        {
            if (!released)
            {
                long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastLevelSent);
                if (elapsed < 300) await Task.Delay(TimeSpan.FromMilliseconds(300 - elapsed), delay.Token);
            }
            delay.Token.ThrowIfCancellationRequested();
            Interlocked.CompareExchange(ref _levelDelay, null, delay);
            using var operation = Link(token);
            return await LockedAsync(async ct =>
            {
                if (State.StepIndex != step || State.Stage != CalibrationStage.AwaitingAnswer) return;
                int latest = Volatile.Read(ref _desiredLevel);
                await SendFrequencyAsync(V2Decibels[latest], ct);
                Interlocked.Exchange(ref _lastLevelSent, Environment.TickCount64);
                Publish(State with { LevelIndex = latest, Decibels = V2Decibels[latest] });
            }, operation.Token);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { return State; }
        finally { Interlocked.CompareExchange(ref _levelDelay, null, delay); delay.Dispose(); }
    }

    public Task<CalibrationSnapshot> ContinueAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        RequireInput(CalibrationWorkflow.HearingV2); _levelDelay?.Cancel();
        using var linked = Link(ct); ct = linked.Token;
        int level = Volatile.Read(ref _desiredLevel);
        if (State.Decibels != V2Decibels[level]) await SendFrequencyAsync(V2Decibels[level], ct);
        await PauseAsync(ct); await AdvanceAsync(V2Decibels[level], ct);
    }, token);

    public Task<CalibrationSnapshot> PreviousAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        if (State.Workflow is not (CalibrationWorkflow.HearingV1 or CalibrationWorkflow.HearingV2) || State.StepIndex <= 0
            || State.Stage is not (CalibrationStage.AwaitingAnswer or CalibrationStage.Paused))
            throw new InvalidOperationException("No previous hearing step is available.");
        _levelDelay?.Cancel(); using var linked = Link(ct); ct = linked.Token;
        if (State.Stage == CalibrationStage.AwaitingAnswer) await PauseAsync(ct);
        int previous = State.StepIndex - 1;
        HearingLevel saved = State.HearingLevels[previous];
        int db = State.Workflow == CalibrationWorkflow.HearingV1 ? 0 : saved.Decibels;
        _search = new(); _desiredLevel = State.Workflow == CalibrationWorkflow.HearingV1 ? 5 : V2Decibels.ToList().IndexOf(db);
        if (_desiredLevel < 0) throw new InvalidDataException("Saved step is not a V2 level.");
        Publish(State with { Stage = CalibrationStage.Paused, StepIndex = previous, Side = saved.Side, FrequencyType = saved.Type,
            HearingLevels = Array.AsReadOnly(State.HearingLevels.Take(previous).ToArray()), Decibels = db, LevelIndex = _desiredLevel, LowerBound = -60, UpperBound = 20 });
        await SendFrequencyAsync(db, ct); Publish(State with { Stage = CalibrationStage.AwaitingAnswer });
    }, token);

    public Task<CalibrationSnapshot> SaveHearingAsync(string name, CancellationToken token = default) => SaveHearingAsync(name, false, token);
    public Task<CalibrationSnapshot> SaveHearingAsync(string name, bool allowReplacement, CancellationToken token = default, int? replaceHistoryUid = null) => LockedAsync(async ct =>
    {
        CalibrationHistoryStore.ValidateName(name);
        if (State.Workflow is not (CalibrationWorkflow.HearingV1 or CalibrationWorkflow.HearingV2) || State.StepIndex != 12 || State.Stage != CalibrationStage.Paused)
            throw new InvalidOperationException("Complete all hearing steps before saving.");
        using var linked = Link(ct); ct = linked.Token;
        var entry = new CalibrationHistoryEntry(_context.StableKey, State.Uid, name,
            State.Workflow == CalibrationWorkflow.HearingV1 ? HearingVersion.V1 : HearingVersion.V2,
            State.HearingLevels.ToArray(), State.ScanResult?.Data.ToArray(), DateTimeOffset.UtcNow);
        if (_history is not null) await _history.CheckSaveAsync(entry, replaceHistoryUid, ct);
        await CheckReplacementAsync(entry.Uid, allowReplacement, ct);
        Publish(State with { Stage = CalibrationStage.Saving });
        await SaveAndVerifyAsync(entry, ct);
        await StopAsync(ct); if (_history is not null) await _history.SaveAsync(entry, ct, replaceHistoryUid);
        Publish(State with { Stage = CalibrationStage.Completed, SavedName = name });
    }, token, preservePreflight: true);

    public Task<CalibrationSnapshot> ApplyHistoryAsync(CalibrationHistoryEntry entry, CancellationToken token = default) => ApplyHistoryAsync(entry, false, token);
    public Task<CalibrationSnapshot> ApplyHistoryAsync(CalibrationHistoryEntry entry, bool allowReplacement, CancellationToken token = default) => LockedAsync(async ct =>
    {
        if (entry.StableKey != _context.StableKey) throw new InvalidOperationException("History belongs to a different device.");
        Require(entry.Version == HearingVersion.V1 ? _context.HearingV1 : _context.HearingV2,
            CalibrationCodecs.HearingParametersCommand, CalibrationCodecs.CurrentHearingCommand);
        CalibrationHistoryStore.ValidateName(entry.Name); CalibrationCodecs.ValidateLevels(entry.Levels);
        if (entry.Levels.Count != 12) throw new InvalidDataException("Incomplete hearing history.");
        if (_context.EarScan && entry.ScanData?.Length is not > 0) throw new InvalidDataException("This device requires matching ear scan data.");
        await CheckReplacementAsync(entry.Uid, allowReplacement, ct);
        NewRun(entry.Version == HearingVersion.V1 ? CalibrationWorkflow.HearingV1 : CalibrationWorkflow.HearingV2, uid: entry.Uid);
        using var linked = Link(ct); ct = linked.Token;
        Publish(State with { Stage = CalibrationStage.Saving });
        await SaveAndVerifyAsync(entry, ct);
        Publish(State with { Stage = CalibrationStage.Completed, StepIndex = 12, HearingLevels = Array.AsReadOnly(entry.Levels.ToArray()),
            ScanResult = entry.ScanData is null ? null : new(3, entry.ScanData.ToArray(), entry.Uid), SavedName = entry.Name });
    }, token, preservePreflight: true);

    private async Task CheckReplacementAsync(int uid, bool allowReplacement, CancellationToken token)
    {
        HearingParameters current = CalibrationCodecs.ParseCurrentHearing((await RequestAsync(CalibrationCodecs.CurrentHearingCommand, [], token)).Payload);
        if (current.Levels.Count != 0 && current.Uid != uid && !allowReplacement)
            throw new InvalidOperationException("Earphones have a different calibration. Confirm replacement before saving.");
    }

    public Task<CalibrationSnapshot> RestoreAsync(int uid, CancellationToken token = default) => LockedAsync(async ct =>
    {
        Require(_context.HearingV1 || _context.HearingV2, CalibrationCodecs.CurrentHearingCommand);
        var reply = await RequestAsync(CalibrationCodecs.CurrentHearingCommand, [], ct);
        var hearing = CalibrationCodecs.ParseCurrentHearing(reply.Payload);
        if (hearing.Uid != uid || hearing.Levels.Count != 12) throw new InvalidDataException("Current device calibration does not match this UID.");
        NewRun(_context.HearingV2 ? CalibrationWorkflow.HearingV2 : CalibrationWorkflow.HearingV1, uid: uid);
        EarScanResult? scan = null;
        if (_context.EarScan && _context.Commands.Contains(CalibrationCodecs.CurrentEarScanCommand))
        {
            scan = CalibrationCodecs.ParseCurrentEarScan((await RequestAsync(CalibrationCodecs.CurrentEarScanCommand, [], ct)).Payload);
            if (scan.Uid != uid || scan.Data.Length == 0) throw new InvalidDataException("Ear scan UID does not match hearing UID.");
        }
        Publish(State with { Stage = CalibrationStage.Completed, StepIndex = 12, HearingLevels = hearing.Levels, ScanResult = scan, SavedName = hearing.Name });
    }, token);

    public Task<CalibrationSnapshot> ReadCurvesAsync(CancellationToken token = default) => LockedAsync(async ct =>
    {
        Require(_context.HearingV1 || _context.HearingV2, CalibrationCodecs.FilterCommand);
        if (State.HearingLevels.Count != 12) throw new InvalidOperationException("Hearing levels are incomplete.");
        _filters = new(State.Uid); Publish(State with { Curves = [] });
        Packet reply = await RequestAsync(CalibrationCodecs.FilterCommand, CalibrationCodecs.FilterRequest(State.HearingLevels, State.Uid), ct);
        AddFilter(reply); // Additional 8116 packets must be forwarded by the shared transport.
    }, token);

    public async Task<CalibrationSnapshot> CancelAsync(CancellationToken token = default)
    {
        _run?.Cancel(); _levelDelay?.Cancel(); _fit?.TrySetCanceled(); _scan?.TrySetCanceled();
        await _gate.WaitAsync(token);
        try
        {
            if (State.Stage is CalibrationStage.Idle or CalibrationStage.Cancelled) return State;
            Publish(State with { Stage = CalibrationStage.Cancelling });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await StopAsync(timeout.Token);
            Publish(State with { Stage = CalibrationStage.Cancelled }); return State;
        }
        catch (Exception error) { Fail(error); throw; }
        finally { _gate.Release(); }
    }

    private async Task SaveAndVerifyAsync(CalibrationHistoryEntry entry, CancellationToken token)
    {
        if (entry.ScanData is not null)
        {
            Require(_context.EarScan, CalibrationCodecs.EarScanSaveCommand, CalibrationCodecs.CurrentEarScanCommand);
            await RequestAsync(CalibrationCodecs.EarScanSaveCommand, CalibrationCodecs.SaveEarScan(entry.ScanData, entry.Uid), token);
            var scan = CalibrationCodecs.ParseCurrentEarScan((await RequestAsync(CalibrationCodecs.CurrentEarScanCommand, [], token)).Payload);
            if (scan.Uid != entry.Uid || !scan.Data.SequenceEqual(entry.ScanData)) throw new InvalidDataException("Ear scan save was not confirmed.");
        }
        await RequestAsync(CalibrationCodecs.HearingParametersCommand, CalibrationCodecs.Parameters(3, entry.Levels, entry.Uid, entry.Name), token);
        var actual = CalibrationCodecs.ParseCurrentHearing((await RequestAsync(CalibrationCodecs.CurrentHearingCommand, [], token)).Payload);
        if (actual.Uid != entry.Uid || actual.Name != entry.Name || !actual.Levels.OrderBy(x => x.Side).ThenBy(x => x.Type).SequenceEqual(entry.Levels.OrderBy(x => x.Side).ThenBy(x => x.Type)))
            throw new InvalidDataException("Hearing save was not confirmed.");
    }
    private async Task AdvanceAsync(int decibels, CancellationToken token)
    {
        var levels = State.HearingLevels.Append(new(State.Side, State.FrequencyType, checked((sbyte)decibels))).ToArray();
        int next = State.StepIndex + 1; _search = new(); _desiredLevel = 5;
        Publish(State with { HearingLevels = Array.AsReadOnly(levels), StepIndex = next, Stage = CalibrationStage.Paused,
            Side = (byte)(next < 6 ? 1 : 2), FrequencyType = (byte)(next % 6 + 1), LowerBound = -60, UpperBound = 20, LevelIndex = 5 });
        if (next == 12) return;
        int db = State.Workflow == CalibrationWorkflow.HearingV1 ? 0 : V2Decibels[5];
        await SendFrequencyAsync(db, token); Publish(State with { Stage = CalibrationStage.AwaitingAnswer, Decibels = db });
    }
    private async Task PauseAsync(CancellationToken token)
    {
        await RequestAsync(CalibrationCodecs.HearingParametersCommand, CalibrationCodecs.Parameters(4, [], State.Uid), token);
        Publish(State with { Stage = CalibrationStage.Paused });
    }
    private Task<Packet> SendFrequencyAsync(int db, CancellationToken token) => RequestAsync(CalibrationCodecs.HearingParametersCommand,
        CalibrationCodecs.Frequency(new(State.Side, State.FrequencyType, checked((sbyte)db))), token);
    private async Task CheckWearAsync(CancellationToken token)
    {
        Publish(State with { Stage = CalibrationStage.CheckingWear });
        var reply = await RequestAsync(0x0107, [], token);
        var components = NotificationParsers.ParseComponentPairs(reply.Payload.AsSpan(1));
        if (!components.TryGetValue(1, out byte left) || !components.TryGetValue(2, out byte right) || (left & 2) == 0 || (right & 2) == 0)
            throw new InvalidOperationException("Insert both earbuds before starting calibration.");
    }
    private async Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_transport.IsConnected) throw new IOException("Earphone connection is unavailable.");
        if (!_context.Commands.Contains(command)) throw new NotSupportedException($"Calibration command 0x{command:X4} is unavailable.");
        Packet reply = await _transport.RequestAsync(command, payload, token);
        token.ThrowIfCancellationRequested();
        if (reply.Command != (command | 0x8000)) throw new InvalidDataException("Unmatched calibration response.");
        CalibrationCodecs.RequireSuccess(reply.Payload); return reply;
    }
    private async Task StartOwnedAsync(ushort command, byte[] payload, CancellationToken token)
    {
        _started = true; // A lost/cancelled ACK leaves an ambiguous started operation to clean up.
        try { await RequestAsync(command, payload, token); }
        catch (CalibrationDeviceException) { _started = false; throw; } // Explicit rejection did not start our operation.
    }
    private async Task CleanupFailedOperationAsync()
    {
        if (!_started || _stopSent) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { await StopAsync(timeout.Token); } catch (Exception) { }
    }
    private CancellationTokenSource NewRun(CalibrationWorkflow workflow, EarScanResult? scan = null, int? uid = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (State.Stage is CalibrationStage.CheckingWear or CalibrationStage.Starting or CalibrationStage.Measuring or CalibrationStage.AwaitingAnswer or CalibrationStage.Paused or CalibrationStage.Saving)
            throw new InvalidOperationException("Cancel the current calibration before starting another.");
        _run?.Cancel(); _run?.Dispose(); _levelDelay?.Cancel(); _run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _stopSent = false; _started = false; _filters = null;
        _baselineUid = null;
        int id = uid ?? scan?.Uid ?? BitConverter.ToInt32(RandomNumberGenerator.GetBytes(4));
        Publish(new() { Generation = _context.Generation, Workflow = workflow, Stage = CalibrationStage.Starting, Uid = id, ScanResult = scan }); return _run;
    }
    private CancellationTokenSource Link(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _run?.Token ?? _lifetime.Token, _lifetime.Token);
    private void EnsureRun(CancellationTokenSource run)
    { if (!ReferenceEquals(_run, run) || run.IsCancellationRequested) throw new OperationCanceledException(); }
    private void RequireInput(CalibrationWorkflow workflow)
    { if (State.Workflow != workflow || State.Stage != CalibrationStage.AwaitingAnswer || State.StepIndex >= 12) throw new InvalidOperationException("Calibration is not accepting this input."); }
    private void Require(bool declaration, params ushort[] commands)
    { if (!declaration || commands.Any(x => !_context.Commands.Contains(x))) throw new NotSupportedException("This device does not declare the requested calibration."); }
    private void RequireEvent(byte id)
    { if (!_context.Events.Contains(id)) throw new NotSupportedException("Required calibration event is unavailable."); }
    private async Task<CalibrationSnapshot> LockedAsync(Func<CancellationToken, Task> action, CancellationToken token, bool preservePreflight = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this); await _gate.WaitAsync(token);
        long initialRevision = State.Revision;
        try { await action(token); return State; }
        catch (Exception error)
        {
            // A rejected name, capacity or replacement must not terminate the session
            // or send cleanup SET. Saving/NewRun is published only after these checks.
            if (preservePreflight && State.Revision == initialRevision) throw;
            await CleanupFailedOperationAsync();
            Fail(error); throw;
        }
        finally { _gate.Release(); }
    }
    private async Task StopAsync(CancellationToken token)
    {
        if (_stopSent || !_started) return; _stopSent = true;
        if (!_transport.IsConnected) { _started = false; return; }
        if (State.Workflow == CalibrationWorkflow.Fit) await RequestAsync(CalibrationCodecs.FitCommand, CalibrationCodecs.Fit(false), token);
        else if (State.Workflow == CalibrationWorkflow.EarScan) await RequestAsync(CalibrationCodecs.HearingDetectionCommand, CalibrationCodecs.EarScanDetection(false, State.Uid), token);
        else
        {
            if (_context.Commands.Contains(CalibrationCodecs.CurrentHearingCommand))
            {
                HearingParameters current = CalibrationCodecs.ParseCurrentHearing((await RequestAsync(CalibrationCodecs.CurrentHearingCommand, [], token)).Payload);
                if (current.Levels.Count != 0 && current.Uid != State.Uid && current.Uid != _baselineUid)
                {
                    _started = false;
                    Publish(State with { Message = "Device calibration changed elsewhere; temporary cleanup was skipped." }); return;
                }
            }
            await RequestAsync(CalibrationCodecs.HearingParametersCommand, CalibrationCodecs.Parameters(2, [], State.Uid), token);
            await RequestAsync(CalibrationCodecs.HearingDetectionCommand, CalibrationCodecs.HearingDetection(false), token);
        }
        _started = false;
    }
    private async Task EndFailedRunAsync(CancellationTokenSource run, Exception error)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(run, _run) || run.IsCancellationRequested) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await StopAsync(timeout.Token); } catch (Exception) { }
            Fail(error);
        }
        finally { _gate.Release(); }
    }
    private void OnNotification(Packet packet)
    {
        try
        {
            if (_run?.IsCancellationRequested != false) return;
            if (State.Workflow == CalibrationWorkflow.Fit && CalibrationCodecs.TryEvent(packet, CalibrationCodecs.FitEvent, out var fit))
                _fit?.TrySetResult(CalibrationCodecs.ParseFit(fit));
            if (State.Workflow == CalibrationWorkflow.EarScan && CalibrationCodecs.TryEvent(packet, CalibrationCodecs.EarScanEvent, out var data))
            {
                var scan = CalibrationCodecs.ParseEarScan(data);
                if (scan.Uid == State.Uid && scan.Data.Length != 0) _scan?.TrySetResult(scan);
            }
            if (packet.Command == 0x8116) AddFilter(packet);
        }
        catch (Exception error) { _fit?.TrySetException(error); _scan?.TrySetException(error); }
    }
    private void AddFilter(Packet packet)
    {
        lock (_stateGate)
        {
            HearingFilterCurve? curve = _filters?.Add(CalibrationCodecs.ParseFilter(packet.Payload)); if (curve is null) return;
            if (curve.Uid != _state.Uid) return;
            Publish(_state with { Curves = Array.AsReadOnly(_state.Curves.Where(x => x.Key != curve.Key).Append(curve).ToArray()) });
        }
    }
    private void OnDisconnected()
    {
        _run?.Cancel(); _levelDelay?.Cancel(); var error = new IOException("Earphone connection ended during calibration.");
        _fit?.TrySetException(error); _scan?.TrySetException(error); _started = false; Fail(error);
    }
    private void Fail(Exception error)
    {
        if (error is OperationCanceledException && State.Stage is CalibrationStage.Cancelling or CalibrationStage.Cancelled) return;
        Publish(State with { Stage = error is OperationCanceledException ? CalibrationStage.Cancelled : CalibrationStage.Failed,
            RawStatus = (error as CalibrationDeviceException)?.Status, Message = error.Message });
    }
    private void Publish(CalibrationSnapshot snapshot)
    {
        lock (_stateGate) { _state = snapshot with { Revision = ++_revision }; snapshot = _state; }
        if (Changed is { } observers)
            foreach (Action<CalibrationSnapshot> observer in observers.GetInvocationList())
                try { observer(snapshot); } catch (Exception) { /* Observers cannot terminate the Bluetooth read pump. */ }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await CancelAsync(); } catch (Exception) { }
        _lifetime.Cancel(); _transport.Notification -= OnNotification; _transport.Disconnected -= OnDisconnected;
        _run?.Dispose(); _lifetime.Dispose();
    }
}
