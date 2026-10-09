using System.IO.Compression;
using System.Text;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Resources;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendSupervisor
{
    private readonly ResourceCoordinator? _resources;
    private readonly SemaphoreSlim _resourceGate = new(1);
    private ResourceCatalog? _activeCatalog;
    private long _activeResourceRevision;
    private bool _catalogPending;
    private bool _resourceTransition;
    private void SetResourceTransitionLocked(bool value)
    {
        _resourceTransition = value;
        foreach (var peer in _peers.Values) PublishLocked(peer);
    }
    private readonly Dictionary<string, (string Owner, ResourceArtworkLease Lease)> _artworkLeases = [];
    private async Task InitializeResourcesAsync(CancellationToken token)
    {
        if (_resources is null) return;
        await _resources.InitializeAsync(token);
        _activeCatalog = _resources.Catalog; _activeResourceRevision = _resources.Revision;
        _resources.Changed += ResourcesChanged;
        _resources.StartAutoRefresh();
        _ = RefreshResourcesSafelyAsync();
    }
    private async Task RefreshResourcesSafelyAsync()
    {
        try { await _resources!.RefreshAsync(token: _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { ReportHealth("在线资源暂时不可用，继续使用已有资源。"); }
    }
    private void ResourcesChanged(object? sender, ResourceChangedEventArgs args) => _ = AdoptResourcesSafelyAsync(args.CatalogChanged);
    private async Task AdoptResourcesSafelyAsync(bool catalogChanged)
    {
        try
        {
            await _resourceGate.WaitAsync(_lifetime.Token);
            try { await AdoptResourcesAsync(catalogChanged, _lifetime.Token); }
            finally { _resourceGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { ReportHealth("资源已下载，将在连接恢复后重新应用。"); }
    }
    private async Task AdoptResourcesAsync(bool catalogChanged, CancellationToken token)
    {
        if (_resources is null) return;
        Peer[] peers;
        bool restart, ownsTransition = false;
        lock (_sync)
        {
            if (_stopping) return;
            if (catalogChanged && _calibrations.Values.Any(IsActiveCalibration))
            { _catalogPending = true; EmitLocked(new() { Kind = "resources", Resources = ResourceSettings() }); return; }
            restart = _activeCatalog?.Sha256 != _resources.Catalog.Sha256 || _activeCatalog?.Source != _resources.Catalog.Source;
            peers = _peers.Values.ToArray();
            // Capture the peers whose existing commands need draining at the same
            // instant that new submissions are blocked, including newly discovered peers.
            if (restart) { ownsTransition = !_resourceTransition; if (ownsTransition) SetResourceTransitionLocked(true); }
        }
        if (restart)
        {
            // Finish any submitted ordinary command before retiring its configuration.
            var acquired = new List<Peer>();
            try
            {
                foreach (var peer in peers) { await peer.Commands.WaitAsync(token); acquired.Add(peer); }
                lock (_sync)
                {
                    if (_activeCatalog?.Source == _resources.Catalog.Source && _calibrations.Values.Any(IsActiveCalibration))
                    { _catalogPending = true; EmitLocked(new() { Kind = "resources", Resources = ResourceSettings() }); return; }
                    _activeCatalog = _resources.Catalog; _activeResourceRevision = _resources.Revision; _catalogPending = false;
                    // Catalog refresh can add a peer while the command gates above are
                    // awaited. It could only start reads during the transition, so cancel
                    // those reads and retire its old-source worker along with all others.
                    peers = _peers.Values.ToArray();
                    foreach (var peer in peers) { StopPeerLocked(peer); peer.Broadcast = null; peer.Failures = 0; PublishLocked(peer); }
                }
            }
            finally
            {
                foreach (var peer in acquired) peer.Commands.Release();
                if (ownsTransition) lock (_sync) SetResourceTransitionLocked(false);
            }
            foreach (var peer in peers) _ = EnsureWorkerAsync(peer);
        }
        else lock (_sync)
        {
            _activeResourceRevision = _resources.Revision;
            foreach (var peer in peers) PublishLocked(peer);
        }
        lock (_sync) EmitLocked(new() { Kind = "resources", Resources = ResourceSettings() });
    }
    public UiResourceSettings ResourceSettings()
    {
        if (_resources is null) return new();
        var p = _resources.Preferences; var c = _activeCatalog ?? _resources.Catalog;
        return new(p.Source.ToString(), p.UseOnlineResources, p.CustomKey, c.ContentVersion, c.IsOnline,
            _catalogPending ? "已下载更新，当前测试完成后应用。" : _resources.LastError ?? (c.IsOnline ? "使用已下载的在线资源" : "使用预装资源"))
        { HeyMelodyKey = p.HeyMelodyKey, WirelessEarphonesKey = p.WirelessEarphonesKey };
    }
    public async Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token)
    {
        if (_resources is null) throw new InvalidOperationException("资源服务不可用。");
        if (!Enum.TryParse<ResourceSource>(settings.Source, out var source) || !Enum.IsDefined(source) || settings.CloudKey.Length > 4096
            || settings.HeyMelodyKey?.Length > 4096 || settings.WirelessEarphonesKey?.Length > 4096)
            throw new InvalidDataException();
        await _resourceGate.WaitAsync(token);
        bool transition = _resources.Preferences.Source != source;
        try
        {
            if (transition)
            {
                lock (_sync) SetResourceTransitionLocked(true);
                await CancelAllCalibrationsAsync();
            }
            var p = _resources.Preferences with { Source = source, UseOnlineResources = settings.UseOnlineResources };
            if (settings.HeyMelodyKey is not null) p = p with { HeyMelodyKey = settings.HeyMelodyKey };
            if (settings.WirelessEarphonesKey is not null) p = p with { WirelessEarphonesKey = settings.WirelessEarphonesKey };
            p = source == ResourceSource.HeyMelody ? p with { HeyMelodyKey = settings.CloudKey } : p with { WirelessEarphonesKey = settings.CloudKey };
            await _resources.UpdatePreferencesAsync(p, token);
            await AdoptResourcesAsync(true, token);
        }
        finally { if (transition) lock (_sync) SetResourceTransitionLocked(false); _resourceGate.Release(); }
        if (settings.UseOnlineResources) _ = RefreshResourcesSafelyAsync();
        return ResourceSettings();
    }
    private bool KnownProduct(string pid) => _activeCatalog is null ? pid.Equals("06EC10", StringComparison.OrdinalIgnoreCase)
        : _activeCatalog.Find(pid) is not null;
    private void ConfigureWorkerResources(IPeerWorker worker, Peer peer)
    {
        if (worker is not IResourcePeerWorker target || _activeCatalog is null) return;
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.SmallestSize, true))
            gzip.Write(Encoding.UTF8.GetBytes(_activeCatalog.Raw.GetRawText()));
        target.ConfigureResources(Convert.ToBase64String(bytes.ToArray()), _activeCatalog.Source.ToString(),
            _activeResourceRevision, VendorResourceClient.RequestCompatVersion(_activeCatalog.Source),
            DeviceWhitelistStore.KeyFor(peer.Candidate.Address), peer.Generation, peer.Broadcast?.ProductId, peer.Broadcast?.Color);
    }
    private async Task UpdateWorkerColorAsync(Peer peer, string productId, byte color)
    {
        try
        {
            await peer.Commands.WaitAsync(_lifetime.Token);
            try
            {
                IResourcePeerWorker worker; long generation; CancellationToken token;
                lock (_sync)
                {
                    if (!EligibleLocked(peer) || peer.Worker is not IResourcePeerWorker target || peer.Initializing
                        || peer.VerifiedProductId != productId || peer.Broadcast?.Color != color) return;
                    worker = target; generation = peer.Generation; token = peer.Lifetime.Token;
                }
                var snapshot = await worker.SetObservedColorAsync(productId, color, token);
                lock (_sync) if (generation == peer.Generation) AdoptSppLocked(peer, snapshot);
            }
            finally { peer.Commands.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* BLE artwork metadata must not interrupt an active device session. */ }
    }
    public async Task<UiArtwork?> AcquireArtworkAsync(string owner, string productId, int? color, string role, CancellationToken token)
    {
        if (_resources is null) return null;
        if (role is not ("hero" or "model" or "case" or "left" or "right" or "lottie" or "capsule" or "detail" or "both"))
            throw new InvalidDataException();
        // Remembered hints can name a model the selected source does not list: it simply has no art.
        if (_resources.Catalog.Find(productId) is null) return null;
        var source = _resources.Preferences.Source;
        var lease = await _resources.ResolveArtworkAsync(productId, color, role, token);
        if (lease is null) return null;
        if (source != _resources.Preferences.Source || token.IsCancellationRequested)
        { lease.Dispose(); token.ThrowIfCancellationRequested(); return null; }
        string id = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            if (_stopping) { lease.Dispose(); return null; }
            _artworkLeases[id] = (owner, lease);
        }
        return new(id, lease.FullPath, lease.Kind, lease.PackageRoot, lease.Sha256, lease.Length, lease.Source.ToString(), lease.Revision);
    }
    public bool ReleaseArtwork(string owner, string leaseId)
    {
        lock (_sync)
        {
            if (!_artworkLeases.TryGetValue(leaseId, out var item) || item.Owner != owner) return false;
            _artworkLeases.Remove(leaseId); item.Lease.Dispose(); return true;
        }
    }
    private Task<UiResourceRedownloadResult>? _redownload;
    /// <summary>One shared re-download at a time; it continues if the requesting client disconnects.</summary>
    public Task<UiResourceRedownloadResult> RedownloadResourcesAsync()
    {
        if (_resources is null) throw new InvalidOperationException("资源服务不可用。");
        lock (_sync) return _redownload is { IsCompleted: false } running ? running : _redownload = RunRedownloadAsync();
    }
    private async Task<UiResourceRedownloadResult> RunRedownloadAsync()
    {
        await _resourceGate.WaitAsync(_lifetime.Token);
        try
        {
            (string, int)[] devices;
            lock (_sync) devices = _peers.Values.Where(p => p.Allowed && p.AppearanceProductId is not null && p.AppearanceColor is not null)
                .Select(p => (p.AppearanceProductId!, (int)p.AppearanceColor!.Value)).Distinct().ToArray();
            var result = await _resources!.RedownloadAsync(devices, _lifetime.Token);
            await AdoptResourcesAsync(result.CatalogChanged, _lifetime.Token);
            return new(ResourceSettings(), result.Failed == 0, result.Updated, result.Failed, result.Message);
        }
        finally { _resourceGate.Release(); }
    }
    private void DisposeResources()
    {
        lock (_sync) { foreach (var item in _artworkLeases.Values) item.Lease.Dispose(); _artworkLeases.Clear(); }
        if (_resources is not null) { _resources.Changed -= ResourcesChanged; _resources.Dispose(); }
    }
}

internal interface IResourcePeerWorker
{
    void ConfigureResources(string catalogGzip, string source, long revision, long appMarker, string stableKey, long generation,
        string? observedProductId, byte? observedColor);
    Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token);
}
