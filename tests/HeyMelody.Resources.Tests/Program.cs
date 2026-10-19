using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.Resources;

var tests = new (string Name, Func<Task> Run)[]
{
    ("The complete HeyMelody catalog and artwork load", Bundles),
    ("Compatibility honors non-null zero and empty lists", Compatibility),
    ("Child expansion matches the official inheritance whitelist", Children),
    ("Whole snapshot selection respects content version", Selection),
    ("Duplicate JSON keys and malformed identifiers are rejected", InvalidCatalogs),
    ("HMAC signs the exact UTF-8 request including raw custom key", Signature),
    ("Cache envelope authenticates data and rejects tampering", Cache),
    ("Plaintext keys and offline preference survive restart", Preferences),
    ("Legacy selections preserve HeyMelody settings and cached resources", LegacyPreferences),
    ("Online update picks strictly higher content and deletes older online cache", Versions),
    ("Failed download keeps the effective catalog and cache", FailedUpdate),
    ("Online off avoids every request and reuses downloaded content", Offline),
    ("Unchanged metadata repairs a damaged cache without publishing equal content", CacheRepair),
    ("Art publication preserves leases and cleans retired online packages", ArtworkLeases),
    ("Hash failures and unsafe archives retain current art", UnsafeArtwork),
    ("Known product identity and color are required for model artwork", ArtworkIdentity),
    ("Model metadata without PID is bound to request and validates GLB", ModelPackages),
    ("Model WebP is obtained without a 3D flag, stays animated offline and rejects damaged replacements", AnimatedModelPackages),
    ("Cached art is served before any network check and refreshed in the background", CachedFirst),
    ("HTTP timeouts fall back to installed art and are throttled like failures", TimeoutFallback),
    ("A first download is awaited briefly and its late package is announced", FirstDownload),
    ("Lottie tutorials retain provenance and never replace the product hero", LottieLists),
    ("Re-extraction publishes beside a leased copy of the same digest", LeasedRepublish),
    ("Packages from an older extractor are re-downloaded once and served until then", ExtractorUpgrade),
    ("Re-download validates replacements, keeps files on failure and requires online use", Redownload),
    ("Startup removes interrupted downloads and superseded packages", StartupSweep),
};
if (args is ["--verify-x4-artwork", var controlPackage, var modelPackage])
    tests = [.. tests, ("Real X4 packages resolve animated product art and static fallback from HeyMelody", () => RealX4Artwork(controlPackage, modelPackage))];
int failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} resource tests passed");
return failed == 0 ? 0 : 1;

static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
static void Eq<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static Task Done(Action action) { action(); return Task.CompletedTask; }
static byte[] Config(int version, string list = "[{\"id\":\"06EC10\",\"name\":\"OPPO Enco X4\",\"brand\":\"oppo\",\"type\":\"T1\",\"supportSpp\":true,\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\",\"function\":{\"batteryInfo\":1}}]")
    => Encoding.UTF8.GetBytes("{\"versionCode\":" + version + ",\"whiteList\":" + list + "}");
