namespace HeyMelodyWindows.App;

// Live render is deliberately a GET-only service boundary, not a UI automation script.
public sealed class ReadOnlyPreviewService(IHeadsetService inner) : IHeadsetService
{
    private string? _allowedIdentity;
    public async Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
    {
        var devices = (await inner.EnumerateAsync(token)).Where(d => d.Connected && d.Name == "OPPO Enco X4").ToArray();
        if (devices.Length != 1) throw new InvalidOperationException("Live render requires exactly one connected X4.");
        _allowedIdentity = devices[0].Identity; return devices;
    }
    public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => identity == _allowedIdentity ? inner.ConnectAsync(identity, token) : throw new InvalidOperationException();
    public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    public Task<UiSnapshot> RefreshAsync(CancellationToken token) => inner.RefreshAsync(token);
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException("Live preview forbids settings.");
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
