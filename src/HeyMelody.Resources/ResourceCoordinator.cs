using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeyMelodyWindows.Resources;

public sealed record ResourceChangedEventArgs(ResourceSource Source, long Revision, bool CatalogChanged, bool ArtworkChanged);

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
            foreach (ResourceSource source in Enum.GetValues<ResourceSource>()) LoadCachedCatalog(source);
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
    {
        await InitializeAsync(token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _sourceCancellation.Token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        bool updated = false;
        try
        {
            if (!Preferences.UseOnlineResources) return false;
            var source = Preferences.Source; string check = SourceName(source) + "/config";
            if (!force && _checks.TryGetValue(check, out var next) && next > DateTimeOffset.UtcNow) return false;
            try
            {
                var metadata = await _vendor.GetConfigurationMetadataAsync(source, ActiveKey(source), linked.Token).ConfigureAwait(false);
                bool cacheValid = CachedCatalogExists(source);
                if (!_fileVersions.TryGetValue(source, out var saved) || saved != metadata.FileVersion || !cacheValid)
                {
                    byte[] bytes = await _vendor.DownloadAsync(metadata.DownloadUrl, ResourceLimits.ConfigBytes, linked.Token).ConfigureAwait(false);
                    if (metadata.Size is long expected && bytes.LongLength != expected) throw new InvalidDataException("Configuration length differs from metadata.");
                    var candidate = ResourceCatalog.Parse(bytes, source, true);
                    if (candidate.ContentVersion > _catalogs[source].ContentVersion)
                    {
                        await SaveCatalogAsync(source, candidate, bytes, linked.Token).ConfigureAwait(false);
                        _catalogs[source] = candidate; Revision++; updated = true;
                        CleanupCatalogs(source, candidate.Sha256);
                    }
                    else if (!cacheValid && _catalogs[source].IsOnline)
                    {
                        // Repair the persisted current snapshot without adopting an equal/older response.
                        var retained = _catalogs[source];
                        await SaveCatalogAsync(source, retained, retained.OriginalBytes, linked.Token).ConfigureAwait(false);
                        CleanupCatalogs(source, retained.Sha256);
                    }
                    _fileVersions[source] = metadata.FileVersion;
                    await ResourceFiles.AtomicWriteAsync(Path.Combine(SourceRoot(source), "metadata.json"), JsonSerializer.SerializeToUtf8Bytes(metadata.FileVersion), linked.Token).ConfigureAwait(false);
                }
                _checks[check] = DateTimeOffset.UtcNow.AddMinutes(10); LastSuccessfulCheck = DateTimeOffset.UtcNow; LastError = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or CryptographicException or ArgumentException)
            { _checks[check] = DateTimeOffset.UtcNow.AddSeconds(30); LastError = "在线资源更新失败，继续使用当前有效资源。"; }
        }
        finally { _gate.Release(); }
        if (updated) Changed?.Invoke(this, new(Preferences.Source, Revision, true, false));
        return updated;
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
                await RefreshArtworkAsync(productId, color.Value, true, false, token).ConfigureAwait(false);
                if (source != Preferences.Source) return null;
                var found = AcquireCachedArtwork(source, productId, color.Value, true, role); if (found is not null) return found;
                found = Bundled.Acquire(Bundled.FindAsset(source, productId, color, "model"), Revision); if (found is not null) return found;
            }
            if (role != "model")
            {
                await RefreshArtworkAsync(productId, color.Value, false, false, token).ConfigureAwait(false);
                if (source != Preferences.Source) return null;
                var found = AcquireCachedArtwork(source, productId, color.Value, false, role); if (found is not null) return found;
            }
        }
        return Bundled.Acquire(Bundled.FindAsset(source, productId, color, role), Revision)
            ?? Bundled.Acquire(Bundled.FindAsset(source, productId, color, "detail"), Revision)
            ?? Bundled.Acquire(Bundled.FindGenericAsset(source, profile.Type, role), Revision);
    }

    public async Task<bool> RefreshArtworkAsync(string productId, int color, bool model = false, bool force = false, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _sourceCancellation.Token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        bool updated = false;
        try
        {
            if (!Preferences.UseOnlineResources || Catalog.Find(productId) is null) return false;
            lock (_leaseGate) _artworkTargets.Add((productId, color, model));
            var source = Preferences.Source; string key = SourceName(source) + "/" + ArtworkSlot(productId, color, model);
            if (!force && _checks.TryGetValue(key, out var next) && next > DateTimeOffset.UtcNow) return false;
            try
            {
                var metadata = await _vendor.GetArtworkMetadataAsync(source, ActiveKey(source), productId, color, model, linked.Token).ConfigureAwait(false);
                if (metadata is not null)
                {
                    string slot = ArtworkRoot(source, productId, color, model);
                    string package = Path.Combine(slot, metadata.Sha256);
                    var current = ReadArtworkPointer(slot);
                    if (current != metadata.Sha256 || !ValidArtworkPackage(package))
                    {
                        byte[] archive = await _vendor.DownloadAsync(metadata.DownloadUrl, ResourceLimits.ArchiveBytes, linked.Token).ConfigureAwait(false);
                        if (Convert.ToHexStringLower(SHA256.HashData(archive)) != metadata.Sha256) throw new InvalidDataException("Resource digest mismatch.");
                        string temporary = Path.Combine(slot, ".download-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporary);
                        try
                        {
                            var entries = ExtractArtwork(archive, temporary, source, productId, color);
                            if (entries.Length == 0) throw new InvalidDataException("Resource package has no usable artwork.");
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(temporary, "artwork.json"), JsonSerializer.SerializeToUtf8Bytes(entries, ResourceJson.Options), linked.Token).ConfigureAwait(false);
                            linked.Token.ThrowIfCancellationRequested();
                            if (Directory.Exists(package))
                            {
                                lock (_leaseGate)
                                {
                                    if (_leases.ContainsKey(package)) throw new IOException("Resource package is still in use.");
                                    ResourcePaths.DeleteOwnedDirectory(_online, package);
                                }
                            }
                            if (Directory.Exists(package)) throw new IOException("Resource package is still in use.");
                            Directory.Move(temporary, package);
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(slot, "current.json"), JsonSerializer.SerializeToUtf8Bytes(metadata.Sha256), linked.Token).ConfigureAwait(false);
                            foreach (string old in Directory.EnumerateDirectories(slot)) if (old != package) Retire(old);
                            Revision++; updated = true;
                        }
                        finally { if (Directory.Exists(temporary)) ResourcePaths.DeleteOwnedDirectory(_online, temporary); }
                    }
                }
                _checks[key] = DateTimeOffset.UtcNow.AddMinutes(10); LastError = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or CryptographicException or ArgumentException)
            { _checks[key] = DateTimeOffset.UtcNow.AddSeconds(30); LastError = "在线美术资源更新失败，继续使用当前有效资源。"; }
        }
        finally { _gate.Release(); }
        if (updated) Changed?.Invoke(this, new(Preferences.Source, Revision, false, true));
        return updated;
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
                var entry = entries.FirstOrDefault(e => e.Source == source && e.ProductIds.Contains(productId, StringComparer.Ordinal) && e.Color == color && BundledResourceStore.MatchesRole(e.Role, role));
                if (entry is null && role == "hero") entry = entries.FirstOrDefault(e => e.Role is "model" or "detail" or "lottie");
                if (entry is null) return null;
                string path = ResourcePaths.Resolve(package, entry.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Length || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != entry.Sha256) return null;
                _leases[package] = _leases.GetValueOrDefault(package) + 1;
                return new(path, entry.Kind, package, entry.Sha256, entry.Length, source, Revision, () => Release(package));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException) { return null; }
        }
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

    private static ResourceArtwork[] ExtractArtwork(byte[] bytes, string root, ResourceSource source, string productId, int color)
    {
        using var memory = new MemoryStream(bytes); using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > ResourceLimits.Members || zip.Entries.Sum(e => e.Length) > ResourceLimits.ExpandedBytes) throw new InvalidDataException("Resource archive exceeds limit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!names.Add(entry.FullName) || entry.Length > ResourceLimits.MemberBytes || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Invalid resource archive member.");
            string path = ResourcePaths.Resolve(root, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = File.Create(path);
            byte[] buffer = new byte[81920]; int read; long total = 0;
            while ((read = input.Read(buffer)) > 0) { total += read; if (total > ResourceLimits.MemberBytes || total > entry.Length) throw new InvalidDataException("Archive member exceeds declared length."); output.Write(buffer, 0, read); }
            if (total != entry.Length) throw new InvalidDataException("Truncated archive member.");
        }
        string configPath = Path.Combine(root, "config.json");
        if (!File.Exists(configPath)) throw new InvalidDataException("Resource package lacks config.json.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(configPath), new JsonDocumentOptions { MaxDepth = 64 });
        ResourceJson.ValidateUniqueProperties(json.RootElement);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Resource configuration must be an object.");
        var result = new List<ResourceArtwork>();
        foreach (var property in json.RootElement.EnumerateObject())
        {
            string role = property.Name switch { "capsuleImageRes" => "capsule", "detailImageRes" => "detail", "leftImageRes" => "left", "rightImageRes" => "right", "boxImageRes" => "box", "model3D" => "model", _ when property.Name.Contains("lottie", StringComparison.OrdinalIgnoreCase) => "lottie", _ => "" };
            if (role.Length == 0) continue;
            Walk(property.Value, role);
        }
        return result.ToArray();
        void Walk(JsonElement node, string role)
        {
            if (node.ValueKind == JsonValueKind.Array) { foreach (var item in node.EnumerateArray()) Walk(item, role); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (!node.TryGetProperty("path", out var value) || value.ValueKind != JsonValueKind.String) return;
            string relative = value.GetString()!, path = ResourcePaths.Resolve(root, relative);
            if (!File.Exists(path)) throw new InvalidDataException("Missing configured resource.");
            string extension = Path.GetExtension(path).ToLowerInvariant(); string kind;
            if (extension == ".vfxms")
            {
                byte[] container = File.ReadAllBytes(path);
                if (container.Length < 40) throw new InvalidDataException("Invalid model container.");
                int start = BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(8, 4)), length = BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(12, 4));
                if (start < 40 || length < 12 || (long)start + length > container.Length || !container.AsSpan(start, 4).SequenceEqual("glTF"u8)) throw new InvalidDataException("Invalid model segment.");
                relative = "model.glb"; path = Path.Combine(root, relative); File.WriteAllBytes(path, container.AsSpan(start, length).ToArray()); kind = "model";
            }
            else if (extension == ".glb") kind = "model";
            else if (extension == ".json" && role == "lottie")
            {
                using var animation = JsonDocument.Parse(File.ReadAllBytes(path));
                if (animation.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid animation configuration.");
                if (animation.RootElement.TryGetProperty("assets", out var images))
                {
                    if (images.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid animation assets.");
                    foreach (var image in images.EnumerateArray())
                    {
                        if (image.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid animation asset.");
                        if (image.TryGetProperty("p", out var imagePath) && imagePath.ValueKind == JsonValueKind.String && !imagePath.GetString()!.StartsWith("data:", StringComparison.Ordinal))
                        {
                            if (image.TryGetProperty("u", out var u) && u.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException("Invalid animation image directory.");
                            string prefix = image.TryGetProperty("u", out u) ? u.GetString() ?? "" : "";
                            string imageFile = ResourcePaths.Resolve(Path.GetDirectoryName(path)!, prefix + imagePath.GetString());
                            if (!File.Exists(imageFile)) throw new InvalidDataException("Missing animation image.");
                        }
                    }
                }
                kind = "lottie";
            }
            else if (extension is ".png" or ".webp" or ".jpg" or ".jpeg") kind = "bitmap";
            else return;
            var raw = File.ReadAllBytes(path);
            if (kind == "model") ValidateGlb(raw);
            result.Add(new() { Source = source, ProductIds = [productId], Color = color, Kind = kind, Role = role, Path = relative, Sha256 = Convert.ToHexStringLower(SHA256.HashData(raw)), Length = raw.Length });
        }
    }

    private static void ValidateGlb(byte[] bytes)
    {
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("glTF"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != bytes.Length
            || !bytes.AsSpan(16, 4).SequenceEqual("JSON"u8)) throw new InvalidDataException("Invalid GLB structure.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        if (length > ResourceLimits.ConfigBytes || 20L + length > bytes.Length) throw new InvalidDataException("Invalid GLB JSON segment.");
        using var json = JsonDocument.Parse(bytes.AsMemory(20, checked((int)length)), new JsonDocumentOptions { MaxDepth = 96 });
        ResourceJson.ValidateUniqueProperties(json.RootElement);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid GLB JSON structure.");
        // The existing desktop GLB loader consumes self-contained models only.
        foreach (string field in new[] { "buffers", "images" })
            if (json.RootElement.TryGetProperty(field, out var rows))
            {
                if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid GLB resources.");
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid GLB resource.");
                    if (row.TryGetProperty("uri", out var uri) && (uri.ValueKind != JsonValueKind.String || !uri.GetString()!.StartsWith("data:", StringComparison.Ordinal)))
                        throw new InvalidDataException("External model URI is not supported.");
                }
            }
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
    private static string? ReadArtworkPointer(string slot)
    {
        try { var value = JsonSerializer.Deserialize<string>(File.ReadAllBytes(Path.Combine(slot, "current.json"))); return IsHash(value) ? value : null; }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }
    private static bool ValidArtworkPackage(string package)
    {
        try
        {
            if (!File.Exists(Path.Combine(package, "config.json"))) return false;
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
    public void Dispose() { _lifetime.Cancel(); _sourceCancellation.Cancel(); _vendor.Dispose(); }
}