static Task Bundles() => Done(() =>
{
    var store = new BundledResourceStore(); var hey = store.GetCatalog();
    Eq(85, hey.Profiles.Count); Eq(117006004, hey.ContentVersion);
    Check(hey.Find("49074B") is null, "Speaker was included");
    Check(hey.Find("06EC10")!.CanUseControlTransport, "X4 transport missing");
    Eq(365, store.Artwork.Count);
    foreach (var asset in store.Artwork)
    {
        string file = store.FullPath(asset); Check(File.Exists(file), "Missing bundled art"); Eq(asset.Length, new FileInfo(file).Length);
        Eq(asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        if (asset.Kind == "lottie")
        {
            using var animation = JsonDocument.Parse(File.ReadAllBytes(file));
            if (animation.RootElement.TryGetProperty("assets", out var images)) foreach (var image in images.EnumerateArray())
                if (image.TryGetProperty("p", out var p) && !p.GetString()!.StartsWith("data:", StringComparison.Ordinal))
                    Check(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, image.TryGetProperty("u", out var u) ? u.GetString() ?? "" : "", p.GetString()!)), "Bundled Lottie image missing");
        }
    }
    Eq(24, store.GetDefaultKey().Length);
});
static Task Compatibility() => Done(() =>
{
    var json = Encoding.UTF8.GetBytes("{\"versionCode\":50,\"compatVersionCode\":0,\"whiteList\":[{\"id\":\"06EC10\",\"brand\":\"oppo\"}],\"compatWhiteList\":[],\"caseNumSite\":5,\"compatCaseNumSite\":0,\"appMarket\":\"a\",\"compatAppMarket\":\"\"}");
    var parsed = ResourceCatalog.Parse(json, true); Eq(0, parsed.ContentVersion); Eq(0, parsed.Profiles.Count);
    Eq(0, parsed.Raw.GetProperty("caseNumSite").GetInt32()); Eq("", parsed.Raw.GetProperty("appMarket").GetString());
    parsed = ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":50,\"compatVersionCode\":null,\"whiteList\":[],\"compatWhiteList\":null}"), true); Eq(50, parsed.ContentVersion);
});
static Task Children() => Done(() =>
{
    string list = "[{\"id\":\"060414\",\"name\":\"parent\",\"brand\":\"oneplus\",\"type\":\"T1\",\"supportSpp\":true,\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\",\"function\":{\"equalizer\":4},\"children\":[{\"name\":\"alias\"},{\"id\":\"060814\",\"name\":\"child\",\"uuid\":\"\",\"function\":{\"equalizer\":99},\"supportSpp\":false}]}]";
    var parsed = ResourceCatalog.Parse(Config(1, list), false); Eq(2, parsed.Profiles.Count);
    var child = parsed.Find("060814")!; Eq("child", child.Name); Check(child.SupportSpp, "Arbitrary child field replaced parent");
    Eq(4, child.Functions.GetProperty("equalizer").GetInt32()); Eq(1, child.Functions.GetProperty("hideFromUIList").GetInt32());
    Check(!child.Raw.TryGetProperty("children", out _), "Child kept nested children");
});
static Task Selection() => Done(() =>
{
    var bundled = ResourceCatalog.Parse(Config(5), false);
    foreach (int version in new[] { 3, 5, 6 })
    {
        var online = ResourceCatalog.Parse(Config(version), true);
        Check(ReferenceEquals(version > 5 ? online : bundled, ResourceCatalog.Select(bundled, online)), "Whole snapshot precedence wrong");
    }
});
static Task InvalidCatalogs() => Done(() =>
{
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":1,\"versionCode\":2,\"whiteList\":[]}"), true));
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":true,\"whiteList\":[]}"), true));
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Config(1, "[{\"id\":\"06ec10\",\"brand\":\"oppo\"}]"), true));
    var profile = ResourceCatalog.Parse(Config(1, "[{\"id\":\"06EC10\",\"brand\":\"oppo\",\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\"}]"), true).Find("06EC10")!;
    Check(!profile.SupportSpp && !profile.CanUseControlTransport, "Missing SPP flag guessed true");
});
static Task Signature() => Done(() =>
{
    byte[] body = VendorResourceClient.CreateRequestBody();
    Eq("{\"platform\":\"android\",\"channel\":\"2\",\"versionCode\":\"17006004\"}", Encoding.UTF8.GetString(body));
    string rawKey = "明文-custom-key";
    using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(rawKey)); Eq(Convert.ToHexStringLower(hmac.ComputeHash(body)), VendorResourceClient.Sign(rawKey, body));
    var model = JsonDocument.Parse(VendorResourceClient.CreateRequestBody("067410", 2, true)).RootElement;
    Eq("14", model.GetProperty("moduleId").GetString()); Eq("17006004", model.GetProperty("versionCode").GetString());
    Eq("2", model.GetProperty("channel").GetString());
    Throws<InvalidDataException>(() => VendorResourceClient.ValidateDownloadUri("https://evil.example/resource"));
});
static Task Cache() => Done(() =>
{
    byte[] key = RandomNumberGenerator.GetBytes(32), original = Config(10), encrypted = ResourceCacheCodec.Encrypt(original, key);
    Check(ResourceCacheCodec.Decrypt(encrypted, key).SequenceEqual(original), "Cache changed bytes"); encrypted[^1] ^= 1;
    Throws<AuthenticationTagMismatchException>(() => ResourceCacheCodec.Decrypt(encrypted, key));
    Throws<AuthenticationTagMismatchException>(() => ResourceCacheCodec.Decrypt(ResourceCacheCodec.Encrypt(original, key), RandomNumberGenerator.GetBytes(32)));
    Throws<InvalidDataException>(() => ResourceCacheCodec.Encrypt(new byte[ResourceLimits.ConfigBytes + 1], key));
});
static async Task Preferences()
{
    using var fixture = new Fixture(); using var first = fixture.Coordinator(); await first.InitializeAsync();
    Check(first.Preferences.UseOnlineResources, "Online should default on");
    string encryption = first.Preferences.CacheEncryptionKey;
    await first.UpdatePreferencesAsync(first.Preferences with { UseOnlineResources = false, HeyMelodyKey = "user-hey" });
    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Path, "preferences.json")));
    Eq("user-hey", saved.RootElement.GetProperty("HeyMelodyKey").GetString());
    Check(!saved.RootElement.TryGetProperty("Source", out _) && !saved.RootElement.TryGetProperty("WirelessEarphonesKey", out _), "Removed preferences were written");
    Eq(encryption, saved.RootElement.GetProperty("CacheEncryptionKey").GetString());
    using var second = fixture.Coordinator(); await second.InitializeAsync(); Eq(encryption, second.Preferences.CacheEncryptionKey);
    Check(!second.Preferences.UseOnlineResources, "Offline preference lost"); Eq(0, fixture.Handler.Requests);
}
static async Task LegacyPreferences()
{
    foreach (int legacySelection in new[] { 0, 1 })
    {
        using var fixture = new Fixture();
        byte[] key = RandomNumberGenerator.GetBytes(32), cached = Config(117006009), ignored = Config(999999999);
        string encryption = Convert.ToBase64String(key);
        Directory.CreateDirectory(fixture.Path);
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, "preferences.json"), JsonSerializer.Serialize(new
        {
            Source = legacySelection, UseOnlineResources = false, HeyMelodyKey = "user-hey",
            WirelessEarphonesKey = "retired-key", CacheEncryptionKey = encryption
        }));
        async Task<string> Seed(string directory, byte[] bytes)
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string path = Path.Combine(fixture.Path, "online", directory, "config", digest, "config.cache");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, ResourceCacheCodec.Encrypt(bytes, key));
            return path;
        }
        await Seed("heymelody", cached);
        string ignoredCache = await Seed("wireless", ignored);
        using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
        Eq("user-hey", coordinator.Preferences.HeyMelodyKey); Eq(encryption, coordinator.Preferences.CacheEncryptionKey);
        Check(!coordinator.Preferences.UseOnlineResources, "Legacy offline preference lost");
        Eq(117006009, coordinator.Catalog.ContentVersion); Check(coordinator.Catalog.IsOnline, "Encrypted HeyMelody cache was discarded");
        Check(File.Exists(ignoredCache), "Unrelated legacy cache was mutated");
        Check(!await coordinator.RefreshAsync(true), "Offline migration attempted an update"); Eq(0, fixture.Handler.Requests);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Path, "preferences.json")));
        Check(!saved.RootElement.TryGetProperty("Source", out _) && !saved.RootElement.TryGetProperty("WirelessEarphonesKey", out _), "Legacy fields survived migration");
    }
}
static async Task Versions()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { HeyMelodyKey = "test-key" });
    fixture.Handler.Configuration = Config(117006010); fixture.Handler.FileVersion = "90"; Check(await coordinator.RefreshAsync(true), "New config not adopted");
    string first = coordinator.Catalog.Sha256; Eq(1, Directory.GetDirectories(Path.Combine(fixture.Path, "online/heymelody/config")).Length);
    fixture.Handler.Configuration = Config(117006011); fixture.Handler.FileVersion = "80"; Check(await coordinator.RefreshAsync(true), "Changed metadata compared numerically");
    Check(!Directory.Exists(Path.Combine(fixture.Path, "online/heymelody/config", first)), "Old online config not removed"); Eq(117006011, coordinator.Catalog.ContentVersion);
    fixture.Handler.Configuration = Config(117006011); fixture.Handler.FileVersion = "81"; Check(!await coordinator.RefreshAsync(true), "Equal content replaced current");
    fixture.Handler.Configuration = Config(117006009); fixture.Handler.FileVersion = "82"; Check(!await coordinator.RefreshAsync(true), "Older content adopted"); Eq(117006011, coordinator.Catalog.ContentVersion);
    Eq("test-key", fixture.Handler.LastVerifiedKey); Eq(1, Directory.GetDirectories(Path.Combine(fixture.Path, "online/heymelody/config")).Length);
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false }); Eq(117006011, coordinator.Catalog.ContentVersion);
}
static async Task FailedUpdate()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Configuration = Config(117006010); Check(await coordinator.RefreshAsync(true), "Initial update failed"); var catalog = coordinator.Catalog;
    fixture.Handler.FileVersion = "changed"; fixture.Handler.Configuration = Encoding.UTF8.GetBytes("{broken"); Check(!await coordinator.RefreshAsync(true), "Malformed config accepted"); Check(ReferenceEquals(catalog, coordinator.Catalog), "Failure changed catalog");
    Check(File.Exists(Path.Combine(fixture.Path, "online/heymelody/config", catalog.Sha256, "config.cache")), "Failure removed previous cache"); Check(coordinator.LastError is not null, "Failure not reported");
}
static async Task Offline()
{
    using var fixture = new Fixture();
    using (var coordinator = fixture.Coordinator()) { await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010); await coordinator.RefreshAsync(true); await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false }); }
    int before = fixture.Handler.Requests;
    using var fresh = fixture.Coordinator(); await fresh.InitializeAsync(); Eq(117006010, fresh.Catalog.ContentVersion); Check(!await fresh.RefreshAsync(true), "Offline made config request");
    using var art = await fresh.ResolveArtworkAsync("06EC10", 0, "detail"); Check(art is not null && art.Kind == "bitmap", "Offline fallback missing"); Eq(before, fixture.Handler.Requests);
}
static async Task ArtworkLeases()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Artwork = ArtZip(1); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "First art failed");
    var lease = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Check(lease is not null, "No art lease"); string old = lease!.PackageRoot;
    fixture.Handler.Artwork = ArtZip(2); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "New art failed"); Check(Directory.Exists(old), "Active renderer lost old resource");
    using var newer = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Check(newer is not null && newer.PackageRoot != old, "New pointer not published"); lease.Dispose(); Check(!Directory.Exists(old), "Released old online package not cleaned");
    using var installed = coordinator.Bundled.Acquire(coordinator.Bundled.FindAsset("06EC10", null, "detail")); Check(installed is not null && File.Exists(installed.FullPath), "Bundled art removed");
}
static async Task CacheRepair()
{
    using var fixture = new Fixture();
    using (var coordinator = fixture.Coordinator())
    {
        await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010); await coordinator.RefreshAsync(true);
        string path = Path.Combine(fixture.Path, "online/heymelody/config", coordinator.Catalog.Sha256, "config.cache");
        byte[] broken = await File.ReadAllBytesAsync(path); broken[^1] ^= 1; await File.WriteAllBytesAsync(path, broken);
        long revision = coordinator.Revision; Check(!await coordinator.RefreshAsync(true), "Equal content published"); Eq(revision, coordinator.Revision);
        Check(ResourceCacheCodec.Decrypt(await File.ReadAllBytesAsync(path), Convert.FromBase64String(coordinator.Preferences.CacheEncryptionKey)).SequenceEqual(fixture.Handler.Configuration), "Cache was not repaired");
    }
    using var fresh = fixture.Coordinator(); await fresh.InitializeAsync(); Eq(117006010, fresh.Catalog.ContentVersion);
}
static async Task UnsafeArtwork()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); await coordinator.RefreshArtworkAsync("06EC10", 0, force: true);
    using var before = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    fixture.Handler.Artwork = ArtZip(2); fixture.Handler.WrongHash = true; Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Wrong hash accepted"); Check(File.Exists(before!.FullPath), "Hash failure deleted art");
    fixture.Handler.WrongHash = false; fixture.Handler.Artwork = ArtZip(3, "../escaped.png"); Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Traversal archive accepted"); Check(!File.Exists(Path.Combine(fixture.Path, "escaped.png")), "Archive escaped cache");
    using var after = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Eq(before.PackageRoot, after!.PackageRoot);
}
static Task ArtworkIdentity() => Done(() =>
{
    var store = new BundledResourceStore(); Check(store.FindAsset("068C10", 1, "model") is not null, "Verified Free4 model missing");
    Check(store.FindAsset("068C10", null, "model") is null, "Model color guessed"); Check(store.FindAsset("06C010", 1, "model") is null, "Free4 model applied to Dynaudio"); Check(store.FindAsset("06EC10", 1, "model") is null, "Free4 model applied to X4");
    Check(store.FindAsset("060410", 0, "detail") is null, "Removed artwork remained in the bundle");
    foreach (string shape in new[] { "T1", "N", "O1" }) Check(store.FindGenericAsset(shape, "hero") is not null, "Generic shape fallback missing");
});
static async Task ModelPackages()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    var verified = coordinator.Bundled.FindAsset("068C10", 1, "model")!;
    fixture.Handler.Artwork = ModelZip(File.ReadAllBytes(coordinator.Bundled.FullPath(verified)));
    Check(await coordinator.RefreshArtworkAsync("068C10", 1, model: true, force: true), "Valid self-contained model rejected");
    using var lease = await coordinator.ResolveArtworkAsync("068C10", 1, "model"); Check(lease is not null && lease.Kind == "model", "Request-only model association was lost"); Eq(verified.Sha256, lease!.Sha256);
    byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"https://external.example/geometry.bin\"}]} ");
    byte[] glb = new byte[20 + json.Length]; "glTF"u8.CopyTo(glb); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)glb.Length); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)json.Length); "JSON"u8.CopyTo(glb.AsSpan(16)); json.CopyTo(glb, 20);
    fixture.Handler.Artwork = ModelZip(glb); Check(!await coordinator.RefreshArtworkAsync("068C10", 1, model: true, force: true), "External model URI accepted"); Check(File.Exists(lease.FullPath), "Rejected model removed previous valid package");
}
static async Task CachedFirst()
{
    using var fixture = new Fixture();
    using (var first = fixture.Coordinator()) { await first.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); Check(await first.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed"); }
    fixture.Handler.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    int before = fixture.Handler.Requests;
    using var art = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail").WaitAsync(TimeSpan.FromSeconds(5));
    Check(art is not null && art.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Cached online art was not served first");
    for (int attempt = 0; fixture.Handler.Pending == 0 && attempt < 200; attempt++) await Task.Delay(10);
    Check(fixture.Handler.Pending == 1 && fixture.Handler.Requests == before + 1, "Background check did not start");
    fixture.Handler.Gate.TrySetResult();
}
static async Task AnimatedModelPackages()
{
    byte[] animation = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-frame.webp"));
    {
        using var fixture = new Fixture();
        fixture.Handler.Artwork = LottieZip(); fixture.Handler.ModelArtwork = ModelWebpZip(animation);
        string root;
        using (var coordinator = fixture.Coordinator())
        {
            await coordinator.InitializeAsync();
            Check(!coordinator.Catalog.Find("06EC10")!.Functions.TryGetProperty("support3DModel", out _), "Fixture no longer exercises an appearance without the 3D flag");
            using var hero = await coordinator.ResolveArtworkAsync("06EC10", 1, "hero");
            Check(hero is { Kind: "animated-webp" }, "Module 14 WebP was skipped or frozen as a bitmap");
            root = hero!.PackageRoot;
            using var detail = await coordinator.ResolveArtworkAsync("06EC10", 1, "detail");
            Check(detail is { Kind: "bitmap" } && detail.PackageRoot != root, "Control package did not provide the static fallback");
            var broken = (byte[])animation.Clone(); broken[4] ^= 1; // RIFF size no longer matches the downloaded member.
            fixture.Handler.ModelArtwork = ModelWebpZip(broken);
            Check(!await coordinator.RefreshArtworkAsync("06EC10", 1, model: true, force: true), "Malformed animated container replaced valid art");
            using var retained = await coordinator.ResolveArtworkAsync("06EC10", 1, "hero");
            Eq(root, retained!.PackageRoot);
            await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
        }
        int requests = fixture.Handler.Requests;
        using var restarted = fixture.Coordinator(); await restarted.InitializeAsync();
        using var offline = await restarted.ResolveArtworkAsync("06EC10", 1, "hero");
        Check(offline is { Kind: "animated-webp" } && offline.PackageRoot == root && fixture.Handler.Requests == requests, "Animated cache was lost on offline restart");
        using var otherColor = await restarted.ResolveArtworkAsync("06EC10", 0, "hero");
        Check(otherColor?.Kind != "animated-webp", "Animation crossed protocol colors");
    }
}
static async Task TimeoutFallback()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Failure = new TaskCanceledException("The request timed out.");
    using var art = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(art is not null && art.Kind == "bitmap" && !art.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Timeout skipped installed fallback");
    Check(coordinator.LastError is not null, "Timeout was not reported as a failure");
    int requests = fixture.Handler.Requests;
    Check(!await coordinator.RefreshArtworkAsync("06EC10", 0) && fixture.Handler.Requests == requests, "Timeout was not throttled");
}
static async Task FirstDownload()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    coordinator.FirstDownloadWait = TimeSpan.FromMilliseconds(200);
    var changed = new TaskCompletionSource<ResourceChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.Changed += (_, args) => changed.TrySetResult(args);
    fixture.Handler.Artwork = ArtZip(1); fixture.Handler.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var fallback = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail").WaitAsync(TimeSpan.FromSeconds(5)))
        Check(fallback is not null && !fallback.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Pending download blocked the installed fallback");
    fixture.Handler.Gate.TrySetResult();
    var args = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(args.ArtworkChanged, "Late package was not announced");
    using var online = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(online is not null && online.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Announced package was not served");
}
static async Task LottieLists()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Artwork = LottieZip();
    Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Lottie package rejected");
    using var hero = await coordinator.ResolveArtworkAsync("06EC10", 0, "hero");
    Check(hero is not null && hero.Kind == "bitmap" && hero.FullPath.EndsWith("detail.png", StringComparison.Ordinal), "Gesture tutorial replaced the product hero");
    using var tutorial = await coordinator.ResolveArtworkAsync("06EC10", 0, "lottie");
    Check(tutorial is not null && tutorial.Kind == "lottie" && tutorial.FullPath.EndsWith("data.heymelody.json", StringComparison.Ordinal), "Explicit tutorial request failed");
    using (var derived = JsonDocument.Parse(File.ReadAllBytes(tutorial!.FullPath)))
    {
        var image = derived.RootElement.GetProperty("assets")[0];
        Eq("images/", image.GetProperty("u").GetString()); Eq("img_0.png", image.GetProperty("p").GetString());
    }
    var entries = JsonSerializer.Deserialize<ResourceArtwork[]>(File.ReadAllBytes(Path.Combine(hero!.PackageRoot, "artwork.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var main = entries.Single(e => e.Role == "lottie" && e.Order == 1);
    Check(main.OriginalPath == "res/lottie/main/data.json" && main.OriginalSha256 is { Length: 64 } && main.Metadata?["imagePath"] == "res/lottie/main/images", "Derived animation lost provenance");
    Check(entries.Any(e => e.Role == "lottie" && e.Order == 2 && e.OriginalPath is null) && entries.Any(e => e.Role == "lottie-left"), "Valid list entries missing");
    Check(!entries.Any(e => e.Role == "lottie" && e.Order == 0) && !entries.Any(e => e.Path.Contains("boot", StringComparison.Ordinal)), "Broken or unrelated entries indexed");
    Check(File.Exists(Path.Combine(hero.PackageRoot, "res/lottie/main/data.json")), "Original animation bytes were not kept");
    using var detail = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(detail is not null && detail.Kind == "bitmap" && detail.PackageRoot == hero.PackageRoot, "Static detail fallback missing");
}
static async Task RealX4Artwork(string controlPath, string modelPath)
{
    byte[] control = File.ReadAllBytes(controlPath), model = File.ReadAllBytes(modelPath);
    Eq("c3f1b327c3ad31d57e47250dad3ccf9e1fc25f3cba0dd170df8388e03f3f5411", Convert.ToHexStringLower(SHA256.HashData(control)));
    Eq("1bb49ddc6d0ffe4f1ab9e6282bc72962d15629bd5ef0dc7480ea4903d51ba6e0", Convert.ToHexStringLower(SHA256.HashData(model)));
    {
        using var fixture = new Fixture();
        fixture.Handler.Artwork = control; fixture.Handler.ModelArtwork = model;
        string modelRoot;
        using (var coordinator = fixture.Coordinator())
        {
            await coordinator.InitializeAsync();
            Check(await coordinator.RefreshArtworkAsync("06EC10", 1, force: true), "Real control package rejected");
            Check(await coordinator.RefreshArtworkAsync("06EC10", 1, model: true, force: true), "Real modelWebp package rejected");
            using var hero = await coordinator.ResolveArtworkAsync("06EC10", 1, "hero");
            Check(hero is { Kind: "animated-webp" }, "Real animated product art was not selected");
            modelRoot = hero!.PackageRoot;
            using var detail = await coordinator.ResolveArtworkAsync("06EC10", 1, "detail");
            Check(detail is { Kind: "bitmap" } && detail.FullPath.EndsWith("img_detail.png", StringComparison.Ordinal), "Real static fallback missing");
            await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
        }
        int requests = fixture.Handler.Requests;
        using var restarted = fixture.Coordinator(); await restarted.InitializeAsync();
        using var restored = await restarted.ResolveArtworkAsync("06EC10", 1, "hero");
        Check(restored is { Kind: "animated-webp" } && restored.PackageRoot == modelRoot && fixture.Handler.Requests == requests, "Real animation was not restored offline");
    }
}
static async Task LeasedRepublish()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Artwork = ArtZip(1); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed");
    var leased = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); string old = leased!.PackageRoot;
    fixture.Handler.Configuration = Config(117006004); // equal to the installed catalog: not adopted
    var result = await coordinator.RedownloadAsync([]);
    Check(result.Failed == 0 && result.Updated == 1, "Re-download did not replace the leased digest: " + result);
    using var replacement = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(replacement!.PackageRoot != old && Path.GetFileName(replacement.PackageRoot).StartsWith(Path.GetFileName(old) + "-", StringComparison.Ordinal), "Leased digest was not published beside its copy");
    Check(Directory.Exists(old), "Active renderer lost its package"); leased.Dispose(); Check(!Directory.Exists(old), "Released copy was not cleaned");
}
static async Task ExtractorUpgrade()
{
    using var fixture = new Fixture(); string root;
    using (var first = fixture.Coordinator())
    {
        await first.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); Check(await first.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed");
        using var art = await first.ResolveArtworkAsync("06EC10", 0, "detail"); root = art!.PackageRoot;
    }
    File.Delete(Path.Combine(root, "package.json"));
    using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); coordinator.Changed += (_, _) => changed.TrySetResult();
    using (var served = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail")) Check(served?.PackageRoot == root, "Old extraction was not served while upgrading");
    await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var upgraded = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(File.Exists(Path.Combine(upgraded!.PackageRoot, "package.json")), "Old extraction was not re-indexed");
}
static async Task Redownload()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Configuration = Config(117006010); fixture.Handler.Artwork = ArtZip(1);
    Check(await coordinator.RefreshAsync(true) && await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Initial resources failed");
    string catalog = coordinator.Catalog.Sha256; int requests = fixture.Handler.Requests;
    var unchanged = await coordinator.RedownloadAsync([("06EC10", 0)]);
    Check(unchanged.Failed == 0 && !unchanged.CatalogChanged && fixture.Handler.Requests >= requests + 4 && coordinator.Catalog.Sha256 == catalog, "Re-download did not re-fetch equal content: " + unchanged);
    using var before = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    fixture.Handler.Failure = new HttpRequestException("offline");
    var failed = await coordinator.RedownloadAsync([("06EC10", 0)]);
    Check(failed.Failed > 0 && coordinator.LastError is not null && File.Exists(before!.FullPath), "Failed re-download removed working files: " + failed);
    using var after = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Eq(before!.PackageRoot, after!.PackageRoot);
    fixture.Handler.Failure = null;
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
    try { await coordinator.RedownloadAsync([]); throw new InvalidOperationException("Offline re-download was accepted"); }
    catch (InvalidOperationException error) when (error.Message.Contains("在线资源已关闭", StringComparison.Ordinal)) { }
}
static async Task StartupSweep()
{
    using var fixture = new Fixture(); string root, slot;
    using (var first = fixture.Coordinator())
    {
        await first.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); Check(await first.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed");
        using var art = await first.ResolveArtworkAsync("06EC10", 0, "detail"); root = art!.PackageRoot; slot = Path.GetDirectoryName(root)!;
    }
    string interrupted = Path.Combine(slot, ".download-" + Guid.NewGuid().ToString("N")), superseded = Path.Combine(slot, new string('a', 64));
    Directory.CreateDirectory(interrupted); Directory.CreateDirectory(superseded);
    using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    Check(!Directory.Exists(interrupted) && !Directory.Exists(superseded) && Directory.Exists(root), "Startup sweep removed the wrong directories");
}
static byte[] LottieZip()
{
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6yAAAAABJRU5ErkJggg==");
    static string Animation(string image) => "{\"v\":\"5.7.4\",\"fr\":30,\"ip\":0,\"op\":60,\"w\":64,\"h\":64,\"assets\":[" + image + "],\"layers\":[]}";
    var members = new Dictionary<string, byte[]>
    {
        ["config.json"] = Encoding.UTF8.GetBytes("{\"detailImageRes\":{\"path\":\"res/image/detail.png\"},\"lottieResList\":[{\"path\":\"res/lottie/missing.json\"},"
            + "{\"path\":\"res/lottie/main\",\"imagePath\":\"res/lottie/main/images\",\"type\":1},{\"path\":\"res/lottie/second.json\"}],"
            + "\"leftLottieResList\":[{\"path\":\"res/lottie/left.json\"}],\"discoveryStates\":{\"connectedState\":{\"bgVideoRes\":{\"path\":\"res/video/boot\"}}}}"),
        ["res/image/detail.png"] = png,
        ["res/lottie/main/data.json"] = Encoding.UTF8.GetBytes(Animation("{\"id\":\"img\",\"w\":1,\"h\":1,\"u\":\"\",\"p\":\"img_0.png\",\"e\":0}")),
        ["res/lottie/main/images/img_0.png"] = png,
        ["res/lottie/second.json"] = Encoding.UTF8.GetBytes(Animation("")),
        ["res/lottie/left.json"] = Encoding.UTF8.GetBytes(Animation("")),
    };
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        foreach (var (name, bytes) in members) using (var stream = archive.CreateEntry(name).Open()) stream.Write(bytes);
    return memory.ToArray();
}
static byte[] ModelZip(byte[] glb)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("config.json").Open())) writer.Write("{\"model3D\":{\"path\":\"res/raw/model.glb\"}}");
        using (var model = archive.CreateEntry("res/raw/model.glb").Open()) model.Write(glb);
    }
    return memory.ToArray();
}
static byte[] ModelWebpZip(byte[] webp)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("config.json").Open())) writer.Write("{\"modelWebp\":{\"path\":\"res/raw/detail_model.webp\"}}");
        using (var model = archive.CreateEntry("res/raw/detail_model.webp").Open()) model.Write(webp);
    }
    return memory.ToArray();
}
static byte[] ArtZip(int revision, string? unsafeName = null)
{
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6yAAAAABJRU5ErkJggg==");
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("config.json").Open())) writer.Write("{\"revision\":" + revision + ",\"detailImageRes\":{\"path\":\"res/image/detail.png\"}}");
        using (var image = archive.CreateEntry("res/image/detail.png").Open()) image.Write(png);
        if (unsafeName is not null) using (var image = archive.CreateEntry(unsafeName).Open()) image.Write(png);
    }
    return memory.ToArray();
}

