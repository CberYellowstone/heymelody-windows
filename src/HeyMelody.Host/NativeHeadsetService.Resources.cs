using System.IO.Compression;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Resources;

namespace HeyMelodyWindows.Host;

public sealed partial class NativeHeadsetService
{
    private ResourceCatalog? _catalog;
    private string _resourceSource = "HeyMelody", _stableKey = "";
    private long _resourceRevision, _generation, _appMarker = 17006004;
    private string? _observedProductId;
    private byte? _observedColor;
    internal void ConfigureResources(WorkerRequest request)
    {
        if (request.CatalogGzip is null) return;
        if (!Enum.TryParse<ResourceSource>(request.Source, out var source) || !Enum.IsDefined(source)) throw new InvalidDataException();
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(request.CatalogGzip)), CompressionMode.Decompress);
        using var target = new MemoryStream(); var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        { if (target.Length + read > 8 * 1024 * 1024) throw new InvalidDataException(); target.Write(buffer, 0, read); }
        _catalog = ResourceCatalog.Parse(target.ToArray(), source, false);
        _resourceSource = request.Source; _resourceRevision = request.ResourceRevision;
        _appMarker = request.AppMarker; _stableKey = request.StableKey; _generation = request.Generation;
        _observedProductId = request.ObservedProductId; _observedColor = request.ObservedColor;
    }
    private DailyClient CreateDailyClient(ulong address)
    {
        if (_catalog is null) return new(new PeerConnector(address, RecordSubmission));
        var services = _catalog.Profiles.Values.Where(p => p.CanUseControlTransport).Select(p => Guid.Parse(p.Uuid!)).Distinct().ToArray();
        return new(new PeerConnector(address, RecordSubmission, services), id => _catalog.Find(id) is { CanUseControlTransport: true } p
            ? DeviceProfile.FromResource(p.Raw, _resourceSource, _catalog.Sha256) : null, _appMarker, _observedProductId, _observedColor);
    }
    internal async Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token) =>
        _client is null ? throw new InvalidOperationException("耳机连接已变化。") : Map(await _client.SetObservedColorAsync(productId, color, token));
    private IReadOnlyList<UiAncMode> BuildAncModes(DailySnapshot snapshot)
    {
        var raw = _catalog?.Find(snapshot.ProductId)?.Functions;
        var definitions = raw is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("noiseReductionMode", out var modes)
            && modes.ValueKind == JsonValueKind.Array ? modes.EnumerateArray().ToArray() : [];
        return snapshot.AncOptions.GroupBy(x => x.Primary).Select(group =>
        {
            int? type = definitions.FirstOrDefault(x => x.TryGetProperty("protocolIndex", out var v) && v.GetInt32() == group.Key)
                is var d && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("modeType", out var mt) ? mt.GetInt32() : null;
            var (name, icon) = type switch { 5 => ("降噪", "anc"), 2 => ("通透", "transparency"),
                1 => ("关闭", "off"), 7 => ("智能降噪", "smart"), 10 => ("自适应降噪", "adaptive"),
                _ => (group.First().Name, "anc") };
            return new UiAncMode(group.Key, name, icon, group.Select(x => new UiOption((x.Primary << 8) | x.Level, x.Name)).ToArray());
        }).ToArray();
    }
    private IReadOnlyList<UiOption> SpatialOptions(DailySnapshot snapshot)
    {
        var functions = _catalog?.Find(snapshot.ProductId)?.Functions;
        int[] types = functions is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("spatialTypes", out var values)
            && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [0,1,2];
        return types.Where(type => type is >= 0 and <= 2 && (snapshot.Capabilities.SpatialRoute != "legacy" || type <= 1))
            .Distinct().Select(type => new UiOption(type, type == 0 ? "关闭" : type == 1 ? "固定空间音频" : "头部跟踪")).ToArray();
    }
}
