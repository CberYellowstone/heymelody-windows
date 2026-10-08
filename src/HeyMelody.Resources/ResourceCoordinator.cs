using System.Security.Cryptography;
using System.Text.Json;

namespace HeyMelodyWindows.Resources;

public sealed record ResourceChangedEventArgs(ResourceSource Source, long Revision, bool CatalogChanged, bool ArtworkChanged);
public sealed record ResourceRedownloadResult(bool CatalogChanged, int Updated, int Unchanged, int Failed, string Message);

/// <summary>Host-owned single writer for preferences, immutable catalogs and leased art packages.</summary>
public sealed class ResourceCoordinator : IDisposable
{
    private readonly string _storage;
    private readonly string _online;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VendorResourceClient _vendor;
    private readonly Dictionary<ResourceSource, ResourceCatalog> _catalogs = [];
    private readonly Dictionary<ResourceSource, string> _fileVersions = [];
    private readonly Dictionary<string, DateTimeOffset> _checks = [];
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string ProductId, int Color, bool Model)> _artworkTargets = [];
    private readonly object _leaseGate = new();
    private readonly Dictionary<string, Task<bool>> _artworkRefreshes = new(StringComparer.Ordinal);
    private int _batchedChanges;
    private CancellationTokenSource _sourceCancellation = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _autoRefresh;
    private bool _initialized;
    public BundledResourceStore Bundled { get; }
    public ResourcePreferences Preferences { get; private set; } = new();
    public ResourceCatalog Catalog => _catalogs[Preferences.Source];
    public long Revision { get; private set; } = 1;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSuccessfulCheck { get; private set; }
    /// <summary>How long a first artwork request waits for its download before falling back.</summary>
    public TimeSpan FirstDownloadWait { get; set; } = TimeSpan.FromSeconds(10);
    public event EventHandler<ResourceChangedEventArgs>? Changed;

    public ResourceCoordinator(string? storageDirectory = null, HttpClient? httpClient = null, string? bundleDirectory = null)
    {
        _storage = Path.GetFullPath(storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "resources"));
        _online = Path.Combine(_storage, "online");
        Bundled = new(bundleDirectory); _vendor = new(httpClient);
        foreach (ResourceSource source in Enum.GetValues<ResourceSource>()) _catalogs[source] = Bundled.GetCatalog(source);
    }
    public static ResourceCoordinator CreateDefault() => new();

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(_storage);
            string preferencesFile = Path.Combine(_storage, "preferences.json");
            if (File.Exists(preferencesFile))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<ResourcePreferences>(await File.ReadAllBytesAsync(preferencesFile, token).ConfigureAwait(false), ResourceJson.Options);
                    if (saved is not null && Enum.IsDefined(saved.Source)) Preferences = saved;
                }
                catch (Exception error) when (error is IOException or JsonException) { LastError = "无法读取资源设置，已采用默认设置。"; }
            }
            if (!ValidKey(Preferences.CacheEncryptionKey)) Preferences = Preferences with { CacheEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await SavePreferencesAsync(token).ConfigureAwait(false);
            foreach (ResourceSource source in Enum.GetValues<ResourceSource>()) { LoadCachedCatalog(source); SweepArtwork(source); }
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task UpdatePreferencesAsync(ResourcePreferences preferences, CancellationToken token = default)
    {
        if (!Enum.IsDefined(preferences.Source)) throw new ArgumentException("Unknown resource source.");
        _ = new System.Text.UTF8Encoding(false, true).GetBytes(preferences.HeyMelodyKey);
        _ = new System.Text.UTF8Encoding(false, true).GetBytes(preferences.WirelessEarphonesKey);
        // Cancel requests belonging to the outgoing selection before waiting for the writer.
        _sourceCancellation.Cancel();
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        bool changed;
        try
        {
            var old = Preferences;
            Preferences = preferences with { CacheEncryptionKey = old.CacheEncryptionKey };
            await SavePreferencesAsync(token).ConfigureAwait(false);
            changed = old.Source != Preferences.Source;
            _sourceCancellation.Dispose(); _sourceCancellation = new();
            _checks.Clear(); LastError = null; Revision++;
            lock (_leaseGate) _artworkTargets.Clear();
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, new(Preferences.Source, Revision, changed, changed));
    }

    public string ExportEffectiveCatalogJson() => Catalog.Raw.GetRawText();

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken token = default)
        => await RefreshConfigurationAsync(force, false, token).ConfigureAwait(false) == RefreshOutcome.Updated;

    private async Task<RefreshOutcome> RefreshConfigurationAsync(bool force, bool redownload, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _sourceCancellation.Token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        var outcome = RefreshOutcome.Skipped;
        try
        {
            if (!Preferences.UseOnlineResources) return RefreshOutcome.Skipped;
            var source = Preferences.Source; string check = SourceName(source) + "/config";
            if (!force && _checks.TryGetValue(check, out var next) && next > DateTimeOffset.UtcNow) return RefreshOutcome.Skipped;
            try
            {
                outcome = RefreshOutcome.Unchanged;
                var metadata = await _vendor.GetConfigurationMetadataAsync(source, ActiveKey(source), linked.Token).ConfigureAwait(false);
                bool cacheValid = CachedCatalogExists(source);
                if (redownload || !_fileVersions.TryGetValue(source, out var saved) || saved != metadata.FileVersion || !cacheValid)
                {
                    byte[] bytes = await _vendor.DownloadAsync(metadata.DownloadUrl, ResourceLimits.ConfigBytes, linked.Token).ConfigureAwait(false);
                    if (metadata.Size is long expected && bytes.LongLength != expected) throw new InvalidDataException("Configuration length differs from metadata.");
                    var candidate = ResourceCatalog.Parse(bytes, source, true);
                    if (candidate.ContentVersion > _catalogs[source].ContentVersion)
                    {
                        await SaveCatalogAsync(source, candidate, bytes, linked.Token).ConfigureAwait(false);
                        _catalogs[source] = candidate; Revision++; outcome = RefreshOutcome.Updated;
                        CleanupCatalogs(source, candidate.Sha256);
                    }
                    else if (_catalogs[source].IsOnline && (!cacheValid || redownload && candidate.Sha256 == _catalogs[source].Sha256))
                    {
                        // Repair or rewrite the persisted current snapshot without adopting an equal/older response.
                        var retained = _catalogs[source];
                        await SaveCatalogAsync(source, retained, retained.OriginalBytes, linked.Token).ConfigureAwait(false);
                        CleanupCatalogs(source, retained.Sha256);
                    }
                    _fileVersions[source] = metadata.FileVersion;
                    await ResourceFiles.AtomicWriteAsync(Path.Combine(SourceRoot(source), "metadata.json"), JsonSerializer.SerializeToUtf8Bytes(metadata.FileVersion), linked.Token).ConfigureAwait(false);
                }
                _checks[check] = DateTimeOffset.UtcNow.AddMinutes(10); LastSuccessfulCheck = DateTimeOffset.UtcNow; LastError = null;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            // An HttpClient timeout is an ordinary failure, not a caller cancellation.
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or JsonException
                or CryptographicException or ArgumentException or UnauthorizedAccessException)
            { _checks[check] = DateTimeOffset.UtcNow.AddSeconds(30); LastError = "在线资源更新失败，继续使用当前有效资源。"; outcome = RefreshOutcome.Failed; }
        }
        finally { _gate.Release(); }
        if (outcome == RefreshOutcome.Updated && Volatile.Read(ref _batchedChanges) == 0) Changed?.Invoke(this, new(Preferences.Source, Revision, true, false));
        return outcome;
    }

    public void StartAutoRefresh()
    {
        if (_autoRefresh is not null) return;
        _autoRefresh = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(token: _lifetime.Token).ConfigureAwait(false);
                    (string ProductId, int Color, bool Model)[] targets;
                    lock (_leaseGate) targets = _artworkTargets.ToArray();
                    foreach (var target in targets)
                        await RefreshArtworkAsync(target.ProductId, target.Color, target.Model, token: _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { if (_lifetime.IsCancellationRequested) break; }
                if (!await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false)) break;
            }
        }, _lifetime.Token);
    }

    /// <summary>
    /// Returns installed art without waiting for the network. Online checks run in the background and
    /// announce replacements through <see cref="Changed"/>. Only a first download is awaited, briefly.
    /// </summary>
    public async Task<ResourceArtworkLease?> ResolveArtworkAsync(string productId, int? color, string role = "hero", CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        var source = Preferences.Source;
        var profile = _catalogs[source].Find(productId);
        if (profile is null) return null;
        // Never invent a protocol color. Uncolored same-model bundled art remains eligible.
        if (color.HasValue)
        {
            bool model = role is "model" or "hero";
            if (model && profile.Functions.ValueKind == JsonValueKind.Object && profile.Functions.TryGetProperty("support3DModel", out var support)
                && support.ValueKind == JsonValueKind.Number && support.TryGetInt32(out int enabled) && enabled > 0)
            {
                var found = await CachedOrDownloadedAsync(source, productId, color.Value, true, role,
                    () => Bundled.Acquire(Bundled.FindAsset(source, productId, color, "model"), Revision), token).ConfigureAwait(false);
                if (found is not null) return found;
            }
            if (role != "model")
            {
                var found = await CachedOrDownloadedAsync(source, productId, color.Value, false, role, null, token).ConfigureAwait(false);
                if (found is not null) return found;
            }
        }
        if (source != Preferences.Source) return null;
        return Bundled.Acquire(Bundled.FindAsset(source, productId, color, role), Revision)
            ?? Bundled.Acquire(Bundled.FindAsset(source, productId, color, "detail"), Revision)
            ?? Bundled.Acquire(Bundled.FindGenericAsset(source, profile.Type, role), Revision);
    }

    private async Task<ResourceArtworkLease?> CachedOrDownloadedAsync(ResourceSource source, string productId, int color, bool model, string role,
        Func<ResourceArtworkLease?>? installed, CancellationToken token)
    {
        var refresh = ScheduleArtworkRefresh(source, productId, color, model);
        var found = AcquireCachedArtwork(source, productId, color, model, role) ?? installed?.Invoke();
        if (found is not null || !Preferences.UseOnlineResources) return found;
        // First use of this slot: wait for the shared download, then fall back. A late package still
        // raises Changed, and clients resolve again.
        try { await refresh.WaitAsync(FirstDownloadWait, token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
        return source == Preferences.Source ? AcquireCachedArtwork(source, productId, color, model, role) : null;
    }

    private Task<bool> ScheduleArtworkRefresh(ResourceSource source, string productId, int color, bool model)
    {
        if (!Preferences.UseOnlineResources || source != Preferences.Source) return Task.FromResult(false);
        string key = SourceName(source) + "/" + ArtworkSlot(productId, color, model);
        lock (_artworkRefreshes)
        {
            if (_artworkRefreshes.TryGetValue(key, out var running) && !running.IsCompleted) return running;
            var refresh = Task.Run(async () =>
            {
                try { return await RefreshArtworkAsync(productId, color, model, token: _lifetime.Token).ConfigureAwait(false); }
                catch (Exception) { return false; }
            });
            _artworkRefreshes[key] = refresh;
            return refresh;
        }
    }

    public async Task<bool> RefreshArtworkAsync(string productId, int color, bool model = false, bool force = false, CancellationToken token = default)
        => await RefreshArtworkCoreAsync(productId, color, model, force, false, token).ConfigureAwait(false) == RefreshOutcome.Updated;

    private async Task<RefreshOutcome> RefreshArtworkCoreAsync(string productId, int color, bool model, bool force, bool redownload, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _sourceCancellation.Token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        var outcome = RefreshOutcome.Skipped;
        try
        {
            if (!Preferences.UseOnlineResources || Catalog.Find(productId) is null) return RefreshOutcome.Skipped;
            lock (_leaseGate) _artworkTargets.Add((productId, color, model));
            var source = Preferences.Source; string key = SourceName(source) + "/" + ArtworkSlot(productId, color, model);
            if (!force && _checks.TryGetValue(key, out var next) && next > DateTimeOffset.UtcNow) return RefreshOutcome.Skipped;
            try
            {
                outcome = RefreshOutcome.Unchanged;
                var metadata = await _vendor.GetArtworkMetadataAsync(source, ActiveKey(source), productId, color, model, linked.Token).ConfigureAwait(false);
                if (metadata is not null)
                {
                    string slot = ArtworkRoot(source, productId, color, model);
                    var current = ReadArtworkPointer(slot);
                    if (redownload || current is null || PackageDigest(current) != metadata.Sha256 || !ValidArtworkPackage(Path.Combine(slot, current)))
                    {
                        byte[] archive = await _vendor.DownloadAsync(metadata.DownloadUrl, ResourceLimits.ArchiveBytes, linked.Token).ConfigureAwait(false);
                        if (Convert.ToHexStringLower(SHA256.HashData(archive)) != metadata.Sha256) throw new InvalidDataException("Resource digest mismatch.");
                        string temporary = Path.Combine(slot, ".download-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporary);
                        try
                        {
                            var entries = ArtworkPackageExtractor.Extract(archive, temporary, source, productId, color);
                            if (entries.Length == 0) throw new InvalidDataException("Resource package has no usable artwork.");
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(temporary, "artwork.json"), JsonSerializer.SerializeToUtf8Bytes(entries, ResourceJson.Options), linked.Token).ConfigureAwait(false);
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(temporary, ArtworkPackageExtractor.MarkerFile),
                                JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = ArtworkPackageExtractor.SchemaVersion, sha256 = metadata.Sha256 }), linked.Token).ConfigureAwait(false);
                            linked.Token.ThrowIfCancellationRequested();
                            string name = metadata.Sha256, package = Path.Combine(slot, name);
                            lock (_leaseGate)
                            {
                                if (Directory.Exists(package))
                                {
                                    // A renderer still reads this digest's previous extraction; publish beside it.
                                    if (_leases.ContainsKey(package)) { name += "-" + Guid.NewGuid().ToString("N")[..8]; package = Path.Combine(slot, name); }
                                    else ResourcePaths.DeleteOwnedDirectory(_online, package);
                                }
                                Directory.Move(temporary, package);
                                _retired.Remove(package);
                            }
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(slot, "current.json"), JsonSerializer.SerializeToUtf8Bytes(name), linked.Token).ConfigureAwait(false);
                            foreach (string old in Directory.EnumerateDirectories(slot)) if (old != package) Retire(old);
                            Revision++; outcome = RefreshOutcome.Updated;
                        }
                        finally { if (Directory.Exists(temporary)) ResourcePaths.DeleteOwnedDirectory(_online, temporary); }
                    }
                }
                _checks[key] = DateTimeOffset.UtcNow.AddMinutes(10); LastError = null;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or JsonException
                or CryptographicException or ArgumentException or UnauthorizedAccessException)
            { _checks[key] = DateTimeOffset.UtcNow.AddSeconds(30); LastError = "在线美术资源更新失败，继续使用当前有效资源。"; outcome = RefreshOutcome.Failed; }
        }
        finally { _gate.Release(); }
        if (outcome == RefreshOutcome.Updated && Volatile.Read(ref _batchedChanges) == 0) Changed?.Invoke(this, new(Preferences.Source, Revision, false, true));
        return outcome;
    }

    /// <summary>
    /// Re-fetches the selected source's configuration and every known artwork slot. Replacements are
    /// validated before they are published; on failure the existing files stay in use.
    /// </summary>
    public async Task<ResourceRedownloadResult> RedownloadAsync(IEnumerable<(string ProductId, int Color)> devices, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        if (!Preferences.UseOnlineResources) throw new InvalidOperationException("在线资源已关闭，开启后才能重新下载。");
        // Stop background checks of the outgoing state before taking the writer.
        _sourceCancellation.Cancel();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        ResourceSource source;
        try
        {
            _sourceCancellation.Dispose(); _sourceCancellation = new();
            source = Preferences.Source; _checks.Clear(); LastError = null;
        }
        finally { _gate.Release(); }
        long before = Revision; int updated = 0, unchanged = 0, failed = 0;
        Interlocked.Increment(ref _batchedChanges);
        bool catalogChanged;
        try
        {
            var configuration = await RefreshConfigurationAsync(true, true, token).ConfigureAwait(false);
            catalogChanged = configuration == RefreshOutcome.Updated;
            if (configuration == RefreshOutcome.Failed) failed++;
            foreach (var target in RedownloadTargets(source, devices))
            {
                if (source != Preferences.Source) break;
                switch (await RefreshArtworkCoreAsync(target.ProductId, target.Color, target.Model, true, true, token).ConfigureAwait(false))
                {
                    case RefreshOutcome.Updated: updated++; break;
                    case RefreshOutcome.Failed: failed++; break;
                    case RefreshOutcome.Unchanged: unchanged++; break;
                }
            }
        }
        finally { Interlocked.Decrement(ref _batchedChanges); }
        LastError = failed == 0 ? null : "部分在线资源重新下载失败，已保留原有资源。";
        if (Revision != before) Changed?.Invoke(this, new(source, Revision, catalogChanged, true));
        string message = failed != 0 ? $"{failed} 项在线资源重新下载失败，已保留原有资源。"
            : updated == 0 ? "已重新下载在线资源，配置与美术资源均已核对。" : $"已重新下载在线资源，更新了 {updated} 项美术资源。";
        return new(catalogChanged, updated, unchanged, failed, message);
    }

    private (string ProductId, int Color, bool Model)[] RedownloadTargets(ResourceSource source, IEnumerable<(string ProductId, int Color)> devices)
    {
        var targets = new HashSet<(string ProductId, int Color, bool Model)>();
        lock (_leaseGate) targets.UnionWith(_artworkTargets);
        string root = Path.Combine(SourceRoot(source), "artwork");
        if (Directory.Exists(root))
            foreach (string slot in Directory.EnumerateDirectories(root))
                if (System.Text.RegularExpressions.Regex.Match(Path.GetFileName(slot), "^([0-9A-F]{6})_([0-9]{1,3})_(14|control)$") is { Success: true } match
                    && int.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int color) && color <= 255)
                    targets.Add((match.Groups[1].Value, color, match.Groups[3].Value == "14"));
        foreach (var (productId, color) in devices)
        {
            if (_catalogs[source].Find(productId) is not { } profile) continue;
            targets.Add((productId, color, false));
            if (profile.Functions.ValueKind == JsonValueKind.Object && profile.Functions.TryGetProperty("support3DModel", out var support)
                && support.ValueKind == JsonValueKind.Number && support.TryGetInt32(out int enabled) && enabled > 0)
                targets.Add((productId, color, true));
        }
        return targets.Where(target => _catalogs[source].Find(target.ProductId) is not null)
            .OrderBy(target => target.ProductId, StringComparer.Ordinal).ThenBy(target => target.Color).ThenBy(target => target.Model).ToArray();
    }

    private ResourceArtworkLease? AcquireCachedArtwork(ResourceSource source, string productId, int color, bool model, string role)
    {
        string slot = ArtworkRoot(source, productId, color, model); var current = ReadArtworkPointer(slot);
        if (current is null) return null;
        string package = Path.Combine(slot, current);
        lock (_leaseGate)
        {
            try
            {
                var entries = JsonSerializer.Deserialize<ResourceArtwork[]>(File.ReadAllBytes(Path.Combine(package, "artwork.json")), ResourceJson.Options) ?? [];
                var entry = SelectPackageEntry(entries.Where(e => e.Source == source && e.ProductIds.Contains(productId, StringComparer.Ordinal) && e.Color == color), role);
                if (entry is null) return null;
                string path = ResourcePaths.Resolve(package, entry.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Length || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != entry.Sha256) return null;
                _leases[package] = _leases.GetValueOrDefault(package) + 1;
                return new(path, entry.Kind, package, entry.Sha256, entry.Length, source, Revision, () => Release(package));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException) { return null; }
        }
    }

    /// <summary>
    /// Product display preference inside one downloaded package: a model, then the first entry of the
    /// main lottieResList (its detail image stays the static fallback), then the detail image.
    /// Side lists (lottie-left/right) are never the hero. This is the single place to tune once the
    /// official playback rules are confirmed on hardware.
    /// </summary>
    internal static ResourceArtwork? SelectPackageEntry(IEnumerable<ResourceArtwork> candidates, string role)
    {
        var entries = candidates.ToArray();
        if (role != "hero") return entries.Where(e => BundledResourceStore.MatchesRole(e.Role, role)).OrderBy(e => e.Order ?? 0).FirstOrDefault();
        return entries.FirstOrDefault(e => e.Role == "model")
            ?? entries.Where(e => e.Role == "lottie").OrderBy(e => e.Order ?? 0).FirstOrDefault()
            ?? entries.FirstOrDefault(e => e.Role == "detail");
    }

    private void Release(string package)
    {
        lock (_leaseGate)
        {
            if (_leases.TryGetValue(package, out int count) && count > 1) _leases[package] = count - 1;
            else { _leases.Remove(package); if (_retired.Contains(package)) TryDelete(package); }
        }
    }
    private void Retire(string package)
    { lock (_leaseGate) { _retired.Add(package); if (!_leases.ContainsKey(package)) TryDelete(package); } }
    private void TryDelete(string package)
    {
        try { ResourcePaths.DeleteOwnedDirectory(_online, package); _retired.Remove(package); }
        catch (Exception error) when (error is IOException or InvalidDataException) { /* Retain for the next cleanup attempt; never invalidate the new pointer. */ }
    }

    private void LoadCachedCatalog(ResourceSource source)
    {
        string root = Path.Combine(SourceRoot(source), "config"); ResourceCatalog? newest = null; string? newestDirectory = null;
        if (Directory.Exists(root)) foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                string name = Path.GetFileName(directory);
                if (!IsHash(name)) continue;
                byte[] plain = ResourceCacheCodec.Decrypt(File.ReadAllBytes(Path.Combine(directory, "config.cache")), Convert.FromBase64String(Preferences.CacheEncryptionKey));
                var candidate = ResourceCatalog.Parse(plain, source, true);
                if (candidate.Sha256 != name) continue;
                if (newest is null || candidate.ContentVersion > newest.ContentVersion) { newest = candidate; newestDirectory = directory; }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException or ArgumentException) { }
        }
        _catalogs[source] = ResourceCatalog.Select(Bundled.GetCatalog(source), newest);
        if (Directory.Exists(root)) foreach (string directory in Directory.EnumerateDirectories(root))
            if (directory != newestDirectory && IsHash(Path.GetFileName(directory))) Retire(directory);
        string metadata = Path.Combine(SourceRoot(source), "metadata.json");
        try { if (File.Exists(metadata)) _fileVersions[source] = JsonSerializer.Deserialize<string>(File.ReadAllBytes(metadata))!; }
        catch (Exception error) when (error is IOException or JsonException) { }
    }

    private bool CachedCatalogExists(ResourceSource source)
    {
        if (!_catalogs[source].IsOnline) return false;
        try
        {
            byte[] bytes = ResourceCacheCodec.Decrypt(File.ReadAllBytes(Path.Combine(SourceRoot(source), "config", _catalogs[source].Sha256, "config.cache")), Convert.FromBase64String(Preferences.CacheEncryptionKey));
            return Convert.ToHexStringLower(SHA256.HashData(bytes)) == _catalogs[source].Sha256;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or ArgumentException) { return false; }
    }
    private async Task SaveCatalogAsync(ResourceSource source, ResourceCatalog catalog, byte[] bytes, CancellationToken token)
    {
        string root = Path.Combine(SourceRoot(source), "config"), directory = Path.Combine(root, catalog.Sha256);
        await ResourceFiles.AtomicWriteAsync(Path.Combine(directory, "config.cache"), ResourceCacheCodec.Encrypt(bytes, Convert.FromBase64String(Preferences.CacheEncryptionKey)), token).ConfigureAwait(false);
    }
    private void CleanupCatalogs(ResourceSource source, string currentHash)
    {
        string root = Path.Combine(SourceRoot(source), "config");
        foreach (string old in Directory.EnumerateDirectories(root)) if (Path.GetFileName(old) != currentHash) Retire(old);
    }
    private Task SavePreferencesAsync(CancellationToken token) => ResourceFiles.AtomicWriteAsync(Path.Combine(_storage, "preferences.json"), JsonSerializer.SerializeToUtf8Bytes(Preferences, ResourceJson.Options), token);
    private string ActiveKey(ResourceSource source) => (source == ResourceSource.HeyMelody ? Preferences.HeyMelodyKey : Preferences.WirelessEarphonesKey) is { Length: > 0 } key ? key : Bundled.GetDefaultKey(source);
    private string SourceRoot(ResourceSource source) => Path.Combine(_online, SourceName(source));
    private static string SourceName(ResourceSource source) => source == ResourceSource.HeyMelody ? "heymelody" : "wireless";
    private static string ArtworkSlot(string productId, int color, bool model) => productId + "_" + color.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + (model ? "14" : "control");
    private string ArtworkRoot(ResourceSource source, string productId, int color, bool model) => Path.Combine(SourceRoot(source), "artwork", ArtworkSlot(productId, color, model));
    private static bool ValidKey(string value) { try { return Convert.FromBase64String(value).Length == 32; } catch (FormatException) { return false; } }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    // "<sha256>" or "<sha256>-<8 hex>" when the digest was re-extracted while its first copy was leased.
    private static bool IsPackageName(string? value) => value is { Length: 64 } ? IsHash(value)
        : value is { Length: 73 } && value[64] == '-' && IsHash(value[..64]) && value[65..].All(Uri.IsHexDigit);
    private static string PackageDigest(string name) => name[..64];
    private static string? ReadArtworkPointer(string slot)
    {
        try { var value = JsonSerializer.Deserialize<string>(File.ReadAllBytes(Path.Combine(slot, "current.json"))); return IsPackageName(value) ? value : null; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private static bool ValidArtworkPackage(string package)
    {
        try
        {
            if (!File.Exists(Path.Combine(package, "config.json"))) return false;
            // Packages indexed by an older extractor are re-downloaded once and served until then.
            using (var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, ArtworkPackageExtractor.MarkerFile))))
                if (!marker.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version)
                    || version != ArtworkPackageExtractor.SchemaVersion) return false;
            var entries = JsonSerializer.Deserialize<ResourceArtwork[]>(File.ReadAllBytes(Path.Combine(package, "artwork.json")), ResourceJson.Options);
            if (entries is null || entries.Length == 0) return false;
            return entries.All(entry =>
            {
                string path = ResourcePaths.Resolve(package, entry.Path);
                return File.Exists(path) && new FileInfo(path).Length == entry.Length && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == entry.Sha256;
            });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException) { return false; }
    }
    /// <summary>Removes interrupted downloads and superseded packages left by an earlier Host process.</summary>
    private void SweepArtwork(ResourceSource source)
    {
        string root = Path.Combine(SourceRoot(source), "artwork");
        if (!Directory.Exists(root)) return;
        foreach (string slot in Directory.EnumerateDirectories(root))
        {
            try
            {
                var current = ReadArtworkPointer(slot);
                foreach (string directory in Directory.EnumerateDirectories(slot))
                {
                    string name = Path.GetFileName(directory);
                    if (name.StartsWith(".download-", StringComparison.Ordinal) || current is not null && name != current
                        && Directory.Exists(Path.Combine(slot, current)) && IsPackageName(name)) Retire(directory);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private enum RefreshOutcome { Skipped, Unchanged, Updated, Failed }

    public void Dispose() { _lifetime.Cancel(); _sourceCancellation.Cancel(); _vendor.Dispose(); }
}
