namespace HeyMelodyWindows.App;

// Feature rows and EQ choices stay editable while a write awaits the device. Like the ANC queue, a newer
// choice replaces the unsent one with the same key, targets go out one at a time after the previous one
// was confirmed, and any unconfirmed result discards the rest of the group: nothing is retried or replayed.
public sealed partial class MainViewModel
{
    private sealed record WriteTarget(string Group, string Key, UiAction Action, long Version, string Identity, long Epoch,
        string BackendInstanceId, long DeviceGeneration, long? ResourceRevision, TaskCompletionSource Done);

    private readonly List<WriteTarget> _writeTargets = [];
    private readonly HashSet<string> _drainingGroups = [];
    private int? _pendingEqEntryId;

    /// <summary>The custom EQ entry the user chose last while it is still unconfirmed; null once observed or discarded.</summary>
    public int? PendingEqEntryId => _pendingEqEntryId;

    private Task QueueFeatureAsync(UiAction action, long resourceRevision) => QueueWriteAsync(action, action.FeatureId!, resourceRevision);

    private Task QueueEqActionAsync(UiAction action)
    {
        if (!CanWrite || !Supports(action) || action.Kind == "CustomSelect" && EqDirty) return Task.CompletedTask;
        if (action.Kind == "CustomSelect") { SelectedPreset = null; _pendingEqEntryId = action.Value; }
        else _pendingEqEntryId = null;
        // The latest click owns the displayed choice until it is observed or discarded.
        _pendingPreset = true; _pendingPresetKey = action.Kind == "Equalizer" ? action.Value : null; _presetVersion++;
        // Presets and custom entries share one slot: only the last click is ever sent.
        return QueueWriteAsync(action, "Equalizer", null);
    }

    private Task QueueWriteAsync(UiAction action, string key, long? resourceRevision)
    {
        if (!CanWrite || !Supports(action) || _liveSnapshot is not { } snapshot) return Task.CompletedTask;
        var group = GroupFor(action.Kind);
        var target = new WriteTarget(group, key, action, group == "Equalizer" ? _presetVersion : 0, _sessionIdentity!, _sessionEpoch,
            snapshot.BackendInstanceId, snapshot.DeviceGeneration, resourceRevision, new(TaskCreationOptions.RunContinuationsAsynchronously));
        // A superseded caller completes without writing; its editor settles unless it was edited again.
        foreach (var old in _writeTargets.Where(t => t.Group == group && t.Key == key).ToArray())
        { _writeTargets.Remove(old); old.Done.TrySetResult(); }
        _writeTargets.Add(target);
        NotifyState();
        if (_drainingGroups.Add(group)) _ = DrainWriteTargetsAsync(group);
        return target.Done.Task;
    }

    private bool WriteContextMatches(WriteTarget target) => target.Epoch == _sessionEpoch
        && target.Identity == _sessionIdentity && target.Identity == SelectedDevice?.Identity
        && _liveSnapshot is { } snapshot && snapshot.Identity == target.Identity
        && snapshot.BackendInstanceId == target.BackendInstanceId && snapshot.DeviceGeneration == target.DeviceGeneration
        && (target.ResourceRevision is not { } revision || snapshot.ResourceRevision == revision);

    private static bool SameContext(WriteTarget target, PendingAction intent) => target.Epoch == intent.Epoch
        && target.Identity == intent.Identity && target.BackendInstanceId == intent.BackendInstanceId
        && target.DeviceGeneration == intent.DeviceGeneration;

    private async Task DrainWriteTargetsAsync(string group)
    {
        try
        {
            while (true)
            {
                // An intent started outside the queue (saving a custom EQ) is chained, never overlapped.
                if (_pending.TryGetValue(group, out var running))
                {
                    var confirmed = await running.Done.Task;
                    if (!confirmed && _writeTargets.FirstOrDefault(t => t.Group == group) is { } head && SameContext(head, running))
                    { DiscardWriteTargets(group); break; }
                    continue;
                }
                if (_writeTargets.FirstOrDefault(t => t.Group == group) is not { } target) break;
                _writeTargets.Remove(target);
                try
                {
                    if (!WriteContextMatches(target) || !CanWrite || !Supports(target.Action))
                    { DiscardWriteTargets(group); break; }
                    // An intervening device observation may already satisfy the last choice.
                    if (GoalMatches(null!, target.Action, _liveSnapshot!)) { SettleWriteTarget(target); continue; }
                    var sent = await ApplyWithResultAsync(target.Action);
                    // Lifecycle changes clear their old targets; a click in a new session is validated on the next pass.
                    if (!sent && WriteContextMatches(target)) { DiscardWriteTargets(group); break; }
                    if (sent) SettleWriteTarget(target);
                }
                finally { target.Done.TrySetResult(); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            DiscardWriteTargets(group);
            Log("设置队列意外中止，未发送的修改已放弃。");
        }
        finally { _drainingGroups.Remove(group); NotifyState(); }
    }

    private void SettleWriteTarget(WriteTarget target)
    {
        if (target.Group != "Equalizer" || target.Version != _presetVersion) return;
        _pendingPreset = false; _pendingPresetKey = null; _pendingEqEntryId = null;
        if (_snapshot is { } observed) DisplaySnapshot(observed);
    }

    private void DropWriteTargets(string group)
    {
        foreach (var target in _writeTargets.Where(t => t.Group == group).ToArray())
        { _writeTargets.Remove(target); target.Done.TrySetResult(); }
    }

    private void DiscardWriteTargets(string group)
    {
        DropWriteTargets(group);
        if (group == "Equalizer")
        {
            _pendingPreset = false; _pendingPresetKey = null; _pendingEqEntryId = null; _presetVersion++;
            // A clean editor follows the observed entry again; an unsaved draft belongs to the user.
            if (!EqDirty) _eqDraftId = null;
        }
        if (_snapshot is { } observed) DisplaySnapshot(observed);
        else NotifyState();
    }

    private void ClearWriteTargets(bool preserveUnsubmittedDraft)
    {
        foreach (var target in _writeTargets) target.Done.TrySetResult();
        _writeTargets.Clear();
        // A preset staged in the legacy editor survives reconnects; queued or submitted clicks never do.
        if (preserveUnsubmittedDraft && !_drainingGroups.Contains("Equalizer") && !_pending.ContainsKey("Equalizer") && _pendingPreset) return;
        _pendingPreset = false; _pendingPresetKey = null; _pendingEqEntryId = null; _presetVersion++;
    }

    private void ClearQueuedChoices(bool preserveUnsubmittedDraft = false)
    {
        ClearAncChoices(preserveUnsubmittedDraft);
        ClearWriteTargets(preserveUnsubmittedDraft);
    }

    // Rows with an unsent target show it, so rebuilt models keep the user's last choice and a later
    // discard is visible as a difference from the observed value. The in-flight row keeps its own editor
    // model: an unchanged observation must not rebuild it.
    private UiFeatureControl ShowQueuedFeature(UiFeatureControl control, UiSnapshot snapshot)
    {
        var target = _writeTargets.LastOrDefault(t => t.Group == "Feature" && t.Key == control.Id);
        if (target is null || target.Epoch != _sessionEpoch || target.Identity != snapshot.Identity
            || target.DeviceGeneration != snapshot.DeviceGeneration || target.ResourceRevision != snapshot.ResourceRevision) return control;
        return control.Kind == "Text" ? control with { Text = target.Action.Text } : control with { Value = target.Action.Value };
    }
}
