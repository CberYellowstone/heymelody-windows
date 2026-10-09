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
    public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => identity == _allowedIdentity
        ? inner is IHeadsetEventService events ? events.SelectAsync(identity, token) : inner.ConnectAsync(identity, token)
        : throw new InvalidOperationException();
    public Task DisconnectAsync(CancellationToken token) => inner is IHeadsetEventService
        ? Task.CompletedTask : inner.DisconnectAsync(token);
    public Task<UiSnapshot> RefreshAsync(CancellationToken token) => RefreshAsync(RefreshScope.Full, token);
    public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => inner is IHeadsetEventService events
        ? events.RefreshAsync(_allowedIdentity ?? throw new InvalidOperationException(), scope, token)
        : inner.RefreshAsync(scope, token);
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException("Live preview forbids settings.");
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
