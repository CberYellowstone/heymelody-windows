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
}
