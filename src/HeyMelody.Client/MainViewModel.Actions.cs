namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private IReadOnlyList<UiApplyResult> _lastApplyAttempts = [];
    public IReadOnlyList<UiApplyResult> LastApplyAttempts => _lastApplyAttempts;

    private sealed class PendingAction(UiAction action, string group, string identity, long epoch,
        long version, long draftIdentity, IReadOnlyList<int> previousIds, CancellationToken token,
        string backendInstanceId, long deviceGeneration)
    {
        public UiAction Action { get; } = action;
        public string Group { get; } = group;
        public string Identity { get; } = identity;
        public long Epoch { get; } = epoch;
        public long Version { get; } = version;
        public long DraftIdentity { get; } = draftIdentity;
        public IReadOnlyList<int> PreviousIds { get; } = previousIds;
        public CancellationToken Token { get; } = token;
        public string BackendInstanceId { get; } = backendInstanceId;
        public long DeviceGeneration { get; } = deviceGeneration;
        public string Stage { get; set; } = "等待应用";
        public List<UiApplyResult> Attempts { get; } = [];
        public bool Confirmed { get; set; }
    }

    private static string GroupFor(string kind) => kind switch
    {
        "Equalizer" or "CustomEqualizer" or "CustomSelect" => "Equalizer",
        "Spatial" or "HiRes" => "Audio",
        "AncMode" => "Anc",
        _ => kind
    };

    private long VersionFor(string kind) => kind switch
    { "Anc" or "AncMode" => _ancVersion, "Equalizer" => _presetVersion, "Spatial" => _spatialVersion, "CustomEqualizer" => _eqVersion, _ => 0 };

    private bool? PendingToggle(string kind) => _pending.TryGetValue(GroupFor(kind), out var intent) && intent.Action.Kind == kind
        ? intent.Action.Value == 1 : null;
    private string? PendingState(string group) => _pending.TryGetValue(group, out var intent) ? intent.Stage : null;
    private bool Current(PendingAction intent) => !Closing && !_lifetime.IsCancellationRequested
        && intent.Epoch == _sessionEpoch && intent.Identity == _sessionIdentity && intent.Identity == SelectedDevice?.Identity
        && CurrentBackendTarget(intent);

    private async Task ApplyAsync(UiAction supplied) { await ApplyWithResultAsync(supplied); }

    private async Task<bool> ApplyWithResultAsync(UiAction supplied)
    {
        if (!CanWrite || !Supports(supplied) || _pending.ContainsKey(GroupFor(supplied.Kind))) return false;
        if (supplied.Kind == "CustomSelect" && EqDirty || supplied.Kind == "CustomEqualizer" && !EqDirty) return false;
        if (IsDemoSelected) { ApplyDemoAction(supplied); return true; }
        if (_preview) { SetStatus("界面演示不会发送耳机设置。"); return false; }
        var action = supplied.EqualizerEntry is not { } entry ? supplied
            : supplied with { EqualizerEntry = entry with { Bands = entry.Bands.ToArray() } };
        var intent = new PendingAction(action, GroupFor(action.Kind), _sessionIdentity!, _sessionEpoch,
            VersionFor(action.Kind), _draftIdentity, _liveSnapshot!.EqualizerEntries.Select(x => x.Id).ToArray(), _sessionWork.Token,
            _liveSnapshot.BackendInstanceId, _liveSnapshot.DeviceGeneration);
        _pending.Add(intent.Group, intent);
        if (action.Kind == "Anc") { _pendingAnc = true; _pendingAncKey = action.Value; _pendingAncPrimary = action.Value >> 8; }
        if (action.Kind == "AncMode") { _pendingAnc = true; _pendingAncKey = null; _pendingAncPrimary = action.Value; }
        if (action.Kind == "Equalizer") { _pendingPreset = true; _pendingPresetKey = SelectedPreset?.Key ?? action.Value; }
        if (action.Kind == "Spatial") { _pendingSpatial = true; _pendingSpatialKey = SelectedSpatial?.Key ?? action.Value; }
        NotifyState();
        try { await _operations.RunAsync(() => ExecuteIntentAsync(intent), OperationCoordinator.User, intent.Token); }
        catch (OperationCanceledException) { }
        catch
        {
            if (Current(intent))
            {
                var result = new UiApplyResult { FailureKind = ApplyFailure.WorkerUnavailable,
                    ConnectionState = ConnectionState.Unknown, Message = "未收到完整设置结果，请重新连接耳机后核实。" };
                intent.Attempts.Add(result);
                await FailAndRefreshAsync(intent, result, second: intent.Attempts.Count > 1);
            }
        }
        finally
        {
            _lastApplyAttempts = intent.Attempts.ToArray();
            if (_pending.TryGetValue(intent.Group, out var pending) && ReferenceEquals(pending, intent)) _pending.Remove(intent.Group);
            // Main radios represent an observed mode after the operation ends. An interrupted
            // or rejected request must not leave a selected radio that prevents a later retry.
            if (intent.Action.Kind == "AncMode" && _ancVersion == intent.Version)
            {
                SettleDraft(intent);
                if (_snapshot is { } observed) DisplaySnapshot(observed);
                else { _selectedAncMode = null; _selectedAncSubmode = null; _selectedAnc = null; }
            }
            NotifyState();
        }
        return intent.Confirmed && Current(intent);
    }

    private async Task ExecuteIntentAsync(PendingAction intent)
    {
        if (!Current(intent)) return;
        var action = intent.Action;
        if (!Current(intent) || _lifecyclePending || !Supports(action)) return;
        var first = await SubmitAsync(intent, action);
        if (!Current(intent)) return;
        if (FinishIfConfirmed(intent, first)) return;
        if (_lifecyclePending) return;
        if (action.Kind is "Anc" or "AncMode")
        {
            // A newer click is a separate intent, never consent to replay this target.
            // In particular, a matching readback without a confirmed receipt/ACK cannot
            // authorize draining a later setting after an uncertain submission.
            if (first.ReadbackMatches == true && HasVerifiedSnapshot && GoalMatches(intent, action, _snapshot!))
                FinishObserved(intent, first);
            else await FailAndRefreshAsync(intent, first, second: false);
            return;
        }
        if (first.ConnectionState != ConnectionState.Connected || !first.BackendReceiptReceived || first.FailureKind == ApplyFailure.Disconnected)
        { await FailAndRefreshAsync(intent, first, second: false); return; }
        if (first.ReadbackMatches == true && HasVerifiedSnapshot && GoalMatches(intent, intent.Action, _snapshot!))
        { FinishObserved(intent, first); return; }
        if (IsGameSoundAction(action))
        {
            // These setters can change the active sound mode and play a headset prompt.
            // Reconcile an uncertain result by reading, without replaying the user's click.
            if (first.FailureKind is not (ApplyFailure.PeerRejected or ApplyFailure.Validation or ApplyFailure.StateUnavailable or ApplyFailure.MutexConsentRequired))
            {
                intent.Stage = "正在核对"; NotifyState();
                var observed = await ReadFullAsync(intent.Epoch);
                if (!Current(intent) || _lifecyclePending) return;
                if (observed is not null && HasVerifiedSnapshot && observed.ConnectionState == ConnectionState.Connected
                    && GoalMatches(intent, action, observed))
                { FinishObserved(intent, first); return; }
            }
            await FailAndRefreshAsync(intent, first, second: false); return;
        }

        var repeatable = Repeatable(action, first, _snapshot);
        if (repeatable)
        {
            intent.Stage = "正在核对"; NotifyState();
            await Task.Delay(TimeSpan.FromMilliseconds(500), intent.Token);
            if (!Current(intent) || _lifecyclePending) return;
            var fresh = await ReadFullAsync(intent.Epoch);
            if (!Current(intent) || _lifecyclePending) return;
            if (fresh is null || !HasVerifiedSnapshot || fresh.Identity != intent.Identity || fresh.ConnectionState != ConnectionState.Connected)
            { await FailAndRefreshAsync(intent, UnknownReadResult(fresh), second: false); return; }
            if (GoalMatches(intent, action, fresh)) { RebaseDraft(intent, fresh, first.ObservedEqualizerId); FinishObserved(intent, first); return; }
            repeatable = Repeatable(action, first, fresh);
        }

        if (!repeatable)
        {
            intent.Stage = "等待确认"; NotifyState();
            var approved = await Dialogs.RunAsync(token => _confirmations.ConfirmRetryAsync("是否再次写入？",
                RetryMessage(action, first), token), intent.Token);
            if (!Current(intent)) return;
            if (!approved) { await CancelAndRefreshAsync(); return; }
        }
        if (!Current(intent) || _lifecyclePending) return;
        intent.Stage = "正在核对"; NotifyState();
        var beforeRetry = await ReadFullAsync(intent.Epoch);
        if (!Current(intent) || _lifecyclePending) return;
        if (beforeRetry is null || !HasVerifiedSnapshot || beforeRetry.ConnectionState != ConnectionState.Connected)
        { await FailAndRefreshAsync(intent, UnknownReadResult(beforeRetry), second: true); return; }
        if (GoalMatches(intent, action, beforeRetry))
        { RebaseDraft(intent, beforeRetry, first.ObservedEqualizerId); FinishObserved(intent, first); return; }

        // New EQs are reconciled by content before any second creation request.
        if (action is { Kind: "CustomEqualizer", EqualizerEntry: { Id: 0 } draft })
        {
            var matches = beforeRetry.EqualizerEntries.Where(x => !intent.PreviousIds.Contains(x.Id) && SameContent(x, draft)).ToArray();
            if (matches.Length > 1 || matches.Length == 0 && beforeRetry.EqualizerEntries.Any(x => x.Name == draft.Name))
            {
                await FailAndRefreshAsync(intent, new() { BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                    FailureKind = ApplyFailure.Validation, Message = "EQ 条目无法唯一确认或名称已存在，未再次创建。" }, second: true); return;
            }
            if (matches.Length == 1)
            {
                RebaseDraft(intent, beforeRetry, matches[0].Id);
                action = new("CustomSelect", matches[0].Id);
            }
        }
        if (!Supports(action))
        {
            await FailAndRefreshAsync(intent, new() { BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                FailureKind = ApplyFailure.Validation, Message = "设备最新能力或条目已改变，无法再次写入。" }, second: true); return;
        }
        var second = await SubmitAsync(intent, action);
        if (!Current(intent)) return;
        if (FinishIfConfirmed(intent, second)) return;
        await FailAndRefreshAsync(intent, second, second: true,
            second.ReadbackMatches == true && second.After is { } after && !GoalMatches(intent, intent.Action, after)
                ? "耳机条目在核对后发生了变化，回读内容与本次保存的 EQ 不一致。" : null);
    }

    private async Task<UiApplyResult> SubmitAsync(PendingAction intent, UiAction action)
    {
        intent.Stage = intent.Attempts.Count == 0 ? "正在应用" : "正在重试（1/1）";
        SetStatus(intent.Attempts.Count == 0 ? "正在应用设置并回读…" : "正在再次写入并回读…");
        NotifyState();
        UiApplyResult result;
        try
        {
            result = EventService is { } events
                ? await events.ApplyAsync(intent.Identity, intent.DeviceGeneration, action, _lifetime.Token)
                : await _service.ApplyAsync(action, _lifetime.Token);
        }
        catch (OperationCanceledException) when (Closing) { throw; }
        catch { result = new() { FailureKind = ApplyFailure.WorkerUnavailable, ConnectionState = ConnectionState.Unknown,
                Message = "后台未返回完整设置结果，设置可能已生效。" }; }
        intent.Attempts.Add(result);
        if (!Current(intent)) return result;
        if (result.After is { } snapshot)
        {
            if (snapshot.Identity != intent.Identity)
            { result.ConnectionState = ConnectionState.Unknown; result.FailureKind = ApplyFailure.WorkerUnavailable; InvalidateSnapshot(); }
            else if (EventService is not null && (snapshot.BackendInstanceId != intent.BackendInstanceId
                || snapshot.DeviceGeneration != intent.DeviceGeneration))
            {
                result.ConnectionState = ConnectionState.Unknown;
                result.FailureKind = ApplyFailure.WorkerUnavailable;
                result.Message = "设置回读所属的控制会话已经变化，请核实当前状态。";
            }
            else
            {
                DisplaySnapshot(snapshot);
                if (_snapshot is { } current) RebaseDraft(intent, current, result.ObservedEqualizerId);
            }
        }
        if (!string.IsNullOrWhiteSpace(result.Message)) Log(result.Message);
        return result;
    }

    private bool FinishIfConfirmed(PendingAction intent, UiApplyResult result)
    {
        var confirmed = result.BackendReceiptReceived && result.ConnectionState == ConnectionState.Connected
            && result.ReadbackMatches == true && (result.Accepted || !result.SubmissionAttempted)
            && !result.AcknowledgementUnknown && !result.PartialEffects;
        if (!confirmed || !HasVerifiedSnapshot || result.After is not { } after || !GoalMatches(intent, intent.Action, after)) return false;
        intent.Confirmed = true;
        SettleDraft(intent);
        _actionWarnings.Remove(intent.Group);
        DisplaySnapshot(_snapshot ?? after);
        SetStatus(result.SubmissionAttempted ? "设置已确认，回读与请求一致。" : "耳机已处于所选设置。");
        Log(_status); return true;
    }

    private void SettleDraft(PendingAction intent)
    {
        if (VersionFor(intent.Action.Kind) == intent.Version)
        {
            if (intent.Action.Kind is "Anc" or "AncMode") { _pendingAnc = false; _pendingAncKey = _pendingAncPrimary = null; }
            if (intent.Action.Kind == "Equalizer") { _pendingPreset = false; _pendingPresetKey = null; }
            if (intent.Action.Kind == "Spatial") { _pendingSpatial = false; _pendingSpatialKey = null; }
            if (intent.Action.Kind == "CustomEqualizer" && intent.DraftIdentity == _draftIdentity) SetEqDirty(false);
        }
    }

    private void FinishObserved(PendingAction intent, UiApplyResult result)
    {
        SettleDraft(intent);
        if (_snapshot is { } snapshot) DisplaySnapshot(snapshot);
        bool acknowledgementConfirmed = result.Accepted && !result.AcknowledgementUnknown;
        var message = acknowledgementConfirmed ? "当前状态已符合请求。" : result.AcknowledgementUnknown || result.AckStatus is null
            ? "响应未确认，当前状态已符合请求；未再次写入。" : "请求曾被设备拒绝，当前状态已符合目标；未再次写入。";
        if (!acknowledgementConfirmed) _actionWarnings[intent.Group] = message;
        else _actionWarnings.Remove(intent.Group);
        SetStatus(message); Log(message);
    }

    private async Task FailAndRefreshAsync(PendingAction intent, UiApplyResult result, bool second, string? detail = null)
    {
        if (!Current(intent)) return;
        var message = second ? "再次写入仍未完成。关闭提示后将刷新设备信息。"
            : result.ConnectionState == ConnectionState.Disconnected || result.FailureKind == ApplyFailure.Disconnected
                ? "耳机连接已中断，设置未完成。关闭提示后将刷新设备信息。"
                : "无法确认耳机控制连接或操作结果，设置可能已生效。关闭提示后将刷新设备信息，请核实连接。";
        if (!string.IsNullOrWhiteSpace(result.Message)) message += Environment.NewLine + Sanitize(result.Message);
        if (detail is not null) message += Environment.NewLine + detail;
        _actionWarnings[intent.Group] = message; intent.Stage = "设置未完成"; NotifyState(); Log(message);
        await Dialogs.RunAsync(token => _confirmations.AlertAsync("设置未完成", message, token), intent.Token);
        if (!Current(intent) || _lifecyclePending) return;
        await ReadFullAsync(intent.Epoch);
    }

    private async Task CancelAndRefreshAsync()
    {
        if (Closing || _lifecyclePending) return;
        AdvanceSession(); ResetSelection();
        SetStatus("已取消操作，正在恢复耳机实际状态…");
        await ReadFullAsync(_sessionEpoch);
    }

    // Called only inside an already-owned communication slot; never enqueue recursively.
    private async Task<UiSnapshot?> ReadFullAsync(long epoch)
    {
        try
        {
            var snapshot = await ReadSelectedAsync(RefreshScope.Full, _lifetime.Token);
            if (epoch != _sessionEpoch || Closing) return null;
            AdoptRead(snapshot);
            return EventService is not null ? _snapshot : snapshot;
        }
        catch (OperationCanceledException) when (Closing) { return null; }
        catch
        {
            if (epoch != _sessionEpoch || Closing) return null;
            InvalidateSnapshot(); SetStatus("设备信息未能刷新，请检查连接并重新选择耳机。", true);
            // A restarted Host owns a new identity map. Enumerating is read-only and must
            // not transfer the old intent to a similarly named device.
            try
            {
                var devices = ComposeDevices(await _service.EnumerateAsync(_lifetime.Token));
                if (epoch != _sessionEpoch || Closing) return null;
                var changed = PresentDevices(devices, _selectedDevice?.Identity);
                if (changed || _snapshot is null) PresentSelectedBackendSnapshot();
                NotifyState();
            }
            catch { }
            return null;
        }
    }

    private static bool IsGameSoundAction(UiAction action) => action.Kind == "Feature"
        && action.FeatureId is "game-sound" or "game-sound-enabled";
    private string RetryMessage(UiAction action, UiApplyResult result)
    {
        var message = "上次设置未能完整确认。是否再次写入本次选择的设置？";
        if (action is { Kind: "CustomEqualizer", EqualizerEntry.Id: 0 }) message += "\n上次 EQ 可能已经创建；再次写入可能创建重复条目。将先核对已有条目。";
        if (!string.IsNullOrWhiteSpace(result.Message)) message += Environment.NewLine + Sanitize(result.Message);
        return message;
    }

    private bool Repeatable(UiAction action, UiApplyResult result, UiSnapshot? snapshot)
    {
        if (!result.BackendReceiptReceived || result.PrerequisiteSubmitted
            || result.FailureKind is ApplyFailure.Validation or ApplyFailure.StateUnavailable or ApplyFailure.MutexConsentRequired
                or ApplyFailure.WorkerUnavailable or ApplyFailure.Disconnected
            || snapshot is null || !HasVerifiedSnapshot || !Supports(action)) return false;
        return action.Kind switch
        {
            "Anc" => snapshot.Anc is not null,
            "Equalizer" => snapshot.Equalizer is not null,
            "DualConnection" => snapshot.DualConnection is not null,
            "GameMode" => snapshot.GameMode is not null,
            "Spatial" => snapshot.Spatial is not null,
            "HiRes" => snapshot.HiRes is not null,
            "CustomSelect" => snapshot.EqualizerEntries.Any(x => x.Id == action.Value),
            "CustomEqualizer" => action.EqualizerEntry is { Id: > 0 } entry && snapshot.EqualizerEntries.Any(x =>
                x.Id == entry.Id && x.MinimumGain == entry.MinimumGain && x.MaximumGain == entry.MaximumGain),
            _ => false
        };
    }

    private static bool SameContent(UiEqEntry observed, UiEqEntry requested) => observed.Name == requested.Name
        && observed.MinimumGain == requested.MinimumGain && observed.MaximumGain == requested.MaximumGain
        && observed.Bands.SequenceEqual(requested.Bands);
    private static bool GoalMatches(PendingAction intent, UiAction action, UiSnapshot snapshot) => action.Kind switch
    {
        "Anc" => snapshot.Anc == action.Value,
        "AncMode" => snapshot.AncPrimary == action.Value,
        "Equalizer" => snapshot.Equalizer == action.Value,
        "Spatial" => snapshot.Spatial == action.Value,
        "DualConnection" => snapshot.DualConnection == (action.Value == 1),
        "GameMode" => snapshot.GameMode == (action.Value == 1),
        "HiRes" => snapshot.HiRes == (action.Value == 1),
        "Feature" => snapshot.FeatureSections.SelectMany(s => s.Controls).Any(c => c.Id == action.FeatureId &&
            (c.Kind == "Text" ? c.Text == action.Text : c.Value == action.Value)),
        "CustomSelect" => snapshot.EqualizerEntries.Any(x => x.Id == action.Value && x.Selected),
        "CustomEqualizer" => action.EqualizerEntry is { } requested && snapshot.EqualizerEntries.Count(x => x.Selected && SameContent(x, requested)
            && (requested.Id == 0 ? !intent.PreviousIds.Contains(x.Id) : x.Id == requested.Id)) == 1,
        _ => false
    };

    private void RebaseDraft(PendingAction intent, UiSnapshot snapshot, int? observedId)
    {
        if (intent.Action is not { Kind: "CustomEqualizer", EqualizerEntry: { } entry }
            || intent.DraftIdentity != _draftIdentity) return;
        var matches = snapshot.EqualizerEntries.Where(x => SameContent(x, entry)
            && (entry.Id == 0 ? !intent.PreviousIds.Contains(x.Id) : x.Id == entry.Id)).ToArray();
        var id = observedId ?? (matches.Length == 1 ? matches[0].Id : (int?)null);
        if (id is not null && snapshot.EqualizerEntries.Any(x => x.Id == id)) _eqDraftId = id;
    }

    private static UiApplyResult UnknownReadResult(UiSnapshot? snapshot) => new()
    {
        BackendReceiptReceived = true, ConnectionState = snapshot?.ConnectionState ?? ConnectionState.Unknown,
        FailureKind = snapshot?.ConnectionState == ConnectionState.Disconnected ? ApplyFailure.Disconnected : ApplyFailure.ReadbackUnavailable,
        Message = "未取得同一设备的有效最新状态，未再次写入。", After = snapshot
    };
}
