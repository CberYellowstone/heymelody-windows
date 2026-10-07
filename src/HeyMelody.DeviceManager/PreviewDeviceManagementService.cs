using HeyMelodyWindows.App;

namespace HeyMelodyWindows.DeviceManager;

/// <summary>In-memory preview: no worker, Bluetooth calls or preference writes.</summary>
internal sealed class PreviewDeviceManagementService : IDeviceManagementService
{
    public Task<UiDeviceCatalog> EnumerateCandidatesAsync(CancellationToken token) =>
        Task.FromResult(new UiDeviceCatalog([
            new("preview-1", "OPPO Enco X4", true, true),
            new("preview-2", "OPPO Enco Air4 Pro", false, false),
            new("preview-3", "OnePlus Buds Pro 3", true, false),
            new("preview-4", "办公室的耳机 — 设备名称较长时会省略显示", false, false),
            new("preview-5", "OPPO Enco X3", false, false),
            new("preview-6", "OnePlus Buds 3", false, false),
            new("preview-7", "OPPO Enco Air3", false, false),
            new("preview-8", "OPPO Enco Free3", false, false)]));
    public Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken token) =>
        Task.FromResult(new UiWhitelistSaveResult(true));
}