sealed class Fixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetFullPath("artifacts/resource-tests"), Guid.NewGuid().ToString("N"));
    public FakeVendorHandler Handler { get; } = new();
    public ResourceCoordinator Coordinator() => new(Path, new HttpClient(Handler, false));
    public void Dispose()
    {
        string allowed = System.IO.Path.GetFullPath("artifacts/resource-tests") + System.IO.Path.DirectorySeparatorChar;
        if (!System.IO.Path.GetFullPath(Path).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup");
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
sealed class FakeVendorHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    public byte[] Configuration { get; set; } = Encoding.UTF8.GetBytes("{\"versionCode\":117006010,\"whiteList\":[]}");
    public string FileVersion { get; set; } = "initial";
    public byte[] Artwork { get; set; } = [];
    public byte[]? ModelArtwork { get; set; }
    public bool WrongHash { get; set; }
    public string? LastVerifiedKey { get; private set; }
    public TaskCompletionSource? Gate { get; set; }
    public Exception? Failure { get; set; }
    public int Pending => _pending;
    private int _pending;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++; byte[] bytes;
        if (Gate is { Task.IsCompleted: false } gate)
        {
            Interlocked.Increment(ref _pending);
            try { await gate.Task.WaitAsync(token); } finally { Interlocked.Decrement(ref _pending); }
        }
        if (Failure is { } failure) throw failure;
        if (request.Method == HttpMethod.Post)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(token); string signed = request.Headers.GetValues("sign").Single();
            if (signed == VendorResourceClient.Sign("test-key", body)) LastVerifiedKey = "test-key";
            CheckHeader(request, "appid", "earphone"); CheckHeader(request, "sv", "v1");
            if (request.RequestUri!.AbsolutePath.EndsWith("latestWhiteList"))
                bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { version = FileVersion, size = Configuration.Length.ToString(), downloadUrl = "https://iot-earbuds-cn.heytapimage.com/config" } });
            else
            {
                bool model = request.RequestUri.AbsolutePath.EndsWith("/fetch");
                string hash = WrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(model ? ModelArtwork ?? Artwork : Artwork));
                if (model) bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { sha256 = hash, resourceUrl = "https://iot-earbuds-cn.heytapimage.com/model-art", fileName = "model.zip" } });
                else bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { productId = "06EC10", sha256 = hash, cartoonUrl = "https://iot-earbuds-cn.heytapimage.com/art" } });
            }
        }
        else bytes = request.RequestUri!.AbsolutePath == "/config" ? Configuration : request.RequestUri.AbsolutePath == "/model-art" ? ModelArtwork ?? Artwork : Artwork;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
    }
    private static void CheckHeader(HttpRequestMessage request, string name, string value)
    { if (request.Headers.GetValues(name).Single() != value) throw new InvalidOperationException("Wrong vendor header"); }
}
