namespace HeyMelodyWindows.App;

public sealed record UiResourceSettings(string Source = "HeyMelody", bool UseOnlineResources = true,
    string CloudKey = "", long ContentVersion = 0, bool IsOnline = false, string Status = "")
{
    public string? HeyMelodyKey { get; init; }
    public string? WirelessEarphonesKey { get; init; }
}
public interface IResourceSettingsService
{
    Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token);
    Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token);
}
public sealed record UiArtwork(string LeaseId, string FullPath, string Kind, string PackageRoot,
    string Sha256, long Length, string Source, long Revision);
public interface IArtworkService
{
    Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token);
    Task ReleaseArtworkAsync(string leaseId, CancellationToken token);
}
public sealed record UiAncMode(int Key, string Name, string IconKey, IReadOnlyList<UiOption> Options);
public sealed record UiFeatureControl(string Id, string Label, string Kind, int? Value = null,
    string? Text = null, IReadOnlyList<UiOption>? Options = null, int Minimum = 0, int Maximum = 1,
    int Step = 1, bool CanWrite = false, string Description = "");
public sealed record UiFeatureSection(string Id, string Header, bool DefaultExpanded,
    IReadOnlyList<UiFeatureControl> Controls, string Description = "");
