using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.Resources;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Both complete bundled catalogs load and exclude speakers", Bundles),
    ("Compatibility honors non-null zero and empty lists", Compatibility),
    ("Child expansion matches the official inheritance whitelist", Children),
    ("Whole-source selection rejects cross-source comparison", Selection),
    ("Duplicate JSON keys and malformed identifiers are rejected", InvalidCatalogs),
    ("HMAC signs the exact UTF-8 request including raw custom key", Signature),
    ("Cache envelope authenticates data and rejects tampering", Cache),
    ("Plaintext keys and offline preference survive restart", Preferences),
    ("Online update picks strictly higher content and deletes older online cache", Versions),
    ("Failed download keeps the effective catalog and cache", FailedUpdate),
    ("Online off avoids every request and reuses downloaded content", Offline),
    ("Unchanged metadata repairs a damaged cache without publishing equal content", CacheRepair),
    ("Art publication preserves leases and cleans retired online packages", ArtworkLeases),
    ("Hash failures and unsafe archives retain current art", UnsafeArtwork),
    ("Known color and source are required for model artwork", ArtworkIdentity),
    ("Model metadata without PID is bound to request and validates GLB", ModelPackages),
};
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
    var store = new BundledResourceStore(); var hey = store.GetCatalog(ResourceSource.HeyMelody); var oem = store.GetCatalog(ResourceSource.WirelessEarphones);
    Eq(85, hey.Profiles.Count); Eq(141, oem.Profiles.Count); Eq(117006004, hey.ContentVersion); Eq(17006002, oem.ContentVersion);
    Check(oem.Find("49074B") is null, "Speaker was included"); Check(oem.Find("065814") is not null, "Explicit child missing");
    Check(oem.Profiles.Values.Where(p => p.Brand == "realme").All(p => p.ExternalOnly), "External realme routes exposed as controls");
    Check(oem.Find("061010")?.SupportSpp == false, "No-SPP model guessed supported");
    Check(hey.Find("06EC10")!.CanUseControlTransport, "X4 transport missing");
    Check(store.Artwork.Count > 800, "Incomplete art inventory");
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
    Eq(24, store.GetDefaultKey(ResourceSource.HeyMelody).Length); Eq(24, store.GetDefaultKey(ResourceSource.WirelessEarphones).Length);
});
static Task Compatibility() => Done(() =>
{
    var json = Encoding.UTF8.GetBytes("{\"versionCode\":50,\"compatVersionCode\":0,\"whiteList\":[{\"id\":\"06EC10\",\"brand\":\"oppo\"}],\"compatWhiteList\":[],\"caseNumSite\":5,\"compatCaseNumSite\":0,\"appMarket\":\"a\",\"compatAppMarket\":\"\"}");
    var parsed = ResourceCatalog.Parse(json, ResourceSource.HeyMelody, true); Eq(0, parsed.ContentVersion); Eq(0, parsed.Profiles.Count);
    Eq(0, parsed.Raw.GetProperty("caseNumSite").GetInt32()); Eq("", parsed.Raw.GetProperty("appMarket").GetString());
    parsed = ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":50,\"compatVersionCode\":null,\"whiteList\":[],\"compatWhiteList\":null}"), ResourceSource.HeyMelody, true); Eq(50, parsed.ContentVersion);
});
static Task Children() => Done(() =>
{
    string list = "[{\"id\":\"060414\",\"name\":\"parent\",\"brand\":\"oneplus\",\"type\":\"T1\",\"supportSpp\":true,\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\",\"function\":{\"equalizer\":4},\"children\":[{\"name\":\"alias\"},{\"id\":\"060814\",\"name\":\"child\",\"uuid\":\"\",\"function\":{\"equalizer\":99},\"supportSpp\":false}]}]";
    var parsed = ResourceCatalog.Parse(Config(1, list), ResourceSource.HeyMelody, false); Eq(2, parsed.Profiles.Count);
    var child = parsed.Find("060814")!; Eq("child", child.Name); Check(child.SupportSpp, "Arbitrary child field replaced parent");
    Eq(4, child.Functions.GetProperty("equalizer").GetInt32()); Eq(1, child.Functions.GetProperty("hideFromUIList").GetInt32());
    Check(!child.Raw.TryGetProperty("children", out _), "Child kept nested children");
});
static Task Selection() => Done(() =>
{
    var bundled = ResourceCatalog.Parse(Config(5), ResourceSource.HeyMelody, false);
    foreach (int version in new[] { 3, 5, 6 })
    {
        var online = ResourceCatalog.Parse(Config(version), ResourceSource.HeyMelody, true);
        Check(ReferenceEquals(version > 5 ? online : bundled, ResourceCatalog.Select(bundled, online)), "Whole snapshot precedence wrong");
    }
    Throws<ArgumentException>(() => ResourceCatalog.Select(bundled, ResourceCatalog.Parse(Config(100), ResourceSource.WirelessEarphones, true)));
});
static Task InvalidCatalogs() => Done(() =>
{
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":1,\"versionCode\":2,\"whiteList\":[]}"), ResourceSource.HeyMelody, true));
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":true,\"whiteList\":[]}"), ResourceSource.HeyMelody, true));
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Config(1, "[{\"id\":\"06ec10\",\"brand\":\"oppo\"}]"), ResourceSource.HeyMelody, true));
    var profile = ResourceCatalog.Parse(Config(1, "[{\"id\":\"06EC10\",\"brand\":\"oppo\",\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\"}]"), ResourceSource.HeyMelody, true).Find("06EC10")!;
    Check(!profile.SupportSpp && !profile.CanUseControlTransport, "Missing SPP flag guessed true");
});
static Task Signature() => Done(() =>
{
    byte[] body = VendorResourceClient.CreateRequestBody(ResourceSource.HeyMelody);
    Eq("{\"platform\":\"android\",\"channel\":\"2\",\"versionCode\":\"17006004\"}", Encoding.UTF8.GetString(body));
    string rawKey = "明文-custom-key";
    using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(rawKey)); Eq(Convert.ToHexStringLower(hmac.ComputeHash(body)), VendorResourceClient.Sign(rawKey, body));
    var model = JsonDocument.Parse(VendorResourceClient.CreateRequestBody(ResourceSource.WirelessEarphones, "067410", 2, true)).RootElement;
    Eq("14", model.GetProperty("moduleId").GetString()); Eq("17006002", model.GetProperty("versionCode").GetString());
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
    Eq(ResourceSource.HeyMelody, first.Preferences.Source); Check(first.Preferences.UseOnlineResources, "Online should default on");
    string encryption = first.Preferences.CacheEncryptionKey;
    await first.UpdatePreferencesAsync(first.Preferences with { Source = ResourceSource.WirelessEarphones, UseOnlineResources = false, HeyMelodyKey = "user-hey", WirelessEarphonesKey = "user-wireless" });
    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Path, "preferences.json")));
    Eq("user-hey", saved.RootElement.GetProperty("HeyMelodyKey").GetString());
    Eq("user-wireless", saved.RootElement.GetProperty("WirelessEarphonesKey").GetString());
    Eq(encryption, saved.RootElement.GetProperty("CacheEncryptionKey").GetString());
    using var second = fixture.Coordinator(); await second.InitializeAsync(); Eq(encryption, second.Preferences.CacheEncryptionKey); Eq(ResourceSource.WirelessEarphones, second.Preferences.Source); Check(!second.Preferences.UseOnlineResources, "Offline preference lost"); Eq(0, fixture.Handler.Requests);
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
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { Source = ResourceSource.WirelessEarphones, UseOnlineResources = false }); Eq(17006002, coordinator.Catalog.ContentVersion);
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { Source = ResourceSource.HeyMelody }); Eq(117006011, coordinator.Catalog.ContentVersion);
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
    using var installed = coordinator.Bundled.Acquire(coordinator.Bundled.FindAsset(ResourceSource.HeyMelody, "06EC10", null, "detail")); Check(installed is not null && File.Exists(installed.FullPath), "Bundled art removed");
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
    var store = new BundledResourceStore(); Check(store.FindAsset(ResourceSource.HeyMelody, "068C10", 1, "model") is not null, "Verified Free4 model missing");
    Check(store.FindAsset(ResourceSource.HeyMelody, "068C10", null, "model") is null, "Model color guessed"); Check(store.FindAsset(ResourceSource.HeyMelody, "06C010", 1, "model") is null, "Free4 model applied to Dynaudio"); Check(store.FindAsset(ResourceSource.HeyMelody, "06EC10", 1, "model") is null, "Free4 model applied to X4");
    Check(store.FindAsset(ResourceSource.HeyMelody, "060410", 0, "detail") is null, "OEM-only downloaded art crossed source"); Check(store.FindAsset(ResourceSource.WirelessEarphones, "060410", 0, "detail") is not null, "Same-source art missing");
    foreach (string shape in new[] { "T1", "N", "O1" }) Check(store.FindGenericAsset(ResourceSource.HeyMelody, shape, "hero") is not null, "Generic shape fallback missing");
});
static async Task ModelPackages()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    var verified = coordinator.Bundled.FindAsset(ResourceSource.HeyMelody, "068C10", 1, "model")!;
    fixture.Handler.Artwork = ModelZip(File.ReadAllBytes(coordinator.Bundled.FullPath(verified)));
    Check(await coordinator.RefreshArtworkAsync("068C10", 1, model: true, force: true), "Valid self-contained model rejected");
    using var lease = await coordinator.ResolveArtworkAsync("068C10", 1, "model"); Check(lease is not null && lease.Kind == "model", "Request-only model association was lost"); Eq(verified.Sha256, lease!.Sha256);
    byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"https://external.example/geometry.bin\"}]} ");
    byte[] glb = new byte[20 + json.Length]; "glTF"u8.CopyTo(glb); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)glb.Length); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)json.Length); "JSON"u8.CopyTo(glb.AsSpan(16)); json.CopyTo(glb, 20);
    fixture.Handler.Artwork = ModelZip(glb); Check(!await coordinator.RefreshArtworkAsync("068C10", 1, model: true, force: true), "External model URI accepted"); Check(File.Exists(lease.FullPath), "Rejected model removed previous valid package");
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
    public bool WrongHash { get; set; }
    public string? LastVerifiedKey { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++; byte[] bytes;
        if (request.Method == HttpMethod.Post)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(token); string signed = request.Headers.GetValues("sign").Single();
            if (signed == VendorResourceClient.Sign("test-key", body)) LastVerifiedKey = "test-key";
            CheckHeader(request, "appid", "earphone"); CheckHeader(request, "sv", "v1");
            if (request.RequestUri!.AbsolutePath.EndsWith("latestWhiteList"))
                bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { version = FileVersion, size = Configuration.Length.ToString(), downloadUrl = "https://iot-earbuds-cn.heytapimage.com/config" } });
            else
            {
                string hash = WrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(Artwork));
                if (request.RequestUri.AbsolutePath.EndsWith("/fetch")) bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { sha256 = hash, resourceUrl = "https://iot-earbuds-cn.heytapimage.com/art", fileName = "model.zip" } });
                else bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { productId = "06EC10", sha256 = hash, cartoonUrl = "https://iot-earbuds-cn.heytapimage.com/art" } });
            }
        }
        else bytes = request.RequestUri!.AbsolutePath == "/config" ? Configuration : Artwork;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
    }
    private static void CheckHeader(HttpRequestMessage request, string name, string value)
    { if (request.Headers.GetValues(name).Single() != value) throw new InvalidOperationException("Wrong vendor header"); }
}
