using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    private readonly object _stateGate = new();
    private readonly object _publishGate = new();
    private readonly CancellationTokenSource _monitorLifetime = new();
    private readonly Dictionary<string, (long Version, Action<DailySnapshot> Apply)> _changes = [];
    private readonly HashSet<DailyRefreshScope> _invalidated = [];
    private long _notificationVersion, _revision, _lastPublishedRevision;
    private volatile bool _monitoringRequested;
    private DailySession? _registeredSession;
    private int _refreshRunner;
    private Task _notificationRefresh = Task.CompletedTask;
    private string? _monitoringWarning;

    /// <summary>Snapshots are independent copies; notifications never count as a setter ACK.</summary>
    public event Action<DailySnapshot>? SnapshotChanged;

    /// <summary>Requests only APK-whitelisted notification subscriptions after initial identity verification.</summary>
    public async Task StartMonitoringAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _monitoringRequested = true;
            if (_session is not { Usable: true } || _commands is null)
                await ReadFreshAsync(token);
            await RegisterMonitoringAsync(token);
            PublishMonitoringWarning();
        }
        finally { _gate.Release(); }
    }

    private async Task RegisterMonitoringAsync(CancellationToken token)
    {
        if (!_monitoringRequested || _session is not { Usable: true } session || _commands is null
            || ReferenceEquals(_registeredSession, session)) return;
        var capability = await session.RequestAsync(0x0200, [], token);
        if (!capability.Matched || !session.Usable) throw new IOException("Notification capability request failed.");
        // A rejected/unknown registration leaves passive 050x reception usable, without guessed fallback.
        if (capability.Status != 0)
        {
            _monitoringWarning = "设备未接受状态通知能力查询，主动状态通知订阅不可用。";
            _registeredSession = session; return;
        }
        byte[] offered = NotificationParsers.ParseSupportedEvents(capability.Payload!);
        byte[] ids = offered.Where(id => id is 1 or 2 or 3 or 4 or 5 or 6 or 14 or 15).ToArray();
        if (ids.Length != 0)
        {
            if (_commands.Supports(0x0205))
            {
                var reply = await session.RequestAsync(0x0205, [(byte)ids.Length, .. ids], token);
                if (!reply.Matched || !session.Usable) throw new IOException("Notification registration failed.");
                if (reply.Status != 0) _monitoringWarning = "设备拒绝了状态通知订阅。";
                else lock (_stateGate) _registeredEvents.UnionWith(ids);
            }
            else foreach (byte id in ids)
            {
                var reply = await session.RequestAsync(0x0201, [id], token);
                if (!reply.Matched || !session.Usable) throw new IOException("Notification registration failed.");
                if (reply.Status != 0) _monitoringWarning = "设备拒绝了部分状态通知订阅。";
                else lock (_stateGate) _registeredEvents.Add(id);
            }
        }
        _registeredSession = session;
    }

    private void PublishMonitoringWarning()
    {
        DailySnapshot? snapshot = null;
        lock (_stateGate)
        {
            if (_latest is null || _monitoringWarning is null || _latest.Warnings.Contains(_monitoringWarning)) return;
            _latest = Clone(_latest); _latest.Warnings.Add(_monitoringWarning);
            _latest.Revision = ++_revision; snapshot = Clone(_latest);
        }
        Publish(snapshot);
    }

    private long NotificationVersion { get { lock (_stateGate) return _notificationVersion; } }
    private void Publish(DailySnapshot snapshot)
    {
        // A consumer must not be able to terminate the sole Bluetooth reader or mutate the cache.
        lock (_publishGate)
        {
            if (snapshot.Revision <= _lastPublishedRevision) return;
            _lastPublishedRevision = snapshot.Revision;
            foreach (Action<DailySnapshot> handler in SnapshotChanged?.GetInvocationList() ?? [])
                try { handler(Clone(snapshot)); } catch (Exception) { }
        }
    }
    private void OnSessionEnded(DailySession source, Exception failure)
    {
        if (ReferenceEquals(_session, source)) Disconnected?.Invoke();
        DailySnapshot? snapshot = null;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_session, source) || _disposed || _latest is null) return;
            _latest = Clone(_latest); _latest.Connected = false;
            _latest.Error = "耳机控制连接已中断。";
            _latest.Revision = ++_revision; _latest.ObservedAtUtc = DateTimeOffset.UtcNow;
            snapshot = Clone(_latest);
        }
        Publish(snapshot);
    }
    private void OnNotification(DailySession source, Packet packet)
    {
        if (!ReferenceEquals(_session, source)) return;
        foreach (Action<Packet> handler in Notification?.GetInvocationList() ?? [])
            try { handler(packet); } catch { }
        DeviceNotification? notification;
        try { if (!NotificationParsers.TryParse(packet, out notification)) return; }
        catch (FormatException) { QueueRefresh(DailyRefreshScope.Full); return; }
        var updates = new Dictionary<string, Action<DailySnapshot>>();
        var refreshes = new HashSet<DailyRefreshScope>();
        try
        {
            byte[] data = notification!.Data;
            switch (notification.Kind)
            {
                case DeviceNotificationKind.Battery:
                    DateTimeOffset batteryObserved = DateTimeOffset.UtcNow;
                    var battery = DailyParsers.ParseBattery([0, .. data]);
                    foreach (var pair in battery.RawComponents)
                    {
                        byte component = pair.Key, raw = pair.Value;
                        updates[$"battery:{component}"] = s => MergeBattery(s, component, raw, batteryObserved);
                    }
                    break;
                case DeviceNotificationKind.Placement:
                    DateTimeOffset placementObserved = DateTimeOffset.UtcNow;
                    foreach (var pair in NotificationParsers.ParseComponentPairs(data))
                    {
                        byte component = pair.Key, raw = pair.Value;
                        updates[$"placement:{component}"] = s => ApplyPlacement(s, component, raw, placementObserved);
                    }
                    break;
                case DeviceNotificationKind.Anc:
                    if (_profile is null) { refreshes.Add(DailyRefreshScope.Anc); break; }
                    var anc = DailyParsers.ParseAnc([0, .. data], _profile);
                    updates["anc"] = s => s.Anc = new() { Kind = anc.Kind, ModeType = anc.ModeType,
                        RawMask = anc.RawMask.ToArray(), Primary = anc.Primary, Level = anc.Level };
                    if (anc.Primary is null) refreshes.Add(DailyRefreshScope.Anc);
                    break;
                case DeviceNotificationKind.Features:
                    foreach (var pair in NotificationParsers.ParseComponentPairs(data))
                    {
                        byte id = pair.Key, value = pair.Value;
                        updates[$"feature:{id}"] = s => s.RawFeatures[id] = value;
                        if (id is 0x1B or 0x18) refreshes.Add(DailyRefreshScope.Features);
                    }
                    break;
                case DeviceNotificationKind.Bass:
                    if (_profile?.FunctionVersion("bassEngineSupport") != 1 || _commands?.Supports(0x0124) != true) break;
                    var bass = DailyFeatureCodecs.ParseBass([0, .. data]);
                    updates["bass"] = s => s.BassEngine = bass;
                    refreshes.Add(DailyRefreshScope.Features);
                    break;
                case DeviceNotificationKind.Preset:
                    byte preset = DailyParsers.ParsePreset([0, .. data]);
                    updates["preset"] = s => s.PresetId = preset;
                    // A built-in ID cannot establish which independent custom EQ entry is selected.
                    refreshes.Add(DailyRefreshScope.Equalizer);
                    break;
                case DeviceNotificationKind.CustomEqualizers:
                    var equalizers = DailyParsers.ParseEqualizers([0, .. data]);
                    updates["equalizers"] = s => { s.CustomEqualizers = CopyEqualizers(equalizers); s.CustomEqualizersRead = true; };
                    break;
                case DeviceNotificationKind.GameMode:
                case DeviceNotificationKind.DualConnection:
                case DeviceNotificationKind.SpatialInvalidated:
                    // Legacy game layouts and multipoint information are not modern 010D/012A snapshots.
                    refreshes.Add(DailyRefreshScope.Features); break;
                case DeviceNotificationKind.Capabilities:
                    // Event 15 is a public-event bitmap, not the 0100 command-capability bitmap.
                    refreshes.Add(DailyRefreshScope.Full); break;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            refreshes.Add(notification!.Kind switch {
                DeviceNotificationKind.Battery => DailyRefreshScope.Battery,
                DeviceNotificationKind.Anc => DailyRefreshScope.Anc,
                DeviceNotificationKind.Preset or DeviceNotificationKind.CustomEqualizers => DailyRefreshScope.Equalizer,
                DeviceNotificationKind.Placement => DailyRefreshScope.Information,
                _ => DailyRefreshScope.Features });
            updates.Clear();
        }
        DailySnapshot? changed = null;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_session, source) || _disposed) return;
            if (updates.Count != 0)
            {
                long version = ++_notificationVersion;
                foreach (var update in updates) _changes[update.Key] = (version, update.Value);
                if (_latest is { Connected: true, ProfileVerified: true })
                {
                    var next = Clone(_latest);
                    foreach (var update in updates.Values) update(next);
                    ResolveCapabilities(next);
                    next.ObservedAtUtc = DateTimeOffset.UtcNow; next.Revision = ++_revision;
                    _latest = next; changed = Clone(next);
                }
            }
        }
        if (changed is not null) Publish(changed);
        foreach (var scope in refreshes) QueueRefresh(scope);
    }

    private static List<CustomEqualizer> CopyEqualizers(IEnumerable<CustomEqualizer> entries) => entries.Select(x => new CustomEqualizer {
        Id = x.Id, Name = x.Name, Selected = x.Selected, MinimumGain = x.MinimumGain,
        MaximumGain = x.MaximumGain, Bands = x.Bands.ToList() }).ToList();
    private static void MergeBattery(DailySnapshot snapshot, byte component, byte raw, DateTimeOffset observed)
    {
        BatterySnapshot part = DailyParsers.ParseBattery([0, 1, component, raw]);
        snapshot.Battery ??= new(); snapshot.Battery.RawComponents[component] = raw;
        if (component == 1) { snapshot.Battery.Left = part.Left; snapshot.LeftBatteryObservedAtUtc = observed; }
        else if (component == 2) { snapshot.Battery.Right = part.Right; snapshot.RightBatteryObservedAtUtc = observed; }
        else if (component == 3) { snapshot.Battery.Case = part.Case; snapshot.CaseBatteryObservedAtUtc = observed; }
    }
    private static void ApplyPlacement(DailySnapshot s, byte component, byte raw, DateTimeOffset observed)
    {
        s.RawStatusComponents[component] = raw;
        if (component == 1) { s.LeftInCase = (raw & 1) == 0; s.LeftInEar = (raw & 2) != 0; s.LeftPlacementObservedAtUtc = observed; }
        else if (component == 2) { s.RightInCase = (raw & 1) == 0; s.RightInEar = (raw & 2) != 0; s.RightPlacementObservedAtUtc = observed; }
        else if (component == 3) { s.CaseOpen = (raw & 4) != 0; s.CaseObservedAtUtc = observed; }
    }

    private void QueueRefresh(DailyRefreshScope scope)
    {
        lock (_stateGate)
        {
            if (!_monitoringRequested || _disposed) return;
            _invalidated.Add(scope);
            if (Interlocked.CompareExchange(ref _refreshRunner, 1, 0) != 0) return;
            _notificationRefresh = Task.Run(RefreshNotificationsAsync);
        }
    }
    private async Task RefreshNotificationsAsync()
    {
        try
        {
            // A single deferred reader coalesces bursts; it never replays settings.
            await Task.Yield();
            while (!_monitorLifetime.IsCancellationRequested)
            {
                await _gate.WaitAsync(_monitorLifetime.Token);
                try
                {
                    DailyRefreshScope[] scopes;
                    lock (_stateGate)
                    {
                        scopes = _invalidated.Contains(DailyRefreshScope.Full) ? [DailyRefreshScope.Full] : _invalidated.ToArray();
                        _invalidated.Clear();
                    }
                    if (_disposed || _session is not { Usable: true }) return;
                    foreach (var scope in scopes) await ReadFreshAsync(_monitorLifetime.Token, scope);
                }
                finally { _gate.Release(); }
                lock (_stateGate) if (_invalidated.Count == 0) return;
            }
        }
        catch (OperationCanceledException) when (_monitorLifetime.IsCancellationRequested) { }
        finally
        {
            lock (_stateGate)
            {
                Interlocked.Exchange(ref _refreshRunner, 0);
                if (_invalidated.Count != 0 && !_disposed) QueueRefresh(_invalidated.First());
            }
        }
    }
    private static string ReadGroup(ushort command) => command switch {
        0x0106 => "battery:", 0x0107 => "placement:", 0x010C => "anc", 0x010D => "feature:",
        0x010F => "preset", 0x0122 => "equalizers", 0x012A => "spatial", 0x0124 => "bass", _ => "information" };
}
