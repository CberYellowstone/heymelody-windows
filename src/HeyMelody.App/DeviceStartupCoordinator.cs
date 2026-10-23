namespace HeyMelodyWindows.App;

internal sealed class DeviceStartupCoordinator(IHeadsetService service, IDeviceManagerProcess manager)
{
    public async Task<IReadOnlyList<UiDevice>?> ResolveAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // Enumerate is a local paired-record lookup, never discovery or connection.
            var devices = await service.EnumerateAsync(token);
            token.ThrowIfCancellationRequested();
            if (devices.Count != 0) return devices;
            if (await manager.RunAsync(token) == DeviceManagerOutcome.Cancelled) return null;
            // A peer can be unpaired between the editor's commit and this lookup.
        }
    }

    /// <summary>
    /// Re-runs the paired-record lookup behind a list that was presented from the backend's subscription.
    /// True only when the effective list is now empty, so start-up must fall back to device management;
    /// a lookup failure keeps the presented selection (the backend reports it as health).
    /// </summary>
    public async Task<bool> ConfirmPresentedAsync(CancellationToken token)
    {
        try { return (await service.EnumerateAsync(token)).Count == 0; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }
}
