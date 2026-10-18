using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeyMelodyWindows.Resources;

public sealed record ResourceMetadata(string FileVersion, string DownloadUrl, long? Size);
public sealed record ArtworkMetadata(string Sha256, string DownloadUrl, string? FileName);

/// <summary>Both vendor channels sign the exact byte array subsequently sent.</summary>
public sealed class VendorResourceClient : IDisposable
{
    public static readonly Uri ApiBase = new("https://iot-earbuds-cn.heytapmobi.com/");
    private readonly HttpClient _client;
    private readonly bool _owned;
    public VendorResourceClient(HttpClient? client = null)
    {
        _owned = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        if (_owned) _client.Timeout = TimeSpan.FromSeconds(25);
    }

    public static int RequestCompatVersion() => 17006004;
    public static byte[] CreateRequestBody(string? productId = null, int? color = null, bool model = false)
    {
        var values = new Dictionary<string, string> { ["platform"] = "android", ["channel"] = "2", ["versionCode"] = RequestCompatVersion().ToString(CultureInfo.InvariantCulture) };
        if (productId is not null)
        { values["productId"] = productId; values["color"] = color?.ToString(CultureInfo.InvariantCulture) ?? throw new ArgumentException("Artwork requires a known protocol color."); }
        if (model) values["moduleId"] = "14";
        return JsonSerializer.SerializeToUtf8Bytes(values);
    }

    public static string Sign(string key, ReadOnlySpan<byte> body) => Convert.ToHexStringLower(HMACSHA1.HashData(new UTF8Encoding(false, true).GetBytes(key), body));

    public async Task<ResourceMetadata> GetConfigurationMetadataAsync(string key, CancellationToken token)
    {
        using var json = await PostAsync("v1/earphone/new/latestWhiteList", CreateRequestBody(), key, token).ConfigureAwait(false);
        JsonElement data = Data(json.RootElement);
        string version = RequiredScalar(data, "version"), url = RequiredString(data, "downloadUrl");
        long? size = data.TryGetProperty("size", out var sizeValue) && long.TryParse(Scalar(sizeValue), out long n) && n >= 0 ? n : null;
        ValidateDownloadUri(url);
        return new(version, url, size);
    }

    public async Task<ArtworkMetadata?> GetArtworkMetadataAsync(string key, string productId, int color, bool model, CancellationToken token)
    {
        using var json = await PostAsync(model ? "v1/earphone/resource/fetch" : "v1/earphone/resource/control", CreateRequestBody(productId, color, model), key, token).ConfigureAwait(false);
        JsonElement data = Data(json.RootElement);
        var rows = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : data.ValueKind == JsonValueKind.Object ? [data] : [];
        var matches = rows.Where(row => row.ValueKind == JsonValueKind.Object &&
            (row.TryGetProperty("productId", out var pid) ? pid.GetString() == productId : model && rows.Length == 1)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException("Ambiguous resource association.");
        data = matches[0];
        string url = RequiredString(data, model ? "resourceUrl" : "cartoonUrl"), hash = RequiredString(data, "sha256").ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid resource digest.");
        ValidateDownloadUri(url);
        return new(hash, url, data.TryGetProperty("fileName", out var name) ? name.GetString() : null);
    }

    public async Task<byte[]> DownloadAsync(string url, int limit, CancellationToken token)
    {
        Uri uri = ValidateDownloadUri(url);
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != uri) throw new InvalidDataException("Unexpected resource redirect.");
        return await BoundedReadAsync(response, limit, token).ConfigureAwait(false);
    }

    public static Uri ValidateDownloadUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Host is not ("iot-earbuds-cn.heytapimage.com" or "iot-heythings-cn.heytapimage.com")) throw new InvalidDataException("Unapproved resource location.");
        return uri;
    }

    private async Task<JsonDocument> PostAsync(string endpoint, byte[] body, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, endpoint)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("appid", "earphone"); request.Headers.Add("sv", "v1");
        request.Headers.Add("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("nonce", Guid.NewGuid().ToString()); request.Headers.Add("sign", Sign(key, body));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != request.RequestUri) throw new InvalidDataException("Unexpected metadata redirect.");
        var document = JsonDocument.Parse(await BoundedReadAsync(response, ResourceLimits.MetadataBytes, token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 64 });
        try { ResourceJson.ValidateUniqueProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static JsonElement Data(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int n) || n is not (0 or 200)) throw new InvalidDataException("Resource service rejected request.");
        return root.TryGetProperty("data", out var data) ? data : default;
    }
    private static string Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    private static string RequiredScalar(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) && Scalar(value) is { Length: > 0 } text ? text : throw new InvalidDataException("Missing resource metadata field.");
    private static string RequiredString(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : throw new InvalidDataException("Missing resource metadata field.");
    private static async Task<byte[]> BoundedReadAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Resource exceeds size limit.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(); byte[] buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (memory.Length + read > limit) throw new InvalidDataException("Resource exceeds size limit."); memory.Write(buffer, 0, read); }
        return memory.ToArray();
    }
    public void Dispose() { if (_owned) _client.Dispose(); }
}
