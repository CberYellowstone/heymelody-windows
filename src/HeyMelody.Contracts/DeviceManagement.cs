namespace HeyMelodyWindows.App;

// Candidate identities are private session tokens, never persisted device keys.
public sealed record UiDeviceCandidate(string Identity, string Name, bool Connected, bool Enabled);
public sealed record UiDeviceCatalog(IReadOnlyList<UiDeviceCandidate> Candidates, string? SelectionWarning = null);
public sealed record UiWhitelistSaveResult(bool Saved, string? Error = null);

public interface IDeviceManagementService
{
    Task<UiDeviceCatalog> EnumerateCandidatesAsync(CancellationToken cancellationToken);
    Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken cancellationToken);
}
