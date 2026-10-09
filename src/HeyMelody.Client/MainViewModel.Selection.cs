using System.Text.Json;

namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private static UiDevice? ChooseDevice(IReadOnlyList<UiDevice> devices, string? currentIdentity) =>
        devices.FirstOrDefault(device => device.Identity == currentIdentity)
        ?? devices.FirstOrDefault(device => !IsDemoDevice(device) && device.IsPreferredSelection)
        ?? devices.FirstOrDefault(device => !IsDemoDevice(device))
        ?? devices.FirstOrDefault();

    // Catalog objects may be replaced or reordered without changing the user's target.
    // Only an identity change cancels its work and clears the previous device's display.
    private bool PresentDevices(IReadOnlyList<UiDevice> devices, string? currentIdentity)
    {
        var oldIdentity = _selectedDevice?.Identity;
        _devices = ComposeDevices(devices);
        _selectedDevice = ChooseDevice(_devices, currentIdentity);
        var changed = oldIdentity != _selectedDevice?.Identity;
        if (changed)
        {
            AdvanceSession();
            _sessionIdentity = EventService is not null ? _selectedDevice?.Identity : null;
            ResetSelection();
        }
        _title = _selectedDevice?.Name ?? "选择一副耳机";
        return changed;
    }

    private void PresentSelectedBackendSnapshot()
    {
        if (IsDemoSelected) { AttachDemo(); return; }
        if (_selectedDevice is { } selected && _backendSnapshots.TryGetValue(selected.Identity, out var snapshot))
        {
            _sessionIdentity = selected.Identity;
            AdoptRead(snapshot);
        }
    }

    private static UiSnapshot ReadOnlyPresentation(UiSnapshot source)
    {
        // Keep the last observed settings, never the VM's staged selections or EQ draft.
        // Clone before marking them cached so retained backend evidence stays immutable.
        var result = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(source))!;
        result.PresentationCached = true;
        result.PresentationSampleTime ??= source.SampleTime == default ? null : source.SampleTime;
        result.ControlReady = false;
        result.ObservationFresh = false;
        result.Connected = false;
        result.ConnectionState = ConnectionState.Unknown;
        result.MonitoringState = MonitoringState.Unavailable;
        return result;
    }

    private bool EqLayoutCompatible => _snapshot?.EqualizerFrequencies is not { Count: > 0 }
        || Bands.Select(band => band.FrequencyHz).SequenceEqual(_snapshot.EqualizerFrequencies);
}
