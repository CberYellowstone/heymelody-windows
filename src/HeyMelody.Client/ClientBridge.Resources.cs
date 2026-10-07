using System.Text.Json;
using HeyMelodyWindows.Host;

namespace HeyMelodyWindows.App;

public sealed partial class ClientBridge
{
    public Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token) =>
        RequestAsync<UiResourceSettings>(new() { Operation = "resourceSettings" }, TimeSpan.FromSeconds(15), token);
    public Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token) =>
        RequestAsync<UiResourceSettings>(new() { Operation = "saveResourceSettings", Action = JsonSerializer.SerializeToElement(settings, RpcWire.Json) }, TimeSpan.FromSeconds(180), token);
    public Task<IReadOnlyList<UiHearingProfile>> GetHearingProfilesAsync(string identity, long generation, CancellationToken token) =>
        RequestProfilesAsync(identity, generation, token);
    private async Task<IReadOnlyList<UiHearingProfile>> RequestProfilesAsync(string identity, long generation, CancellationToken token) =>
        await RequestAsync<List<UiHearingProfile>>(new() { Operation = "hearingProfiles", Identity = identity, DeviceGeneration = generation }, TimeSpan.FromSeconds(30), token);
    public Task<UiApplyResult> SelectHearingProfileAsync(string identity, long generation, int? uid, CancellationToken token) =>
        RequestAsync<UiApplyResult>(new() { Operation = "selectHearing", Identity = identity, DeviceGeneration = generation,
            Action = JsonSerializer.SerializeToElement(new { uid }, RpcWire.Json) }, TimeSpan.FromSeconds(60), token);
    public Task<UiCalibrationState> CreateCalibrationAsync(string identity, long generation, string kind, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "createCalibration", Identity = identity, DeviceGeneration = generation,
            Action = JsonSerializer.SerializeToElement(new { kind }, RpcWire.Json) }, TimeSpan.FromSeconds(30), token);
    public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "getCalibration", CalibrationHandle = handle }, TimeSpan.FromSeconds(15), token);
    public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "advanceCalibration", CalibrationHandle = handle, Revision = revision,
            Action = JsonSerializer.SerializeToElement(input, RpcWire.Json) }, TimeSpan.FromSeconds(45), token);
    public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "cancelCalibration", CalibrationHandle = handle }, TimeSpan.FromSeconds(30), token);
    public async Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token)
    {
        var result = await RequestAsync<ArtworkResponse>(new() { Operation = "acquireArtwork",
            Action = JsonSerializer.SerializeToElement(new { productId, color, role }, RpcWire.Json) }, TimeSpan.FromSeconds(90), token);
        return result.Artwork;
    }
    public async Task ReleaseArtworkAsync(string leaseId, CancellationToken token) =>
        _ = await RequestAsync<bool>(new() { Operation = "releaseArtwork", Action = JsonSerializer.SerializeToElement(new { leaseId }, RpcWire.Json) }, TimeSpan.FromSeconds(15), token);
    private sealed record ArtworkResponse(UiArtwork? Artwork);
}
