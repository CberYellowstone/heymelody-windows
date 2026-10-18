namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private sealed record AncTarget(UiAction Action, long Version, string Identity, long Epoch,
        string BackendInstanceId, long DeviceGeneration);

    private AncTarget? _queuedAncTarget;
    private bool _ancQueueRunning;
    private Task? _ancQueueTask;

    private Task QueueAncActionAsync(UiAction action)
    {
        if (!CanWrite || !Supports(action) || _liveSnapshot is not { } snapshot) return Task.CompletedTask;
        _pendingAnc = true;
        _pendingAncPrimary = action.Kind == "AncMode" ? action.Value : action.Value >> 8;
        _pendingAncKey = action.Kind == "Anc" ? action.Value : null;
        _ancVersion++;
        _selectedAncMode = _ancModes.FirstOrDefault(mode => mode.Key == _pendingAncPrimary);
        _selectedAncSubmode = _selectedAncMode?.Options.FirstOrDefault(option => option.Key == _pendingAncKey);
        _selectedAnc = _ancOptions.FirstOrDefault(option => option.Key == _pendingAncKey);
        _queuedAncTarget = new(action, _ancVersion, _sessionIdentity!, _sessionEpoch,
            snapshot.BackendInstanceId, snapshot.DeviceGeneration);
        NotifyState();
        if (_ancQueueRunning) return _ancQueueTask ?? Task.CompletedTask;
        _ancQueueRunning = true;
        return _ancQueueTask = DrainAncTargetsAsync();
    }

    private bool AncContextMatches(AncTarget target) => target.Epoch == _sessionEpoch
        && target.Identity == _sessionIdentity && target.Identity == SelectedDevice?.Identity
        && _liveSnapshot is { } snapshot && snapshot.Identity == target.Identity
        && snapshot.BackendInstanceId == target.BackendInstanceId && snapshot.DeviceGeneration == target.DeviceGeneration;

    private static bool AncGoalMatches(UiAction action, UiSnapshot snapshot) => action.Kind == "AncMode"
        ? snapshot.AncPrimary == action.Value : snapshot.Anc == action.Value;

    private async Task DrainAncTargetsAsync()
    {
        try
        {
            while (_queuedAncTarget is { } target)
            {
                _queuedAncTarget = null;
                if (!AncContextMatches(target) || !CanWrite || !Supports(target.Action))
                {
                    ClearAncChoices();
                    if (_snapshot is { } observed) DisplaySnapshot(observed);
                    break;
                }
                // An intervening device observation may already satisfy the last click.
                if (AncGoalMatches(target.Action, _liveSnapshot!))
                {
                    if (target.Version == _ancVersion)
                    {
                        _pendingAnc = false; _pendingAncKey = _pendingAncPrimary = null;
                        DisplaySnapshot(_liveSnapshot!);
                    }
                    continue;
                }
                var confirmed = await ApplyWithResultAsync(target.Action);
                if (!confirmed && AncContextMatches(target))
                {
                    ClearAncChoices();
                    if (_snapshot is { } observed) DisplaySnapshot(observed);
                    break;
                }
                // Lifecycle changes clear their old queued targets; a new user click in
                // a new session is validated separately on the next iteration.
            }
        }
        finally { _ancQueueRunning = false; NotifyState(); }
    }

    private void ClearAncChoices(bool preserveUnsubmittedDraft = false)
    {
        _queuedAncTarget = null;
        // The legacy editor can stage a protocol ID without submitting it. Preserve
        // that ordinary edit through reconnects, while discarding every queued or
        // already submitted click so lifecycle events cannot replay a setter.
        if (preserveUnsubmittedDraft && !_ancQueueRunning && !_pending.ContainsKey("Anc")
            && _pendingAnc && _pendingAncKey is not null) return;
        _pendingAnc = false; _pendingAncKey = _pendingAncPrimary = null;
        _ancVersion++;
    }
}
