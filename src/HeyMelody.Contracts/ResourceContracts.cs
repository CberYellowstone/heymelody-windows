namespace HeyMelodyWindows.App;

public sealed record UiResourceSettings(bool UseOnlineResources = true,
    string HeyMelodyKey = "", long ContentVersion = 0, bool IsOnline = false, string Status = "");
public interface IResourceSettingsService
{
    Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token);
    Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token);
    // Re-fetches HeyMelody configuration and artwork; existing files stay until replacements validate.
    Task<UiResourceRedownloadResult> RedownloadResourcesAsync(CancellationToken token) =>
        Task.FromException<UiResourceRedownloadResult>(new NotSupportedException("当前服务不支持重新下载在线资源。"));
}
public sealed record UiResourceRedownloadResult(UiResourceSettings Settings, bool Success, int Updated, int Failed, string Message);
public sealed record UiArtwork(string LeaseId, string FullPath, string Kind, string PackageRoot,
    string Sha256, long Length, long Revision);
public interface IArtworkService
{
    Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token);
    Task ReleaseArtworkAsync(string leaseId, CancellationToken token);
}
public sealed record UiAncMode(int Key, string Name, string IconKey, IReadOnlyList<UiOption> Options)
{
    public string Description { get; init; } = "";
    public UiAncToggle? Toggle { get; init; }
}
public sealed record UiAncToggle(int OffKey, int OnKey, string Title, string Description);
public sealed record UiFeatureControl(string Id, string Label, string Kind, int? Value = null,
    string? Text = null, IReadOnlyList<UiOption>? Options = null, int Minimum = 0, int Maximum = 1,
    int Step = 1, bool CanWrite = false, string Description = "");
public sealed record UiFeatureSection(string Id, string Header, bool DefaultExpanded,
    IReadOnlyList<UiFeatureControl> Controls, string Description = "");
